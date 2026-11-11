using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Interop;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Import;

/// <summary>
/// The Jellyfin Enhanced bookmark import (since 1.1.0) over the kit's
/// <see cref="JellyfinEnhancedReader"/>: every user's <c>bookmark.json</c> (JE 12.8 to 12.12, <c>je-bookmark/1</c>),
/// read-only, matched to the Jellyfin user its folder names, and each bookmark to a video of that user's libraries.
/// </summary>
/// <remarks>
/// <para><b>Matching</b>, always as the bookmark's owner (the kit's privacy rule: a user's record only ever names videos
/// they can open): JE's <c>ItemId</c> when the owner can open it; else the kit's
/// <see cref="IdentityResolver"/> on what JE knew (Library Maintenance's move journal, then the TMDb or TVDb id of the film,
/// or of the series with the season and episode numbers); one Strong candidate is taken, several give the first by
/// library order, reported as ambiguous; no match keeps the bookmark detached ("Not in library") with JE's title
/// and ids, so the identity service attaches it once the video is there, with the import relink policy.</para>
/// <para><b>Apply</b> is one store write of the bookmarks, the ledger and the applied-import entries
/// (<see cref="JeImportPlanner"/>, planned again under the store's lock), after a pre-operation snapshot when it changes
/// anything; imports are run one at a time. A dry run writes nothing. Nothing here ever writes, moves or deletes a JE file.
/// Names and labels never reach the log: counts and ids only.</para>
/// <para><b>The checklist's gate</b> (<see cref="Describe"/>): the settings page shows the switch-off checklist once every
/// JE user with a <c>bookmark.json</c> has an applied import (a dry run doesn't count); with no JE bookmarks at all there
/// is nothing to import first.</para>
/// </remarks>
internal sealed class JellyfinEnhancedBookmarkImporter : IBookmarkJeImport, IDisposable
{
    /// <summary>The most user ids one request names.</summary>
    public const int MaxUserIds = 1000;

    /// <summary>The 404 answer when there is nothing to import.</summary>
    public const string NoDataMessage = "Jellyfin Enhanced has no bookmarks on this server.";

    private readonly IBookmarkStore _store;
    private readonly JellyfinEnhancedReader _reader;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IdentityResolver _resolver;
    private readonly MoveJournalReader _moves;
    private readonly string _snapshotDirectory;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _applying = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinEnhancedBookmarkImporter"/> class.
    /// </summary>
    /// <param name="store">The bookmark store.</param>
    /// <param name="reader">The kit's reader of Jellyfin Enhanced's files.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="resolver">The kit's identity resolver.</param>
    /// <param name="moves">The kit's reader of Library Maintenance's move journal, read once per import.</param>
    /// <param name="snapshotDirectory">The snapshots folder, for the pre-operation snapshot.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="clock">The clock; the system clock when <c>null</c>.</param>
    public JellyfinEnhancedBookmarkImporter(
        IBookmarkStore store,
        JellyfinEnhancedReader reader,
        ILibraryManager library,
        IUserManager users,
        IdentityResolver resolver,
        MoveJournalReader moves,
        string snapshotDirectory,
        ILogger logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(moves);
        ArgumentException.ThrowIfNullOrEmpty(snapshotDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _reader = reader;
        _library = library;
        _users = users;
        _resolver = resolver;
        _moves = moves;
        _snapshotDirectory = snapshotDirectory;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the relink policy the import matches with: Bookmarks' import policy, which takes one Strong candidate
    /// without a stored runtime (JE never stored one).
    /// </summary>
    public static RelinkPolicy MatchPolicy { get; } = RelinkPolicy.RuntimeWithin();

    /// <inheritdoc />
    public async Task<BookmarkJeImportReportDto> ImportAsync(bool dryRun, BookmarkJeImportRequest? request, Guid appliedBy, CancellationToken cancellationToken)
    {
        var wanted = Validate(request);
        var listed = _reader.ListUsersWithBookmarks(out var problem);
        if (problem is not null)
        {
            throw PluginException.Unavailable(problem);
        }

        // Each user once (the kit lists a user with two folder spellings once; this keeps it so).
        var userIds = listed.Where(id => wanted is null || wanted.Contains(id)).Distinct().ToList();
        if (userIds.Count == 0)
        {
            throw BookmarkException.NotFound(wanted is null ? NoDataMessage : "None of these users has Jellyfin Enhanced bookmarks on this server.");
        }

        if (dryRun)
        {
            return await Task.Run(() => DryRun(userIds), cancellationToken).ConfigureAwait(false);
        }

        _store.ThrowIfReadOnly();
        await _applying.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyAsync(userIds, appliedBy.Equals(Guid.Empty) ? null : appliedBy, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _applying.Release();
        }
    }

    /// <summary>
    /// Describes the import for Diagnostics (<c>JeImport</c>): the JE users with bookmarks, which of them no
    /// applied import covers yet (<c>Pending</c>), the applied imports, and <c>ChecklistReady</c>, the settings page's gate
    /// for the switch-off checklist and "Use B for bookmarks": every JE user with bookmarks covered by an applied import (a
    /// dry run doesn't count), or no JE bookmarks at all. A data folder that can't be listed keeps it closed, with the reason.
    /// </summary>
    /// <returns>A plain dictionary.</returns>
    public IDictionary<string, object?> Describe()
    {
        var listed = _reader.ListUsersWithBookmarks(out var problem);
        var runs = _store.ImportRuns;
        var pending = problem is null ? listed.Where(id => !runs.ContainsKey(JeImportSource.RunKeyOf(id))).ToList() : [];
        var applied = runs.Where(r => r.Key.StartsWith("je:", StringComparison.Ordinal)).ToList();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["UsersWithBookmarks"] = listed.Count,
            ["Pending"] = pending.Select(id => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["JeUserId"] = id.ToString("N", CultureInfo.InvariantCulture),
                ["UserName"] = UserName(id)
            }).ToList(),
            ["Problem"] = problem,
            ["ChecklistReady"] = problem is null && pending.Count == 0,
            ["StoreReadOnly"] = _store.IsReadOnly,
            ["LastAppliedUtc"] = applied.Count == 0 ? null : applied.Max(r => r.Value.AppliedUtc),
            ["Runs"] = applied.Select(r =>
            {
                var id = Guid.TryParseExact(r.Key[3..], "N", out var parsed) ? parsed : Guid.Empty;
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["JeUserId"] = r.Key[3..],
                    ["UserName"] = id.Equals(Guid.Empty) ? null : UserName(id),
                    ["AppliedUtc"] = r.Value.AppliedUtc,
                    ["AppliedBy"] = r.Value.AppliedBy?.ToString("N", CultureInfo.InvariantCulture),
                    ["AppliedByName"] = r.Value.AppliedBy is { } by ? UserName(by) : null,
                    ["Found"] = r.Value.Found,
                    ["Imported"] = r.Value.Imported,
                    ["Updated"] = r.Value.Updated,
                    ["Detached"] = r.Value.Detached,
                    ["Duplicates"] = r.Value.Duplicates,
                    ["AlreadyImported"] = r.Value.AlreadyImported,
                    ["Skipped"] = r.Value.Skipped
                };
            }).ToList(),
            ["LedgerEntries"] = _store.ImportLedger.Count,
            ["ImportedBookmarks"] = _store.GetAll().Count(b => b.Origin == BookmarkOrigin.JellyfinEnhanced && BookmarkRecordRules.IsLive(b))
        };
    }

    /// <summary>
    /// Gets a value indicating whether Jellyfin Enhanced has bookmarks on this server that no applied import covers yet:
    /// the hint an admin sees on an empty My Bookmarks page. False when JE's folder can't be listed.
    /// </summary>
    /// <returns>Whether an import is waiting.</returns>
    public bool HasPendingImport()
    {
        var listed = _reader.ListUsersWithBookmarks(out var problem);
        var runs = _store.ImportRuns;
        return problem is null && listed.Any(id => !runs.ContainsKey(JeImportSource.RunKeyOf(id)));
    }

    private static HashSet<Guid>? Validate(BookmarkJeImportRequest? request)
    {
        if (request?.UserIds is not { Count: > 0 } ids)
        {
            return null;
        }

        if (ids.Count > MaxUserIds)
        {
            throw BookmarkException.Invalid("At most 1000 user ids can be imported at once.");
        }

        if (ids.Any(id => id.Equals(Guid.Empty)))
        {
            throw BookmarkException.Invalid("A user id can't be empty.");
        }

        return [.. ids];
    }

    private BookmarkJeImportReportDto DryRun(IReadOnlyList<Guid> userIds)
    {
        var sources = Read(userIds);
        var document = _store.Documents.Current;
        var resolutions = Resolve(document, sources);
        var plan = JeImportPlanner.Plan(document, sources, key => resolutions.GetValueOrDefault(key), Now(), appliedBy: null);
        return Report(plan, dryRun: true, appliedUtc: null, snapshot: null);
    }

    private async Task<BookmarkJeImportReportDto> ApplyAsync(IReadOnlyList<Guid> userIds, Guid? appliedBy, CancellationToken cancellationToken)
    {
        var sources = await Task.Run(() => Read(userIds), cancellationToken).ConfigureAwait(false);
        var before = _store.Documents.Current;
        var resolutions = await Task.Run(() => Resolve(before, sources), cancellationToken).ConfigureAwait(false);
        var now = Now();

        // A snapshot first when the import changes anything (as before every risky operation); a second run with nothing
        // new writes nothing, not even a snapshot.
        string? snapshot = null;
        if (JeImportPlanner.Plan(before, sources, key => resolutions.GetValueOrDefault(key), now, appliedBy).Writes)
        {
            snapshot = await _store.Documents.PreOpSnapshotAsync(_snapshotDirectory, SnapshotOperations.Import, cancellationToken).ConfigureAwait(false) is { } path
                ? System.IO.Path.GetFileName(path)
                : null;
        }

        // Planned again under the store's lock, over the document it writes: a bookmark changed or a run applied meanwhile is
        // seen as it is now.
        JeImportPlan? plan = null;
        await _store.UpdateDocumentAsync(
            current =>
            {
                plan = JeImportPlanner.Plan(current, sources, key => resolutions.GetValueOrDefault(key), now, appliedBy);
                return plan.ApplyTo(current);
            },
            cancellationToken).ConfigureAwait(false);

        var report = Report(plan!, dryRun: false, appliedUtc: now, snapshot: plan!.Writes ? snapshot : null);
        _logger.LogInformation(
            "Jellyfin Enhanced bookmark import applied for {Users} user(s): {Imported} added ({Detached} not in the library), {Updated} updated, {Duplicates} duplicates, {Already} already imported, {Skipped} skipped",
            report.Users.Count,
            report.Users.Sum(u => u.Import),
            report.Users.Sum(u => u.Detached),
            report.Users.Sum(u => u.Updated),
            report.Users.Sum(u => u.Duplicates),
            report.Users.Sum(u => u.AlreadyImported),
            report.Users.Sum(u => u.Skipped));
        return report;
    }

    private BookmarkJeImportReportDto Report(JeImportPlan plan, bool dryRun, DateTime? appliedUtc, string? snapshot)
    {
        var report = new BookmarkJeImportReportDto
        {
            DryRun = dryRun,
            Format = JeBookmarkFile.FormatName,
            AppliedUtc = appliedUtc,
            StoreReadOnly = _store.IsReadOnly,
            Snapshot = snapshot,
            Users = plan.Users,
            Samples = plan.Samples,
            MoreEntries = plan.MoreEntries
        };

        if (dryRun)
        {
            return report;
        }

        // After an applied import: the gate, and the checklist as the server sees it now.
        var ready = HasNothingPending();
        report.ChecklistReady = ready;
        if (ready)
        {
            var overlap = JeSwitchOff.Describe(JeFeature.Bookmarks, _reader.Read(), UserName);
            report.Checklist = overlap.Checklist.Select(i => new BookmarkJeChecklistItemDto
            {
                Key = i.Key,
                Text = i.Text,
                Done = i.Done,
                Detail = i.Detail,
                NeedsRestart = i.NeedsRestart
            }).ToList();
            report.ChecklistComplete = overlap.Complete;
            report.CanUseB = overlap.CanUseB;
        }

        return report;
    }

    private bool HasNothingPending()
    {
        var listed = _reader.ListUsersWithBookmarks(out var problem);
        var runs = _store.ImportRuns;
        return problem is null && listed.All(id => runs.ContainsKey(JeImportSource.RunKeyOf(id)));
    }

    private List<JeImportSource> Read(IReadOnlyList<Guid> userIds)
        => userIds.Select(id => new JeImportSource(id, FindUser(id), _reader.ReadBookmarks(id))).ToList();

    // Finds each entry's video once, for the entries the plan would import or rewrite, as their owner.
    private Dictionary<string, JeEntryResolution> Resolve(BookmarkStoreDocument document, IReadOnlyList<JeImportSource> sources)
    {
        var resolutions = new Dictionary<string, JeEntryResolution>(StringComparer.Ordinal);
        var wanted = JeImportPlanner.ToResolve(document, sources);
        MoveJournal? journal = null;
        foreach (var source in sources)
        {
            if (source.Owner is not { } owner || source.File.Format != JeDataFormat.Bookmarks1)
            {
                continue;
            }

            var cache = new Dictionary<string, JeEntryResolution>(StringComparer.Ordinal);
            foreach (var entry in source.File.Entries)
            {
                if (!wanted.Contains(source.LedgerKey(entry.Key)))
                {
                    continue;
                }

                // The same video asked once per user: JE's item id, kind, ids and numbers.
                var lookup = string.Join('|', entry.ItemId, entry.MediaType, entry.TmdbId, entry.TvdbId, entry.SeasonNumber, entry.EpisodeNumber, entry.Name);
                if (!cache.TryGetValue(lookup, out var resolution))
                {
                    journal ??= _moves.Read();
                    resolution = ResolveEntry(owner, entry, journal);
                    cache[lookup] = resolution;
                }

                resolutions[source.LedgerKey(entry.Key)] = resolution;
            }
        }

        return resolutions;
    }

    private JeEntryResolution ResolveEntry(User owner, JeBookmark entry, MoveJournal journal)
    {
        if (!Guid.TryParse(entry.ItemId, out var jeItemId) || jeItemId.Equals(Guid.Empty))
        {
            return JeEntryResolution.Skip("Malformed: its ItemId isn't a Jellyfin item id.");
        }

        // 1. JE's item id, as the owner (their libraries and parental settings).
        if (Find(jeItemId, owner) is Video byId)
        {
            return Matched(BookmarkJeImportOutcome.MatchedById, byId, "On the video Jellyfin Enhanced's item id names.");
        }

        // 2. What JE knew: the move journal, then the provider ids (a film's own; an episode's series with its numbers).
        var snapshot = Snapshot(jeItemId, entry);
        var result = _resolver.Resolve(snapshot, MatchPolicy, journal, attachedUtc: null, isVisible: item => Find(item.Id, owner) is Video);
        var ids = DescribeIds(snapshot);
        if (result.ShouldRelink && !result.IsPending && result.Item is Video relinked && Find(relinked.Id, owner) is Video visible)
        {
            return result.Strength == MatchStrength.Exact
                ? Matched(BookmarkJeImportOutcome.MatchedById, visible, "Jellyfin Enhanced's item id was moved (Library Maintenance's move journal); on the video it moved to.")
                : Matched(BookmarkJeImportOutcome.MatchedByProviderId, visible, $"Jellyfin Enhanced's item id is gone; matched by {ids}.");
        }

        if (result.Outcome == ResolveOutcome.Ambiguous && result.Reason == AmbiguousReason.SeveralStrongCandidates)
        {
            var strong = result.Candidates.Where(c => c.Strength == MatchStrength.Strong).ToList();
            if (strong.Select(c => Find(c.ItemId, owner)).OfType<Video>().FirstOrDefault() is { } first)
            {
                return Matched(
                    BookmarkJeImportOutcome.Ambiguous,
                    first,
                    string.Create(CultureInfo.InvariantCulture, $"{strong.Count} videos have {ids}; imported on the first by library order. Check its position."));
            }
        }

        // 3. No match: detached ("Not in library") with what JE knew, so it attaches once the video is there.
        var name = string.IsNullOrWhiteSpace(entry.Name) ? "Video" : entry.Name.Trim();
        return new JeEntryResolution(
            BookmarkJeImportOutcome.Detached,
            jeItemId,
            snapshot,
            name,
            null,
            ids.Length == 0
                ? "No video has Jellyfin Enhanced's item id; kept as Not in library."
                : $"No video has Jellyfin Enhanced's item id or {ids}; kept as Not in library until it is there.");
    }

    private JeEntryResolution Matched(BookmarkJeImportOutcome outcome, Video item, string reason)
        => new(outcome, item.Id, ItemIdentity.Capture(item, _library), BookmarkManager.GetDisplayName(item), item.RunTimeTicks is > 0 ? item.RunTimeTicks : null, reason);

    // What JE knew of the video: a film's TMDb (and TVDb) id, or an episode's series ids with its numbers.
    private static IdentitySnapshot Snapshot(Guid jeItemId, JeBookmark entry)
    {
        var name = entry.Name?.Trim() ?? string.Empty;
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(entry.TmdbId))
        {
            ids["Tmdb"] = entry.TmdbId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(entry.TvdbId))
        {
            ids["Tvdb"] = entry.TvdbId.Trim();
        }

        return entry.MediaType?.Trim().ToUpperInvariant() switch
        {
            "MOVIE" => new IdentitySnapshot { ItemId = jeItemId, Kind = nameof(BaseItemKind.Movie), Name = name, ProviderIds = ids },
            "TV" => new IdentitySnapshot
            {
                ItemId = jeItemId,
                Kind = nameof(BaseItemKind.Episode),
                Name = name,
                SeriesProviderIds = ids.Count > 0 ? ids : null,
                Season = entry.SeasonNumber,
                Episode = entry.EpisodeNumber
            },
            _ => new IdentitySnapshot { ItemId = jeItemId, Kind = nameof(BaseItemKind.Video), Name = name }
        };
    }

    // "TMDb 550", or "the series' TVDb 900401, S01E02", for the report.
    private static string DescribeIds(IdentitySnapshot snapshot)
    {
        var own = string.Join(" or ", snapshot.ProviderIds.Select(p => Provider(p.Key) + " " + p.Value));
        if (own.Length > 0)
        {
            return own;
        }

        if (snapshot.SeriesProviderIds is { Count: > 0 } series)
        {
            var numbers = snapshot.Season is int season && snapshot.Episode is int episode
                ? string.Create(CultureInfo.InvariantCulture, $", S{season:00}E{episode:00}")
                : string.Empty;
            return "the series' " + string.Join(" or ", series.Select(p => Provider(p.Key) + " " + p.Value)) + numbers;
        }

        return string.Empty;
    }

    private static string Provider(string key) => key.ToUpperInvariant() switch
    {
        "TMDB" => "TMDb",
        "TVDB" => "TVDb",
        "IMDB" => "IMDb",
        _ => key
    };

    private BaseItem? Find(Guid id, User owner)
    {
        if (id.Equals(Guid.Empty))
        {
            return null;
        }

        try
        {
            return _library.GetItemById<BaseItem>(id, owner);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Jellyfin throws for ids it can't materialise (its internal placeholder item).
            _logger.LogDebug(ex, "Could not load item {ItemId}", id);
            return null;
        }
    }

    private User? FindUser(Guid id)
    {
        try
        {
            return _users.GetUserById(id);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private string? UserName(Guid id) => FindUser(id)?.Username;

    /// <inheritdoc />
    public void Dispose() => _applying.Dispose();

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;
}
