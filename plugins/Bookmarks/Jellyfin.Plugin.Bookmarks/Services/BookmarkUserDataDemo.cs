using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// The developer demo of the kit's users and data services: a demo consumer from before Bookmarks'
/// own store became an <see cref="IUserDataOwner"/> with schema 2. It exposes the kit's
/// <see cref="UserReconciler"/>, <see cref="JsonStoreImport"/>, <see cref="ExportEnvelope"/> and the store's snapshot
/// restore on the demo store of <see cref="UserDataDemoOwner"/>, for <c>scripts/users-probe.mjs</c>.
/// </summary>
internal sealed class BookmarkUserDataDemo : IBookmarkUserDataDemo
{
    /// <summary>The largest demo record text.</summary>
    public const int MaxTextLength = 200;

    private const string FileStem = "bookmarks-userdata-demo";

    private readonly IPluginConfigurationProvider _configuration;
    private readonly UserDataDemoOwner _owner;
    private readonly UserReconciler _reconciler;
    private readonly IUserManager _userManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly UserDataDemoImportAdapter _adapter = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkUserDataDemo"/> class.
    /// </summary>
    /// <param name="configuration">The plugin settings.</param>
    /// <param name="owner">The demo store.</param>
    /// <param name="reconciler">The kit's reconciler.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="applicationHost">The server, for its id in exports.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    public BookmarkUserDataDemo(
        IPluginConfigurationProvider configuration,
        UserDataDemoOwner owner,
        UserReconciler reconciler,
        IUserManager userManager,
        IServerApplicationHost applicationHost,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _owner = owner;
        _reconciler = reconciler;
        _userManager = userManager;
        _applicationHost = applicationHost;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public IDictionary<string, object?> GetRecords()
    {
        EnsureEnabled();
        var document = _owner.Store.Current;
        var server = ServerUsers();
        var known = document.KnownUsers ?? KnownUsers.Empty;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Store"] = KitDiagnostics.DescribeStore(_owner.Store),
            ["KnownUsers"] = known.ToDictionary(k => KnownUsers.FormatId(k.Key), k => (object?)k.Value, StringComparer.Ordinal),
            ["Records"] = document.Records.Select(r => Describe(r, server, known)).ToList()
        };
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> AddRecordAsync(JsonElement body, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("UserId", out var userIdValue)
            || userIdValue.ValueKind != JsonValueKind.String
            || !Guid.TryParse(userIdValue.GetString(), out var userId))
        {
            throw PluginException.Invalid("The body must be {UserId, Text, HasFile} with UserId a user id.");
        }

        var text = body.TryGetProperty("Text", out var textValue) && textValue.ValueKind == JsonValueKind.String ? textValue.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength)
        {
            throw PluginException.Invalid($"Text must be 1 to {MaxTextLength} characters.");
        }

        var hasFile = body.TryGetProperty("HasFile", out var fileValue) && fileValue.ValueKind == JsonValueKind.True;
        var user = KitUsers.Find(_userManager, userId) ?? throw PluginException.Invalid("No user of this server has this id.");
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var record = new UserDataDemoRecord { Id = Guid.NewGuid(), UserId = user.Id, Text = text, HasFile = hasFile, CreatedAt = now, UpdatedAt = now };

        // A write records the writer's name ("refreshed on writes").
        var document = await _owner.Store.UpdateAsync(
            d => UserDataDemoOwner.WithRecords(d, [.. d.Records, record]) with { KnownUsers = (d.KnownUsers ?? KnownUsers.Empty).With(user) },
            cancellationToken).ConfigureAwait(false);
        return Describe(record, ServerUsers(), document.KnownUsers);
    }

    /// <inheritdoc />
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        EnsureEnabled();
        await _owner.Store.UpdateAsync(_ => UserDataDemoDocument.CreateEmpty(), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("The user data demo store was emptied");
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> ReconcileAsync(CancellationToken cancellationToken)
    {
        EnsureEnabled();
        return (await _reconciler.ReconcileAsync(cancellationToken).ConfigureAwait(false)).ToDictionary();
    }

    /// <inheritdoc />
    public IDictionary<string, object?> GetUsers()
    {
        EnsureEnabled();
        return _reconciler.Describe();
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> RemapUsersAsync(string? by, bool? dryRun, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (!string.IsNullOrWhiteSpace(by) && !string.Equals(by.Trim(), "Name", StringComparison.OrdinalIgnoreCase))
        {
            throw PluginException.Invalid("by must be Name.");
        }

        return (await _reconciler.RemapByNameAsync(dryRun ?? true, cancellationToken).ConfigureAwait(false)).ToDictionary();
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> ApplyPolicyAsync(JsonElement body, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var ids = new List<Guid>();
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("UserIds", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            throw PluginException.Invalid("The body must be {UserIds: [...]}.");
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var id))
            {
                throw PluginException.Invalid("Every entry of UserIds must be a user id.");
            }

            ids.Add(id);
        }

        return (await _reconciler.ApplyPolicyAsync(ids, cancellationToken).ConfigureAwait(false)).ToDictionary();
    }

    /// <inheritdoc />
    public IActionResult Export(HttpContext context)
    {
        EnsureEnabled();

        // The data first: it refuses (503) a store whose upgrade failed, and passes on a newer version's file as it is.
        var data = _owner.Store.ExportData();
        var document = _owner.Store.Current;
        var envelope = ExportEnvelope.Create(
            BookmarksKit.Options,
            data,
            ExportUser.For(UserDataDemoOwner.CountRecordsByUser(document).Keys, document.KnownUsers ?? KnownUsers.Empty, ServerUsers(), _reconciler.DeletedUserIds),
            _configuration.GetConfiguration(),
            archive: null,
            _applicationHost.SystemId,
            _timeProvider.GetUtcNow().UtcDateTime);
        _logger.LogInformation("The user data demo was exported ({Records} records)", document.Records.Count);
        return envelope.ToFileResult(context, FileStem);
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> ImportAsync(HttpRequest request, string? mode, bool? dryRun, string? mapUsersBy)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEnabled();
        var parsed = ImportRequest.Parse(mode, dryRun, mapUsersBy);
        var cancellationToken = request.HttpContext.RequestAborted;
        var envelope = await ExportEnvelope.ReadAsync(request, cancellationToken).ConfigureAwait(false);
        var report = await JsonStoreImport.RunAsync(
            _owner.Store,
            envelope,
            parsed,
            _adapter,
            BookmarksKit.Options,
            ServerUsers(),
            _reconciler.DeletedUserIds,
            _owner.SnapshotDirectory,
            _logger,
            cancellationToken).ConfigureAwait(false);
        if (!parsed.DryRun)
        {
            await _reconciler.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }

        return report.ToDictionary();
    }

    /// <inheritdoc />
    public IDictionary<string, object?> GetSnapshots()
    {
        EnsureEnabled();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["KeepSnapshotsBeforeUtc"] = _owner.Store.KeepSnapshotsBeforeUtc,
            ["Snapshots"] = _owner.Store.ListSnapshots(_owner.SnapshotDirectory).Select(s => s.ToDictionary()).ToList()
        };
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> TakeSnapshotAsync(int? keep, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var count = keep ?? 7;
        if (count is < 1 or > 100)
        {
            throw PluginException.Invalid("keep must be 1 to 100.");
        }

        var path = await _owner.Store.SnapshotAsync(_owner.SnapshotDirectory, count, cancellationToken).ConfigureAwait(false);
        var result = GetSnapshots();
        result["Snapshot"] = path is null ? null : System.IO.Path.GetFileName(path);
        return result;
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> RestoreSnapshotAsync(string? name, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw PluginException.Invalid("name is required.");
        }

        var restored = await _owner.Store.RestoreSnapshotAsync(_owner.SnapshotDirectory, name, cancellationToken).ConfigureAwait(false);

        // After a restore comes the verify sweep; the demo has no library items, so the reconciler takes its place.
        await _reconciler.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        var result = restored.ToDictionary();
        result["Records"] = _owner.Store.Current.Records.Count;
        return result;
    }

    private static IDictionary<string, object?> Describe(UserDataDemoRecord record, IReadOnlyDictionary<Guid, string> server, KnownUsers known) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Id"] = record.Id,
        ["UserId"] = KnownUsers.FormatId(record.UserId),
        ["UserName"] = server.GetValueOrDefault(record.UserId) ?? known.NameOf(record.UserId),
        ["UserKnown"] = server.ContainsKey(record.UserId),
        ["Text"] = record.Text,
        ["HasFile"] = record.HasFile,
        ["DeletedUtc"] = record.DeletedUtc,
        ["CreatedAt"] = record.CreatedAt,
        ["UpdatedAt"] = record.UpdatedAt
    };

    private Dictionary<Guid, string> ServerUsers()
        => KitUsers.GetAll(_userManager).GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First().Username);

    private void EnsureEnabled()
    {
        if (!_owner.IsEnabled)
        {
            throw PluginException.FeatureDisabled("The user data demo is turned off (developer setting EnableUserDataDemo).");
        }
    }
}
