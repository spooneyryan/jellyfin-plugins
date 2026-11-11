using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Normalization;

namespace Jellyfin.Plugin.LibraryMaintenance.Settings;

/// <summary>
/// The settings' rules, pure: what a save or an import may write, each field's text, what changed, and the
/// settings' version.
/// </summary>
/// <remarks>
/// A value this version doesn't know (a match mode, a policy or a language a newer build wrote) is kept as it is when the
/// change leaves it alone, and is never written by a change. A save refuses anything else it can't use (400, naming the
/// field); an import is lenient: it keeps the saved value, or leaves the entry out, and says so in a warning, since a
/// file from another server or version is expected to hold some of them (a path from another server must not
/// silently become this server's setting).
/// </remarks>
internal static class SettingsRules
{
    /// <summary>The most excluded folders.</summary>
    public const int MaxFolders = 100;

    /// <summary>The longest excluded folder.</summary>
    public const int MaxPathLength = 1024;

    /// <summary>The most edition phrases.</summary>
    public const int MaxPhrases = 200;

    /// <summary>The longest edition phrase.</summary>
    public const int MaxPhraseLength = 100;

    /// <summary>The longest wait for a build, in seconds.</summary>
    public const int MaxRequestWaitSeconds = 30;

    /// <summary>The match modes.</summary>
    public static readonly IReadOnlyList<string> MatchModes = ["TitleAndYear", "Title"];

    /// <summary>The unknown-year policies.</summary>
    public static readonly IReadOnlyList<string> UnknownYearPolicies = ["MatchUnknownOnly", "MatchAny"];

    /// <summary>
    /// The Settings tab's fields, in its order, with their labels: the ten duplicate-finding fields of 1.0.0 and 1.0.1,
    /// then the web client's and Move's switches (since 1.0.2), which the tab, the export and
    /// the import carry too.
    /// </summary>
    public static readonly IReadOnlyList<(string Field, string Label)> Fields =
    [
        (nameof(PluginConfiguration.DuplicatesEnabled), "Find duplicate movies"),
        (nameof(PluginConfiguration.DefaultMatchMode), "Default match mode"),
        (nameof(PluginConfiguration.UnknownYearPolicy), "A movie with no year"),
        (nameof(PluginConfiguration.IncludedLibraryIds), "Libraries"),
        (nameof(PluginConfiguration.ExcludedPathPrefixes), "Excluded folders"),
        (nameof(PluginConfiguration.StripEditionSuffixes), "Cut edition suffixes"),
        (nameof(PluginConfiguration.EditionPhrases), "Edition phrases"),
        (nameof(PluginConfiguration.ArticleLanguages), "Leading articles dropped"),
        (nameof(PluginConfiguration.WarmIndexAfterScan), "Build the index after every library scan"),
        (nameof(PluginConfiguration.RequestWaitSeconds), "Wait for a build"),
        (nameof(PluginConfiguration.EnableWebUi), "Add Library Maintenance to the web client"),
        (nameof(PluginConfiguration.MoveEnabled), "Move media"),
        (nameof(PluginConfiguration.ShowInItemMenu), "Move in the item menu"),
        (nameof(PluginConfiguration.ReportWebAnchorProblems), "Report missing web client elements"),
    ];

    /// <summary>
    /// Gets the settings as the API gives them, every field set.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <returns>The values.</returns>
    public static LibraryMaintenanceSettingsDto ToDto(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new LibraryMaintenanceSettingsDto
        {
            DuplicatesEnabled = configuration.DuplicatesEnabled,
            DefaultMatchMode = configuration.DefaultMatchMode ?? string.Empty,
            UnknownYearPolicy = configuration.UnknownYearPolicy ?? string.Empty,
            IncludedLibraryIds = [.. configuration.IncludedLibraryIds ?? []],
            ExcludedPathPrefixes = [.. configuration.ExcludedPathPrefixes ?? []],
            StripEditionSuffixes = configuration.StripEditionSuffixes,
            EditionPhrases = [.. configuration.EditionPhrases ?? []],
            ArticleLanguages = [.. configuration.ArticleLanguages ?? []],
            WarmIndexAfterScan = configuration.WarmIndexAfterScan,
            RequestWaitSeconds = configuration.RequestWaitSeconds,
            EnableWebUi = configuration.EnableWebUi,
            MoveEnabled = configuration.MoveEnabled,
            ShowInItemMenu = configuration.ShowInItemMenu,
            ReportWebAnchorProblems = configuration.ReportWebAnchorProblems
        };
    }

    /// <summary>
    /// Gets a copy of the settings: every field, the Move fields the Settings tab doesn't show yet too, so a save
    /// of the tab (<see cref="Apply"/> starts from this copy) keeps them as they are.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <returns>The copy.</returns>
    public static PluginConfiguration Copy(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new PluginConfiguration
        {
            DuplicatesEnabled = configuration.DuplicatesEnabled,
            DefaultMatchMode = configuration.DefaultMatchMode,
            UnknownYearPolicy = configuration.UnknownYearPolicy,
            IncludedLibraryIds = [.. configuration.IncludedLibraryIds ?? []],
            ExcludedPathPrefixes = [.. configuration.ExcludedPathPrefixes ?? []],
            StripEditionSuffixes = configuration.StripEditionSuffixes,
            EditionPhrases = [.. configuration.EditionPhrases ?? []],
            ArticleLanguages = [.. configuration.ArticleLanguages ?? []],
            WarmIndexAfterScan = configuration.WarmIndexAfterScan,
            RequestWaitSeconds = configuration.RequestWaitSeconds,
            MoveEnabled = configuration.MoveEnabled,
            ShowInItemMenu = configuration.ShowInItemMenu,
            ShowDetailPageButton = configuration.ShowDetailPageButton,
            ShowInTvLayout = configuration.ShowInTvLayout,
            AllowedItemKinds = [.. configuration.AllowedItemKinds ?? []],
            AllowCrossLibraryMoves = configuration.AllowCrossLibraryMoves,
            AllowCrossVolumeMoves = configuration.AllowCrossVolumeMoves,
            FreeSpaceMarginMb = configuration.FreeSpaceMarginMb,
            VerifyCopies = configuration.VerifyCopies,
            MetadataCarryOver = configuration.MetadataCarryOver,
            RestoreDateAdded = configuration.RestoreDateAdded,
            PreStageInternalData = configuration.PreStageInternalData,
            RewriteCollectionsAndPlaylists = configuration.RewriteCollectionsAndPlaylists,
            CallRelinkHooks = configuration.CallRelinkHooks,
            LooseFileFolderMode = configuration.LooseFileFolderMode,
            RemoveEmptySourceFolders = configuration.RemoveEmptySourceFolders,
            WaitForScanMinutes = configuration.WaitForScanMinutes,
            AwaitLibraryHours = configuration.AwaitLibraryHours,
            SourceDeleteRetries = configuration.SourceDeleteRetries,
            MaxQueuedJobs = configuration.MaxQueuedJobs,
            JobHistoryDays = configuration.JobHistoryDays,
            EnableWebUi = configuration.EnableWebUi,
            ReportWebAnchorProblems = configuration.ReportWebAnchorProblems
        };
    }

    /// <summary>
    /// Gets the settings' version: 16 hex digits of a SHA-256 of the Settings tab's fields' values (<see cref="Fields"/>).
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <returns>The version.</returns>
    public static string Version(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var text = new StringBuilder();
        foreach (var (field, _) in Fields)
        {
            text.Append(field).Append('=').Append(Canonical(configuration, field)).Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// Applies a change to the saved settings: each field given replaces the saved one, after the rules above.
    /// </summary>
    /// <param name="current">The saved settings.</param>
    /// <param name="change">The fields to change; null ones keep their value.</param>
    /// <param name="libraries">The ids of the server's movie and mixed libraries.</param>
    /// <param name="lenient">True for an import: a value it can't use is a warning, not a 400.</param>
    /// <param name="warnings">Gets the import's warnings.</param>
    /// <returns>The new settings (a copy; <paramref name="current"/> is unchanged).</returns>
    /// <exception cref="PluginException">400 for a value a save can't use.</exception>
    public static PluginConfiguration Apply(PluginConfiguration current, LibraryMaintenanceSettingsDto change, IReadOnlyCollection<Guid> libraries, bool lenient, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(warnings);
        var next = Copy(current);
        next.DuplicatesEnabled = change.DuplicatesEnabled ?? current.DuplicatesEnabled;
        next.DefaultMatchMode = Name(change.DefaultMatchMode, current.DefaultMatchMode, MatchModes, nameof(PluginConfiguration.DefaultMatchMode), lenient, warnings);
        next.UnknownYearPolicy = Name(change.UnknownYearPolicy, current.UnknownYearPolicy, UnknownYearPolicies, nameof(PluginConfiguration.UnknownYearPolicy), lenient, warnings);
        if (change.IncludedLibraryIds is { } included)
        {
            next.IncludedLibraryIds = Libraries(included, current.IncludedLibraryIds ?? [], libraries, lenient, warnings);
        }

        if (change.ExcludedPathPrefixes is { } folders)
        {
            next.ExcludedPathPrefixes = Folders(folders, lenient, warnings);
        }

        next.StripEditionSuffixes = change.StripEditionSuffixes ?? current.StripEditionSuffixes;
        if (change.EditionPhrases is { } phrases)
        {
            next.EditionPhrases = Phrases(phrases, lenient, warnings);
        }

        if (change.ArticleLanguages is { } languages)
        {
            next.ArticleLanguages = Languages(languages, current.ArticleLanguages ?? [], lenient, warnings);
        }

        next.WarmIndexAfterScan = change.WarmIndexAfterScan ?? current.WarmIndexAfterScan;
        next.EnableWebUi = change.EnableWebUi ?? current.EnableWebUi;
        next.MoveEnabled = change.MoveEnabled ?? current.MoveEnabled;
        next.ShowInItemMenu = change.ShowInItemMenu ?? current.ShowInItemMenu;
        next.ReportWebAnchorProblems = change.ReportWebAnchorProblems ?? current.ReportWebAnchorProblems;
        if (change.RequestWaitSeconds is { } wait)
        {
            if (wait is >= 0 and <= MaxRequestWaitSeconds)
            {
                next.RequestWaitSeconds = wait;
            }
            else if (lenient)
            {
                warnings.Add(string.Create(CultureInfo.InvariantCulture, $"RequestWaitSeconds {wait} isn't from 0 to {MaxRequestWaitSeconds}; the saved {current.RequestWaitSeconds} is kept."));
            }
            else
            {
                throw PluginException.Invalid(string.Create(CultureInfo.InvariantCulture, $"RequestWaitSeconds must be a whole number from 0 to {MaxRequestWaitSeconds}."));
            }
        }

        return next;
    }

    /// <summary>
    /// Gets each of the Settings tab's fields whose value differs, with both values as text.
    /// </summary>
    /// <param name="before">The saved settings.</param>
    /// <param name="after">The new settings.</param>
    /// <param name="libraryName">A library's name, or null for one that isn't on the server.</param>
    /// <returns>The changes, in the Settings tab's order.</returns>
    public static IReadOnlyList<LibraryMaintenanceSettingsChangeDto> Diff(PluginConfiguration before, PluginConfiguration after, Func<Guid, string?> libraryName)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(libraryName);
        var changes = new List<LibraryMaintenanceSettingsChangeDto>();
        foreach (var (field, label) in Fields)
        {
            if (!string.Equals(Canonical(before, field), Canonical(after, field), StringComparison.Ordinal))
            {
                changes.Add(new LibraryMaintenanceSettingsChangeDto { Field = field, Label = label, From = Text(before, field, libraryName), To = Text(after, field, libraryName) });
            }
        }

        return changes;
    }

    /// <summary>
    /// Gets whether the title rules differ (rules N4, N7 and N7b's settings): the groups are worked out again.
    /// </summary>
    /// <param name="before">The saved settings.</param>
    /// <param name="after">The new settings.</param>
    /// <returns>Whether they differ.</returns>
    public static bool TitleRulesDiffer(PluginConfiguration before, PluginConfiguration after)
        => !string.Equals(Canonical(before, nameof(PluginConfiguration.StripEditionSuffixes)), Canonical(after, nameof(PluginConfiguration.StripEditionSuffixes)), StringComparison.Ordinal)
            || !string.Equals(Canonical(before, nameof(PluginConfiguration.EditionPhrases)), Canonical(after, nameof(PluginConfiguration.EditionPhrases)), StringComparison.Ordinal)
            || !string.Equals(Canonical(before, nameof(PluginConfiguration.ArticleLanguages)), Canonical(after, nameof(PluginConfiguration.ArticleLanguages)), StringComparison.Ordinal);

    /// <summary>
    /// Gets whether the index's scope differs: the libraries it reads or the folders it leaves out (the index's own key).
    /// </summary>
    /// <param name="before">The saved settings.</param>
    /// <param name="after">The new settings.</param>
    /// <returns>Whether it differs.</returns>
    public static bool ScopeDiffers(PluginConfiguration before, PluginConfiguration after)
        => !string.Equals(MovieIndexService.IndexKey(before), MovieIndexService.IndexKey(after), StringComparison.Ordinal);

    /// <summary>
    /// Gets a folder as the excluded folders compare it: <c>/</c> as <c>\</c>, no trailing separator, ignoring case.
    /// </summary>
    /// <param name="path">The folder.</param>
    /// <returns>Its comparison form.</returns>
    public static string FolderKey(string path)
        => (path ?? string.Empty).Trim().Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();

    /// <summary>
    /// Gets whether a folder is a Windows device or NT path: <c>\\?\</c> or <c>\\.\</c> (either slash, or that root
    /// alone, <c>\\?</c> or <c>\\.</c>), or the NT object prefix <c>\??\</c> (either slash, or <c>\??</c> alone), which
    /// Win32 passes straight to the object manager, so <c>\??\UNC\host\share</c> is a share and <c>\??\C:\x</c> a drive.
    /// Every other such spelling starts with one of these (<c>\\?\GLOBALROOT</c>, <c>\??\GLOBALROOT</c>,
    /// <c>\\.\UNC</c>, <c>\\?\Volume{…}</c>). And, as Move's <c>PathRules</c> has it (the settings
    /// since 1.0.2), any path whose second character is <c>?</c>: .NET takes <c>\?\C:\x</c> (one slash)
    /// for fully qualified, and Win32 resolves it to <c>C:\</c>. Not a folder a library's items are under, and one a check
    /// could open as a device, a pipe or a share. The settings refuse it.
    /// </summary>
    /// <param name="path">The folder, trimmed.</param>
    /// <returns>True for a device path.</returns>
    public static bool IsDevicePath(string path)
        => path is not null && ((path.Length >= 2 && path[1] == '?')
            || (path.Length >= 3 && IsSlash(path[0])
                && ((IsSlash(path[1]) && path[2] is '?' or '.') || (path[1] == '?' && path[2] == '?'))
                && (path.Length == 3 || IsSlash(path[3]))));

    /// <summary>
    /// Gets whether a folder is on a network share by its form: a UNC path (<c>\\server\share</c>, either slash) that
    /// isn't a device path. A preview never opens it, so nothing connects to a server before the administrator saves.
    /// </summary>
    /// <param name="path">The folder, trimmed.</param>
    /// <returns>True for a UNC path.</returns>
    public static bool IsNetworkPath(string path)
        => path is not null && path.Length >= 2 && IsSlash(path[0]) && IsSlash(path[1]) && !IsDevicePath(path);

    private static bool IsSlash(char c) => c is '\\' or '/';

    // One field's value in a fixed form: what the version hashes and the diff compares.
    private static string Canonical(PluginConfiguration configuration, string field) => field switch
    {
        nameof(PluginConfiguration.DuplicatesEnabled) => configuration.DuplicatesEnabled ? "1" : "0",
        nameof(PluginConfiguration.DefaultMatchMode) => configuration.DefaultMatchMode ?? string.Empty,
        nameof(PluginConfiguration.UnknownYearPolicy) => configuration.UnknownYearPolicy ?? string.Empty,
        nameof(PluginConfiguration.IncludedLibraryIds) => string.Join(',', (configuration.IncludedLibraryIds ?? []).Select(id => id.ToString("N", CultureInfo.InvariantCulture))),
        nameof(PluginConfiguration.ExcludedPathPrefixes) => string.Join('\u001f', configuration.ExcludedPathPrefixes ?? []),
        nameof(PluginConfiguration.StripEditionSuffixes) => configuration.StripEditionSuffixes ? "1" : "0",
        nameof(PluginConfiguration.EditionPhrases) => string.Join('\u001f', configuration.EditionPhrases ?? []),
        nameof(PluginConfiguration.ArticleLanguages) => string.Join(',', configuration.ArticleLanguages ?? []),
        nameof(PluginConfiguration.WarmIndexAfterScan) => configuration.WarmIndexAfterScan ? "1" : "0",
        nameof(PluginConfiguration.RequestWaitSeconds) => configuration.RequestWaitSeconds.ToString(CultureInfo.InvariantCulture),
        nameof(PluginConfiguration.EnableWebUi) => configuration.EnableWebUi ? "1" : "0",
        nameof(PluginConfiguration.MoveEnabled) => configuration.MoveEnabled ? "1" : "0",
        nameof(PluginConfiguration.ShowInItemMenu) => configuration.ShowInItemMenu ? "1" : "0",
        nameof(PluginConfiguration.ReportWebAnchorProblems) => configuration.ReportWebAnchorProblems ? "1" : "0",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a setting of the Settings tab.")
    };

    // One field's value as the report's text.
    private static string Text(PluginConfiguration configuration, string field, Func<Guid, string?> libraryName)
    {
        static string OnOff(bool value) => value ? "On" : "Off";
        static string List(IEnumerable<string> values, string none)
        {
            var text = string.Join(", ", values);
            return text.Length == 0 ? none : text;
        }

        return field switch
        {
            nameof(PluginConfiguration.DuplicatesEnabled) => OnOff(configuration.DuplicatesEnabled),
            nameof(PluginConfiguration.DefaultMatchMode) => configuration.DefaultMatchMode switch
            {
                "TitleAndYear" => "Title + Year",
                "Title" => "Title only",
                var other => other ?? string.Empty
            },
            nameof(PluginConfiguration.UnknownYearPolicy) => configuration.UnknownYearPolicy switch
            {
                "MatchUnknownOnly" => "Only with other movies with no year",
                "MatchAny" => "With the title's one year, when it has one",
                var other => other ?? string.Empty
            },
            nameof(PluginConfiguration.IncludedLibraryIds) => List(
                (configuration.IncludedLibraryIds ?? []).Select(id => libraryName(id) ?? "a library no longer on this server (" + id.ToString("N", CultureInfo.InvariantCulture) + ")"),
                "Every movie and mixed library"),
            nameof(PluginConfiguration.ExcludedPathPrefixes) => List(configuration.ExcludedPathPrefixes ?? [], "None"),
            nameof(PluginConfiguration.StripEditionSuffixes) => OnOff(configuration.StripEditionSuffixes),
            nameof(PluginConfiguration.EditionPhrases) => List(configuration.EditionPhrases ?? [], "None"),
            nameof(PluginConfiguration.ArticleLanguages) => List(configuration.ArticleLanguages ?? [], "None"),
            nameof(PluginConfiguration.WarmIndexAfterScan) => OnOff(configuration.WarmIndexAfterScan),
            nameof(PluginConfiguration.RequestWaitSeconds) => configuration.RequestWaitSeconds.ToString(CultureInfo.InvariantCulture) + " s",
            nameof(PluginConfiguration.EnableWebUi) => OnOff(configuration.EnableWebUi),
            nameof(PluginConfiguration.MoveEnabled) => OnOff(configuration.MoveEnabled),
            nameof(PluginConfiguration.ShowInItemMenu) => OnOff(configuration.ShowInItemMenu),
            nameof(PluginConfiguration.ReportWebAnchorProblems) => OnOff(configuration.ReportWebAnchorProblems),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a setting of the Settings tab.")
        };
    }

    // A name setting: one this version knows (written in its own spelling), or the saved value unchanged.
    private static string Name(string? value, string? saved, IReadOnlyList<string> known, string field, bool lenient, List<string> warnings)
    {
        var kept = saved ?? known[0];
        if (value is null)
        {
            return kept;
        }

        var text = value.Trim();
        var match = known.FirstOrDefault(k => string.Equals(k, text, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        if (string.Equals(text, saved, StringComparison.Ordinal))
        {
            return kept;
        }

        if (lenient)
        {
            warnings.Add($"{field} \"{text}\" isn't one this version knows ({string.Join(", ", known)}); the saved \"{kept}\" is kept.");
            return kept;
        }

        throw PluginException.Invalid($"{field} must be {string.Join(" or ", known)}.");
    }

    private static Guid[] Libraries(IReadOnlyList<Guid> ids, IReadOnlyList<Guid> saved, IReadOnlyCollection<Guid> libraries, bool lenient, List<string> warnings)
    {
        var result = new List<Guid>();
        var dropped = 0;
        foreach (var id in ids.Where(id => id != Guid.Empty).Distinct())
        {
            if (libraries.Contains(id) || saved.Contains(id))
            {
                // A saved id that is no longer a library here stays until the administrator removes it (a select keeps a
                // value it has no option for).
                result.Add(id);
            }
            else if (lenient)
            {
                dropped++;
            }
            else
            {
                throw PluginException.Invalid($"IncludedLibraryIds: {id:N} isn't a movie or mixed library on this server.");
            }
        }

        if (dropped > 0)
        {
            warnings.Add(result.Count == 0
                ? string.Create(CultureInfo.InvariantCulture, $"None of the file's {dropped} included libraries is on this server, so the index would read every movie and mixed library.")
                : string.Create(CultureInfo.InvariantCulture, $"{dropped} of the file's included libraries {(dropped == 1 ? "isn't" : "aren't")} on this server and {(dropped == 1 ? "is" : "are")} left out."));
        }

        return [.. result];
    }

    private static string[] Folders(IReadOnlyList<string> folders, bool lenient, List<string> warnings)
    {
        var result = new List<string>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in folders)
        {
            var folder = (raw ?? string.Empty).Trim();
            if (folder.Length == 0)
            {
                continue;
            }

            var problem = folder.Length > MaxPathLength
                ? string.Create(CultureInfo.InvariantCulture, $"is longer than {MaxPathLength:N0} characters")
                : folder.Any(char.IsControl) ? "holds a control character"
                    : IsDevicePath(folder) ? "is a device path, not a folder path (like D:\\Clips)"
                        : !System.IO.Path.IsPathFullyQualified(folder) ? "isn't a full folder path on this server (like D:\\Clips)"
                            : null;
            if (problem is not null)
            {
                if (!lenient)
                {
                    throw PluginException.Invalid($"ExcludedPathPrefixes: \"{Shorten(folder)}\" {problem}.");
                }

                warnings.Add($"The excluded folder \"{Shorten(folder)}\" {problem}, so it is left out.");
                continue;
            }

            if (keys.Add(FolderKey(folder)))
            {
                result.Add(folder);
            }
        }

        if (result.Count > MaxFolders)
        {
            if (!lenient)
            {
                throw PluginException.Invalid(string.Create(CultureInfo.InvariantCulture, $"ExcludedPathPrefixes holds at most {MaxFolders} folders."));
            }

            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"The file has {result.Count} excluded folders; the first {MaxFolders} are kept."));
            result = result.Take(MaxFolders).ToList();
        }

        return [.. result];
    }

    private static string[] Phrases(IReadOnlyList<string> phrases, bool lenient, List<string> warnings)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in phrases)
        {
            var phrase = (raw ?? string.Empty).Trim();
            if (phrase.Length == 0)
            {
                continue;
            }

            if (phrase.Length > MaxPhraseLength || phrase.Any(char.IsControl))
            {
                var problem = phrase.Length > MaxPhraseLength ? string.Create(CultureInfo.InvariantCulture, $"is longer than {MaxPhraseLength} characters") : "holds a control character";
                if (!lenient)
                {
                    throw PluginException.Invalid($"EditionPhrases: \"{Shorten(phrase)}\" {problem}.");
                }

                warnings.Add($"The edition phrase \"{Shorten(phrase)}\" {problem}, so it is left out.");
                continue;
            }

            if (seen.Add(phrase))
            {
                result.Add(phrase);
            }
        }

        if (result.Count > MaxPhrases)
        {
            if (!lenient)
            {
                throw PluginException.Invalid(string.Create(CultureInfo.InvariantCulture, $"EditionPhrases holds at most {MaxPhrases} phrases."));
            }

            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"The file has {result.Count} edition phrases; the first {MaxPhrases} are kept."));
            result = result.Take(MaxPhrases).ToList();
        }

        return [.. result];
    }

    private static string[] Languages(IReadOnlyList<string> languages, IReadOnlyList<string> saved, bool lenient, List<string> warnings)
    {
        var result = new List<string>();
        foreach (var raw in languages)
        {
            var code = (raw ?? string.Empty).Trim();
            if (code.Length == 0)
            {
                continue;
            }

            var known = NormalizationOptions.ArticlesByLanguage.Keys.FirstOrDefault(k => string.Equals(k, code, StringComparison.OrdinalIgnoreCase));
            var value = known ?? (saved.Contains(code, StringComparer.Ordinal) ? code : null);
            if (value is null)
            {
                var all = string.Join(", ", NormalizationOptions.ArticlesByLanguage.Keys);
                if (!lenient)
                {
                    throw PluginException.Invalid($"ArticleLanguages: \"{Shorten(code)}\" isn't a language this version knows ({all}).");
                }

                warnings.Add($"The article language \"{Shorten(code)}\" isn't one this version knows ({all}), so it is left out.");
                continue;
            }

            if (!result.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(value);
            }
        }

        return [.. result];
    }

    private static string Shorten(string text) => text.Length <= 80 ? text : string.Concat(text.AsSpan(0, 77), "…");
}
