using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.LibraryMaintenance.Api;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Normalization;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ItemVisibility = Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups.ItemVisibility;

namespace Jellyfin.Plugin.LibraryMaintenance.Settings;

/// <summary>
/// The Settings tab's server side: the settings and what their fields offer, a save, and the settings'
/// export and import in the kit's envelope.
/// </summary>
/// <remarks>
/// <para>Every change is a dry run unless it says otherwise (<c>dryRun=false</c>), and answers what it changes. A change to
/// the index's scope (the included libraries, the excluded folders) is previewed: a build with the new settings, which only
/// reads the library (about as long as the index's own build: 0.4 to 0.9 s at 10, 000 movies), gives the movies the
/// index would hold, beside the ones it holds now. The Settings tab shows that preview and saves only when the
/// administrator confirms it; a change that leaves the scope alone is saved at once.</para>
/// <para>A save carries the version of the settings it was made from, and is refused (409) when they were saved since, by
/// another administrator or the Dashboard's own form.</para>
/// <para>The preview's counts cover every library and every movie in them, so an administrator who can't open every
/// movie library, or who has a parental rating or tag limit, gets the libraries and folders that change without them
/// (as Diagnostics does).</para>
/// </remarks>
internal sealed class SettingsService : ILibraryMaintenanceSettings, IDisposable
{
    /// <summary>The schema version of the export's <c>Data</c>: 1.0 has no data of its own (an empty object).</summary>
    public const int SchemaVersion = 1;

    /// <summary>The <c>code</c> of the 409 of a save or import over settings saved since its version (the third review:
    /// the page tells the two 409s apart by it, never by their words).</summary>
    public const string SettingsChangedCode = "SettingsChanged";

    /// <summary>The <c>code</c> of the 409 of an import of a file a newer version wrote (its schema).</summary>
    public const string NewerSchemaCode = "NewerSchema";

    /// <summary>The 413 of an import file over <see cref="LibraryMaintenanceAdminController.MaxImportBytes"/>.</summary>
    public const string TooLargeImport = "An import file is at most 1 MB.";

    /// <summary>The <c>NotChecked</c> of a network folder: never opened before the administrator saves.</summary>
    internal const string NotCheckedNetwork = "Network";

    /// <summary>The <c>NotChecked</c> of a folder whose check didn't answer within <see cref="FolderCheckTimeout"/>.</summary>
    internal const string NotCheckedNoAnswer = "NoAnswer";

    /// <summary>
    /// The <c>NotChecked</c> of a device path (<c>\\?\</c>, <c>\\.\</c>, <c>\??\</c>) in the saved settings: never
    /// opened, since <c>\\?\UNC\host\share</c> or <c>\??\UNC\host\share</c> would connect to the host and <c>\\.\</c>
    /// names a device or a pipe (the 1.0 verifier).
    /// </summary>
    internal const string NotCheckedDevice = "Device";

    private const string HiddenCounts = "The movies aren't counted: these numbers cover every movie library, and your access is limited to some of them or by a parental rating or tags.";

    // The preview's build failed: its exception goes to the log, never to the page (the third review).
    private const string NotCounted = "The movies couldn't be counted: the preview's read of the library failed. The server's log has the details.";

    // An import's report names at most this many of the file's settings this version doesn't have, each cut at
    // MaxEchoedKeyLength characters (the third review: a 1 MB file can hold thousands).
    private const int MaxEchoedKeys = 50;
    private const int MaxEchoedKeyLength = 80;

    // An export's Settings: names in any case, ids in any of Guid's formats (the export writes "D"; Jellyfin's own JSON "N").
    private static readonly JsonSerializerOptions ImportJson = new() { PropertyNameCaseInsensitive = true, Converters = { new AnyGuidConverter() } };

    private readonly IPluginConfigurationProvider _configuration;
    private readonly MovieIndexBuilder _builder;
    private readonly MovieIndexService _index;
    private readonly LibraryAccess _access;
    private readonly IServerApplicationHost _host;
    private readonly ILogger<SettingsService> _logger;
    private readonly SemaphoreSlim _previews = new(1, 1);

    // One save or import at a time, from its version check to its save: a scope change's preview runs between the two
    // (0.4 to 0.8 s at 10, 000 movies), and a second save checked meanwhile would pass the check and be overwritten.
    private readonly SemaphoreSlim _saves = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsService"/> class.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <param name="builder">The index's builder: the server's libraries, and the preview's build.</param>
    /// <param name="index">The movie index: the counts before a change.</param>
    /// <param name="access">The caller's libraries.</param>
    /// <param name="host">The server, for its id in an export.</param>
    /// <param name="logger">The logger.</param>
    public SettingsService(IPluginConfigurationProvider configuration, MovieIndexBuilder builder, MovieIndexService index, LibraryAccess access, IServerApplicationHost host, ILogger<SettingsService> logger)
    {
        _configuration = configuration;
        _builder = builder;
        _index = index;
        _access = access;
        _host = host;
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets how long a preview waits for its local folders' checks, all run at once (the third review: an
    /// unreachable disk can take much longer); a folder not answered by then is "not checked".
    /// </summary>
    internal TimeSpan FolderCheckTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets how a local folder is looked for (<see cref="Directory.Exists(string)"/>; a test's seam).</summary>
    internal Func<string, bool> FolderExists { get; set; } = Directory.Exists;

    /// <inheritdoc />
    public LibraryMaintenanceSettingsViewDto Read()
    {
        var configuration = _configuration.GetConfiguration();
        var included = (configuration.IncludedLibraryIds ?? []).Where(id => id != Guid.Empty).ToHashSet();
        var libraries = _builder.MovieLibraries();
        var known = libraries.Select(l => l.Id).ToHashSet();
        var offered = libraries.Select(l => new LibraryMaintenanceSettingsLibraryDto
        {
            Id = l.Id,
            Name = l.Name ?? string.Empty,
            Kind = l.CollectionType is null ? "mixed" : "movies",
            Enabled = _builder.IsLibraryEnabled(l),
            Included = included.Contains(l.Id)
        }).ToList();
        offered.AddRange((configuration.IncludedLibraryIds ?? []).Where(id => id != Guid.Empty && !known.Contains(id)).Distinct()
            .Select(id => new LibraryMaintenanceSettingsLibraryDto { Id = id, Included = true, Missing = true }));
        return new LibraryMaintenanceSettingsViewDto
        {
            Settings = SettingsRules.ToDto(configuration),
            Version = SettingsRules.Version(configuration),
            Libraries = offered,
            ArticlesByLanguage = NormalizationOptions.ArticlesByLanguage.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            DefaultEditionPhrases = [.. NormalizationOptions.DefaultEditionPhrases]
        };
    }

    /// <inheritdoc />
    /// <exception cref="PluginException">400 for a value it can't use; 409 when the settings were saved since.</exception>
    public Task<LibraryMaintenanceSettingsReportDto> ChangeAsync(LibraryMaintenanceSettingsDto change, string? basedOn, bool dryRun, User? caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        return ChangeAsync(change, basedOn, dryRun, caller, lenient: false, [], null, cancellationToken);
    }

    /// <summary>
    /// Exports the settings (<c>GET /Admin/Export</c>): the kit's envelope, its <c>Settings</c> the Settings tab's saved
    /// fields (<see cref="SettingsRules.Fields"/>: the ten duplicate-finding fields and the web client's and Move's
    /// switches; not the other Move fields, which the tab doesn't show yet) and its <c>Data</c> empty (the plugin
    /// keeps no data; the ignore marks join it later).
    /// </summary>
    /// <returns>The envelope.</returns>
    public ExportEnvelope CreateExport()
        => ExportEnvelope.Create(
            LibraryMaintenanceKit.Options,
            new StoreExportData(SchemaVersion, new JsonObject()),
            [],
            settings: SettingsRules.ToDto(_configuration.GetConfiguration()),
            serverId: _host.SystemId);

    /// <summary>
    /// Imports the settings of an export (<c>POST /Admin/Import</c>): as a change, but lenient (a value this server can't
    /// use is a warning), with where the file came from. The kit's own import never applies an envelope's settings;
    /// this one does, only the Settings tab's fields (<see cref="SettingsRules.Fields"/>), and only after the
    /// administrator saw the dry run's report.
    /// </summary>
    /// <param name="envelope">The export.</param>
    /// <param name="basedOn">The version the import was checked against: a save needs it; a dry run without it isn't
    /// checked.</param>
    /// <param name="dryRun">Whether nothing is saved.</param>
    /// <param name="caller">The administrator, or null for an API key.</param>
    /// <param name="cancellationToken">Cancels the preview.</param>
    /// <returns>The report.</returns>
    /// <exception cref="PluginException">400 another plugin's file or settings it can't read; 409 a newer schema, or the
    /// settings saved since.</exception>
    public Task<LibraryMaintenanceSettingsReportDto> ImportEnvelopeAsync(ExportEnvelope envelope, string? basedOn, bool dryRun, User? caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        try
        {
            envelope.EnsureFor(LibraryMaintenanceKit.Options, SchemaVersion);
        }
        catch (PluginException ex) when (ex.Kind == PluginErrorKind.Conflict)
        {
            // The kit's 409 for a newer schema: the same words, with the code the page tells it apart by.
            throw new PluginException(PluginErrorKind.Conflict, ex.Message, ex) { Code = NewerSchemaCode };
        }

        if (envelope.Settings is not { } settings)
        {
            throw PluginException.Invalid("The file has no Settings to import.");
        }

        LibraryMaintenanceSettingsDto change;
        try
        {
            change = settings.Deserialize<LibraryMaintenanceSettingsDto>(ImportJson) ?? new LibraryMaintenanceSettingsDto();
        }
        catch (JsonException ex)
        {
            throw new PluginException(PluginErrorKind.Invalid, "The file's Settings can't be read" + (ex.Path is { Length: > 0 } path ? " at " + path.TrimStart('$', '.') : string.Empty) + ".", ex);
        }

        var warnings = new List<string>();
        var known = SettingsRules.Fields.Select(f => f.Field).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var other = settings.Select(p => p.Key).Where(k => !known.Contains(k)).Order(StringComparer.Ordinal).ToList();
        if (other.Count > 0)
        {
            warnings.Add(
                "The file has settings this version doesn't have, which are left out: "
                + string.Join(", ", other.Take(MaxEchoedKeys).Select(k => k.Length > MaxEchoedKeyLength ? k[..MaxEchoedKeyLength] + "…" : k))
                + (other.Count > MaxEchoedKeys ? ", and " + (other.Count - MaxEchoedKeys).ToString("N0", CultureInfo.InvariantCulture) + " more" : string.Empty)
                + ".");
        }

        var missing = SettingsRules.Fields.Where(f => !settings.Any(p => string.Equals(p.Key, f.Field, StringComparison.OrdinalIgnoreCase))).Select(f => f.Label).ToList();
        if (missing.Count > 0)
        {
            warnings.Add("The file doesn't have these settings, which keep their saved values: " + string.Join(", ", missing) + ".");
        }

        // A setting the file has with no value (null) is left out too, which the report says (the third review).
        var empty = SettingsRules.Fields.Where(f => settings.Any(p => p.Value is null && string.Equals(p.Key, f.Field, StringComparison.OrdinalIgnoreCase))).Select(f => f.Label).ToList();
        if (empty.Count > 0)
        {
            warnings.Add("The file has no value (null) for these settings, which keep their saved values: " + string.Join(", ", empty) + ".");
        }

        var source = new LibraryMaintenanceSettingsSourceDto
        {
            PluginVersion = envelope.PluginVersion,
            ExportedUtc = envelope.ExportedUtc,
            SameServer = envelope.ServerId is null ? null : string.Equals(envelope.ServerId, _host.SystemId, StringComparison.OrdinalIgnoreCase)
        };
        if (source.SameServer == false)
        {
            warnings.Add("The file is from another server: its libraries and folders are this server's only where their ids and paths are the same.");
        }

        return ChangeAsync(change, basedOn, dryRun, caller, lenient: true, warnings, source, cancellationToken);
    }

    /// <inheritdoc />
    public IActionResult Export(HttpContext context) => CreateExport().ToFileResult(context, LibraryMaintenanceKit.Options.DataFolder);

    /// <inheritdoc />
    public async Task<LibraryMaintenanceSettingsReportDto> ImportAsync(HttpRequest request, string? basedOn, bool dryRun, User? caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExportEnvelope envelope;
        try
        {
            envelope = await ExportEnvelope.ReadAsync(request, cancellationToken, LibraryMaintenanceAdminController.MaxImportBytes).ConfigureAwait(false);
        }
        catch (PluginException ex) when (ex.Kind == PluginErrorKind.PayloadTooLarge)
        {
            // The kit's words name its own default (100 MB).
            throw new PluginException(PluginErrorKind.PayloadTooLarge, TooLargeImport, ex);
        }

        return await ImportEnvelopeAsync(envelope, basedOn, dryRun, caller, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _previews.Dispose();
        _saves.Dispose();
    }

    // A drive letter Windows maps to a network share: asked of Windows, which knows its mappings without connecting.
    private static bool IsMappedNetworkDrive(string path)
    {
        var root = OperatingSystem.IsWindows() ? Path.GetPathRoot(path) : null;
        if (root is null || root.Length < 2 || root[1] != ':')
        {
            return false;
        }

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // Whether each folder exists (the third review). A network folder, a UNC path or a drive mapped to a share, is never
    // opened: a dry run (or a save) connects to no server. Nor is a device path (the 1.0 verifier): the settings refuse a
    // new one, but one saved before that rule or through Jellyfin's own configuration route reaches this check when a
    // preview removes it, and \\?\UNC\host\share or \??\UNC\host\share would make an SMB connection. A
    // saved folder isn't trimmed, so its form is read trimmed, as the settings' rule reads it. The local ones are checked
    // at once, within FolderCheckTimeout for them all; one not answered by then is "not checked".
    private async Task<Dictionary<string, (bool? Exists, string? NotChecked)>> CheckFoldersAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, (bool? Exists, string? NotChecked)>(StringComparer.Ordinal);
        var checks = new Dictionary<string, Task<bool?>>(StringComparer.Ordinal);
        var exists = FolderExists;
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            var form = path.Trim();
            if (SettingsRules.IsNetworkPath(form))
            {
                results[path] = (null, NotCheckedNetwork);
                continue;
            }

            if (SettingsRules.IsDevicePath(form))
            {
                results[path] = (null, NotCheckedDevice);
                continue;
            }

            checks[path] = Task.Run<bool?>(() => IsMappedNetworkDrive(path) ? null : exists(path), cancellationToken);
        }

        if (checks.Count > 0)
        {
            await Task.WhenAny(Task.WhenAll(checks.Values), Task.Delay(FolderCheckTimeout, cancellationToken)).ConfigureAwait(false);
        }

        foreach (var (path, check) in checks)
        {
            if (!check.IsCompletedSuccessfully)
            {
                results[path] = (null, NotCheckedNoAnswer);
                continue;
            }

            var found = await check.ConfigureAwait(false);
            results[path] = found is { } isThere ? (isThere, null) : (null, NotCheckedNetwork);
        }

        return results;
    }

    private async Task<LibraryMaintenanceSettingsReportDto> ChangeAsync(LibraryMaintenanceSettingsDto change, string? basedOn, bool dryRun, User? caller, bool lenient, List<string> warnings, LibraryMaintenanceSettingsSourceDto? source, CancellationToken cancellationToken)
    {
        if (!dryRun && string.IsNullOrWhiteSpace(basedOn))
        {
            // A save is checked against the settings it was made from (the third review): one without its version could
            // overwrite another administrator's save unseen.
            throw PluginException.Invalid("A save needs the version of the settings it was made from: read them (GET /LibraryMaintenance/Admin/Settings) and send their Version as version.");
        }

        if (dryRun)
        {
            return await CheckAndChangeAsync(change, basedOn, dryRun, caller, lenient, warnings, source, cancellationToken).ConfigureAwait(false);
        }

        await _saves.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CheckAndChangeAsync(change, basedOn, dryRun, caller, lenient, warnings, source, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saves.Release();
        }
    }

    private async Task<LibraryMaintenanceSettingsReportDto> CheckAndChangeAsync(LibraryMaintenanceSettingsDto change, string? basedOn, bool dryRun, User? caller, bool lenient, List<string> warnings, LibraryMaintenanceSettingsSourceDto? source, CancellationToken cancellationToken)
    {
        var current = _configuration.GetConfiguration();
        var version = SettingsRules.Version(current);
        if (!string.IsNullOrWhiteSpace(basedOn) && !string.Equals(basedOn.Trim(), version, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginException(PluginErrorKind.Conflict, "The settings were saved since you opened them (by someone else, or in another window). Reload them, then make your change again.") { Code = SettingsChangedCode };
        }

        var libraries = _builder.MovieLibraries();
        var names = libraries.GroupBy(l => l.Id).ToDictionary(g => g.Key, g => g.First().Name ?? string.Empty);
        var proposed = SettingsRules.Apply(current, change, names.Keys, lenient, warnings);
        var changes = SettingsRules.Diff(current, proposed, id => names.TryGetValue(id, out var name) ? name : null);
        var scope = SettingsRules.ScopeDiffers(current, proposed);
        var report = new LibraryMaintenanceSettingsReportDto
        {
            DryRun = dryRun,
            Version = version,
            Settings = SettingsRules.ToDto(proposed),
            Changes = changes,
            ScopeChanged = scope,
            RebuildsIndex = proposed.DuplicatesEnabled && (scope || !current.DuplicatesEnabled),
            TitleRulesChanged = SettingsRules.TitleRulesDiffer(current, proposed),
            Warnings = warnings,
            Source = source
        };
        if (scope)
        {
            report.Preview = await PreviewAsync(current, proposed, caller, cancellationToken).ConfigureAwait(false);
        }

        if (!dryRun && changes.Count > 0)
        {
            _configuration.Save(proposed);
            report.Applied = true;
            report.Version = SettingsRules.Version(_configuration.GetConfiguration());
            _logger.LogInformation(
                "Library Maintenance's settings were {How}: {Fields}",
                source is null ? "saved" : "imported",
                string.Join(", ", changes.Select(c => c.Field)));
        }

        return report;
    }

    private async Task<LibraryMaintenanceSettingsPreviewDto> PreviewAsync(PluginConfiguration current, PluginConfiguration proposed, User? caller, CancellationToken cancellationToken)
    {
        var server = _builder.MovieLibraries();
        var visible = caller is null ? null : _access.VisibleLibraries(caller).Select(l => l.Id).ToHashSet();
        var counted = caller is null || (server.All(l => visible!.Contains(l.Id)) && !ItemVisibility.HasItemRestrictions(caller));
        var snapshot = _index.Current;
        var preview = new LibraryMaintenanceSettingsPreviewDto
        {
            Counted = counted,
            Note = counted ? null : HiddenCounts,
            IndexBuiltUtc = snapshot?.GeneratedUtc
        };

        MovieIndexContent? after = null;
        if (counted)
        {
            await _previews.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                after = await Task.Run(() => _builder.Build(proposed, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Library Maintenance couldn't preview the settings' libraries and folders");
                preview.Note = NotCounted;
            }
            finally
            {
                _previews.Release();
            }
        }

        var beforeRead = snapshot?.Libraries.ToDictionary(l => l.Id) ?? _builder.IncludedLibraries(current).ToDictionary(l => l.Id, l => new IndexLibrary(l.Id, l.Name ?? string.Empty, string.Empty, 0));
        var afterRead = after?.Libraries.ToDictionary(l => l.Id) ?? _builder.IncludedLibraries(proposed).ToDictionary(l => l.Id, l => new IndexLibrary(l.Id, l.Name ?? string.Empty, string.Empty, 0));
        var withCounts = counted && after is not null;
        preview.MoviesBefore = withCounts && snapshot is not null ? snapshot.Stats.Movies : null;
        preview.ExcludedBefore = withCounts && snapshot is not null ? snapshot.Stats.Excluded : null;
        preview.MoviesAfter = withCounts ? after!.Stats.Movies : null;
        preview.ExcludedAfter = withCounts ? after!.Stats.Excluded : null;
        preview.Libraries = server.Select(l => l.Id).Concat(beforeRead.Keys).Distinct()
            .Where(id => beforeRead.ContainsKey(id) || afterRead.ContainsKey(id))
            .Select(id =>
            {
                var was = beforeRead.GetValueOrDefault(id);
                var will = afterRead.GetValueOrDefault(id);
                return new LibraryMaintenanceSettingsPreviewLibraryDto
                {
                    Id = id,
                    Name = (will ?? was)!.Name,
                    Change = was is null ? "Joins" : will is null ? "Leaves" : "Stays",
                    MoviesBefore = withCounts && snapshot is not null && was is not null ? was.Movies : null,
                    MoviesAfter = withCounts && will is not null ? will.Movies : null
                };
            })
            .ToList();

        var saved = (current.ExcludedPathPrefixes ?? []).GroupBy(SettingsRules.FolderKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var next = (proposed.ExcludedPathPrefixes ?? []).GroupBy(SettingsRules.FolderKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var changed = next.Where(p => !saved.ContainsKey(p.Key)).Select(p => (Path: p.Value, Change: "Added"))
            .Concat(saved.Where(p => !next.ContainsKey(p.Key)).Select(p => (Path: p.Value, Change: "Removed")))
            .ToList();
        var checks = await CheckFoldersAsync(changed.Select(c => c.Path), cancellationToken).ConfigureAwait(false);
        preview.Folders = changed.Select(c => new LibraryMaintenanceSettingsPreviewFolderDto
        {
            Path = c.Path,
            Change = c.Change,
            Exists = checks[c.Path].Exists,
            NotChecked = checks[c.Path].NotChecked
        }).ToList();
        return preview;
    }
}
