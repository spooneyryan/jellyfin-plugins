using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Integration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// Keeps every bookmark on its video: the identity backfill after the startup gate, detach and
/// reattach on library changes with Bookmarks' <see cref="RelinkPolicy"/>, the move-journal catch-up (at startup and when
/// Library Maintenance calls <see cref="ItemMovedHookV1"/>), and the bookmark store as the kit's record owner (the verify
/// sweep, re-resolution, the retention archive, the reattach from it, purge).
/// </summary>
/// <remarks>
/// <para><b>Which records.</b> Every pass skips records that fail <see cref="BookmarkRecordRules.HasLibraryItem"/> (adopted
/// clips have no library item, and 10.11.5's <c>GetItemById(Guid.Empty)</c> throws). Clip-only tombstones take part like
/// live bookmarks: their clip's video can move or leave the library too. Nothing here changes a record's
/// <c>UpdatedAt</c> (the user's <c>IfUpdatedAt</c> token) or its clip.</para>
/// <para><b>Backfill</b>: attached records whose snapshot lacks what a 1.0 bookmark never stored (the path, the
/// runtime, the year, an episode's series and numbers; a migrated record's provisional name) are looked up once per
/// distinct item, in batches of <see cref="BatchSize"/> with a yield, one store write per batch. An item the library no
/// longer has is left to the verify sweep.</para>
/// <para><b>Library changes</b>, in the kit's order: records whose video, or whose video's series or season, was
/// removed detach (their snapshot completed from the one the monitor captured in the handler); a batch with removed
/// folders awaits the verify sweep; added and updated items refresh the snapshots of the records on them (a series or
/// season those of its episodes) and re-resolve the detached records through the kit (<see cref="DetachedRecords"/>):
/// id, move journal, path, then provider ids, the latter only for one candidate whose runtime is within 2 % and 60 s
/// (<see cref="RelinkPolicy.RuntimeWithin"/>). An Exact relink keeps the media source (rewritten when it was the old
/// item's id); a Strong one clears the media source and audio track, which belong to the other file, and sets the
/// "Relinked, check position" mark. Two candidates, or one that fails the runtime check (the other cut of a film), leave
/// the record detached and ambiguous in Diagnostics. A record without a stored runtime can't be checked: only a Jellyfin
/// Enhanced import (which never had a file of its own) takes the one candidate then; a bookmark made on a file (a 1.0
/// bookmark whose video was gone before the backfill looked it up) stays detached and ambiguous
/// (<see cref="GetRelinkPolicy"/>).</para>
/// <para><b>Move journal</b>: Committed lines after the stored position, each moving the records still on its
/// <c>OldId</c> that were attached before the line (a path used again gives an old id again) or moved there by an
/// earlier line of the same catch-up: an Exact relink (<c>AttachedUtc</c> = the line's time); a series or season line
/// moves the container ids. A new journal id, or a journal behind the stored position, replays it from the start.</para>
/// <para><b>Read-only data</b>: the service asks <see cref="BackgroundWriteGate"/> before any write, stays idle
/// for the process while the store is read-only, and reports the store's <see cref="IsReadOnly"/> to the kit, which then
/// leaves it alone.</para>
/// <para><b>Retention and purge.</b> Bookmarks keeps detached records for good by default (<c>DetachedRetentionDays</c>
/// 0). A record that has a clip file is never archived or purged here: its file needs a live record (clip records leave
/// through the clip operations and the missing-clip retention).</para>
/// <para>Logs name bookmarks by id and count them; names, notes and paths never reach Information.</para>
/// </remarks>
internal sealed class BookmarkIdentityService : KitBackgroundService, ILibraryChangeConsumer, IDetachedRecordOwner
{
    /// <summary>The owner's name: the store's, and the archive's (<c>archive/bookmarks-archived.jsonl</c>).</summary>
    public const string StoreName = "bookmarks";

    /// <summary>The consumer's name in logs and Diagnostics <c>LibraryChanges</c>.</summary>
    public const string ConsumerName = "Bookmarks";

    /// <summary>How many items the backfill and a refresh look up between two store writes.</summary>
    public const int BatchSize = 100;

    private readonly IBookmarkStore _store;
    private readonly BackgroundWriteGate _gate;
    private readonly ILibraryManager _library;
    private readonly MoveJournalReader _moves;
    private readonly Func<AttachedRecordVerifier> _verifier;
    private readonly Func<DetachedRecords> _detached;
    private readonly IPluginConfigurationProvider _configuration;
    private readonly string _snapshotDirectory;
    private readonly SemaphoreSlim _catchUpLock = new(1, 1);
    private readonly Channel<bool> _catchUpRequests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly Action _hook;
    private readonly object _statusLock = new();
    private IdentityPassResult? _lastBackfill;
    private IdentityPassResult? _lastCatchUp;
    private bool _startupDone;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkIdentityService"/> class.
    /// </summary>
    /// <param name="applicationHost">The server, for the startup gate.</param>
    /// <param name="store">The bookmark store.</param>
    /// <param name="gate">The read-only gate of automatic writers.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="moves">The kit's move-journal reader.</param>
    /// <param name="verifier">The kit's verify sweep, resolved when first needed.</param>
    /// <param name="detached">The kit's detached records, resolved when first needed.</param>
    /// <param name="configuration">The plugin settings.</param>
    /// <param name="snapshotDirectory">The snapshot folder, for the pre-purge snapshot.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The lifecycle's clock (the kit's <c>Clock</c>).</param>
    public BookmarkIdentityService(
        IServerApplicationHost applicationHost,
        IBookmarkStore store,
        BackgroundWriteGate gate,
        ILibraryManager library,
        MoveJournalReader moves,
        Func<AttachedRecordVerifier> verifier,
        Func<DetachedRecords> detached,
        IPluginConfigurationProvider configuration,
        string snapshotDirectory,
        ILogger logger,
        TimeProvider? timeProvider = null)
        : base(applicationHost, logger, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(moves);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(detached);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(snapshotDirectory);
        _store = store;
        _gate = gate;
        _library = library;
        _moves = moves;
        _verifier = verifier;
        _detached = detached;
        _configuration = configuration;
        _snapshotDirectory = snapshotDirectory;
        _hook = RequestCatchUp;
        Filter = new LibraryChangeFilter
        {
            // The videos bookmarks are on, and the series and seasons whose removal takes episodes with them (one event for
            // the folder) or whose update brings the episodes' series ids. Removed folders of other kinds reach
            // RemovedFolders anyway, and the verify sweep.
            Kinds = new HashSet<BaseItemKind>
            {
                BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video, BaseItemKind.MusicVideo, BaseItemKind.Trailer,
                BaseItemKind.Series, BaseItemKind.Season
            },
        };
    }

    /// <inheritdoc />
    public string Name => StoreName;

    /// <inheritdoc />
    public LibraryChangeFilter Filter { get; }

    /// <inheritdoc />
    public bool IsReadOnly => _store.IsReadOnly;

    /// <summary>
    /// Gets Bookmarks' relink policy: one Strong candidate only when its runtime is within 2 % and at most 60 s
    /// of the stored one, and never for a record without a stored runtime, which was made on a file whose runtime nobody
    /// knows (a 1.0 bookmark whose video was gone before the backfill looked it up; the other copy may be
    /// another cut).
    /// </summary>
    public RelinkPolicy RelinkPolicy { get; } = RelinkPolicy.RuntimeWithin(requireStoredRuntime: true);

    /// <summary>
    /// Gets the relink policy of a Jellyfin Enhanced import: as <see cref="RelinkPolicy"/>, except that
    /// an import without a stored runtime (one that never attached) takes the one Strong candidate.
    /// </summary>
    public RelinkPolicy ImportRelinkPolicy { get; } = RelinkPolicy.RuntimeWithin();

    /// <inheritdoc />
    public int DetachedRetentionDays => Math.Max(0, _configuration.GetConfiguration().DetachedRetentionDays);

    /// <inheritdoc />
    string ILibraryChangeConsumer.Name => ConsumerName;

    /// <inheritdoc />
    protected override string ServiceName => "The bookmark identity service";

    /// <summary>
    /// Whether a record's snapshot lacks what the backfill fills: a snapshot of another id, no kind, no path (a
    /// migrated 1.0 record, whose name is the provisional display name), no runtime or year, or, for an episode, its
    /// series, season and numbers.
    /// </summary>
    /// <param name="bookmark">The record.</param>
    /// <returns>Whether to look its item up.</returns>
    public static bool NeedsBackfill(Bookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        var identity = bookmark.Identity;
        return identity.ItemId != bookmark.ItemId
            || string.IsNullOrEmpty(identity.Kind)
            || identity.Path is null
            || identity.RunTimeTicks is null
            || identity.Year is null
            || (string.Equals(identity.Kind, nameof(BaseItemKind.Episode), StringComparison.Ordinal)
                && (identity.SeriesId is null || identity.SeasonId is null || identity.SeriesName is null || identity.Season is null || identity.Episode is null));
    }

    /// <summary>
    /// Completes a stored snapshot with the one captured when its item was removed (the monitor captures it in the handler):
    /// each field the capture has wins, the others are kept, so a record whose item left before the backfill
    /// looked it up still carries the runtime, path and provider ids its reattach needs.
    /// </summary>
    /// <param name="stored">The record's snapshot.</param>
    /// <param name="captured">The snapshot captured at removal, or null.</param>
    /// <returns>The completed snapshot; <paramref name="stored"/> when the capture is of another item or missing.</returns>
    public static IdentitySnapshot Complete(IdentitySnapshot stored, IdentitySnapshot? captured)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (captured is null || captured.ItemId != stored.ItemId)
        {
            return stored;
        }

        var completed = stored with
        {
            Kind = string.IsNullOrEmpty(captured.Kind) ? stored.Kind : captured.Kind,
            Name = string.IsNullOrEmpty(captured.Name) ? stored.Name : captured.Name,
            Year = captured.Year ?? stored.Year,
            ProviderIds = captured.ProviderIds.Count > 0 ? captured.ProviderIds : stored.ProviderIds,
            SeriesProviderIds = captured.SeriesProviderIds ?? stored.SeriesProviderIds,
            SeriesName = captured.SeriesName ?? stored.SeriesName,
            Season = captured.Season ?? stored.Season,
            Episode = captured.Episode ?? stored.Episode,
            SeriesId = captured.SeriesId ?? stored.SeriesId,
            SeasonId = captured.SeasonId ?? stored.SeasonId,
            AlbumId = captured.AlbumId ?? stored.AlbumId,
            RunTimeTicks = captured.RunTimeTicks ?? stored.RunTimeTicks,
            Path = captured.Path ?? stored.Path
        };
        return completed.Equals(stored) ? stored : completed;
    }

    /// <summary>
    /// Describes the service for Diagnostics (<c>Identity</c>): whether the startup work is done, the last backfill and
    /// catch-up, the move-journal position (<c>Journal</c>) and the hook's calls.
    /// </summary>
    /// <returns>A plain dictionary; counts and ids only.</returns>
    public IDictionary<string, object?> Describe()
    {
        IdentityPassResult? backfill;
        IdentityPassResult? catchUp;
        bool startupDone;
        lock (_statusLock)
        {
            backfill = _lastBackfill;
            catchUp = _lastCatchUp;
            startupDone = _startupDone;
        }

        var document = _store.IsReadOnly ? null : _store.Documents.Current;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ReadOnly"] = _store.IsReadOnly,
            ["StartupDone"] = startupDone,
            ["Backfill"] = backfill?.ToDictionary(),
            ["CatchUp"] = catchUp?.ToDictionary(),
            ["Journal"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["JournalId"] = document?.MoveJournalId,
                ["LastMoveSeq"] = document?.LastMoveSeq,
                ["HookCalls"] = ItemMovedHookV1.Calls
            },
            ["Failures"] = Failures
        };
    }

    /// <summary>
    /// Asks for a move-journal catch-up without waiting (the hook's target). Requests made while one runs are merged into
    /// the next.
    /// </summary>
    public void RequestCatchUp() => _catchUpRequests.Writer.TryWrite(true);

    /// <summary>
    /// Applies the move-journal lines after the stored position and stores the new position, in one write.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>What was done; nothing while the data is read-only.</returns>
    public async Task<IdentityPassResult> CatchUpAsync(CancellationToken cancellationToken)
    {
        if (!_gate.MayWrite(BackgroundWriter.IdentityService))
        {
            return IdentityPassResult.Skip("CatchUp", "ReadOnly", Now());
        }

        await _catchUpLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var started = Now();
            var journal = _moves.Read();
            var current = _store.Documents.Current;
            var planned = MoveJournalReader.CatchUp(journal, current.MoveJournalId, current.LastMoveSeq);
            var result = new IdentityPassResult { Pass = "CatchUp", StartedUtc = started, JournalStatus = journal.Status.ToString() };
            if (!planned.HasChanges(current.MoveJournalId, current.LastMoveSeq))
            {
                return Remember(result);
            }

            MoveJournalCatchUp? applied = null;
            var relinked = 0;
            var containers = 0;
            await _store.UpdateDocumentAsync(
                document =>
                {
                    // The stored position as the store's lock sees it (an import may have replaced the document meanwhile).
                    applied = MoveJournalReader.CatchUp(journal, document.MoveJournalId, document.LastMoveSeq);
                    relinked = 0;
                    containers = 0;
                    if (!applied.HasChanges(document.MoveJournalId, document.LastMoveSeq))
                    {
                        return document;
                    }

                    (var records, relinked, containers) = ApplyMoves(document.Bookmarks, applied.Lines);
                    return document with { Bookmarks = records, MoveJournalId = applied.JournalId, LastMoveSeq = applied.LastMoveSeq };
                },
                cancellationToken).ConfigureAwait(false);

            if (applied is { Reset: not MoveJournalReset.None } reset)
            {
                Logger.LogInformation("The move journal was replayed from the start ({Reset})", reset.Reset);
            }

            if (relinked + containers > 0)
            {
                Logger.LogInformation(
                    "Move journal: {Relinked} bookmarks followed their moved videos and {Containers} their moved series or seasons ({Lines} lines, up to {Seq})",
                    relinked,
                    containers,
                    applied?.Lines.Count ?? 0,
                    applied?.LastMoveSeq);
            }
            else
            {
                Logger.LogDebug("Move journal: {Lines} new lines, no bookmark on them (up to {Seq})", applied?.Lines.Count ?? 0, applied?.LastMoveSeq);
            }

            return Remember(result with { Checked = applied?.Lines.Count ?? 0, Updated = relinked + containers, Relinked = relinked });
        }
        finally
        {
            _catchUpLock.Release();
        }
    }

    /// <summary>
    /// The identity backfill: looks up the items of attached records whose snapshot lacks fields
    /// (<see cref="NeedsBackfill"/>) and completes the snapshots.
    /// </summary>
    /// <param name="cancellationToken">Cancels between batches and waiting for the store.</param>
    /// <returns>What was done; nothing while the data is read-only.</returns>
    public async Task<IdentityPassResult> BackfillAsync(CancellationToken cancellationToken)
    {
        if (!_gate.MayWrite(BackgroundWriter.IdentityService))
        {
            return IdentityPassResult.Skip("Backfill", "ReadOnly", Now());
        }

        var ids = _store.GetAll()
            .Where(b => BookmarkRecordRules.HasLibraryItem(b) && b.DetachedUtc is null && NeedsBackfill(b))
            .Select(b => b.ItemId)
            .Distinct()
            .ToList();
        var result = await RefreshAsync("Backfill", ids, cancellationToken).ConfigureAwait(false);
        if (result.Updated > 0)
        {
            Logger.LogInformation("Identity backfill: {Updated} bookmarks completed from {Checked} videos ({Missing} not in the library)", result.Updated, result.Checked, result.Missing);
        }
        else
        {
            Logger.LogDebug("Identity backfill: nothing to complete ({Checked} videos looked up)", result.Checked);
        }

        lock (_statusLock)
        {
            _lastBackfill = result;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task HandleAsync(LibraryChangeBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!_gate.MayWrite(BackgroundWriter.IdentityService))
        {
            return;
        }

        // 1. Removed: the records on a removed video, or on a video inside a removed series or season (one event for the
        // folder).
        var removed = new Dictionary<Guid, ItemChange>();
        foreach (var change in batch.Removed.Concat(batch.RemovedFolders))
        {
            if (change.ItemId != Guid.Empty)
            {
                removed.TryAdd(change.ItemId, change);
            }
        }

        if (removed.Count > 0)
        {
            await DetachRemovedAsync(removed, cancellationToken).ConfigureAwait(false);
        }

        // 2. The verify sweep, for what a removed folder took with it silently.
        if (batch.RemovedFolders.Count > 0)
        {
            await _verifier().SweepAsync(VerifySweepReason.RemovedFolders, cancellationToken).ConfigureAwait(false);
        }

        // 3. Updated items: fresh snapshots for the records on them, or on the episodes of an updated series or season.
        if (batch.Updated.Count > 0)
        {
            var updated = batch.Updated.Select(c => c.ItemId).ToHashSet();
            var ids = _store.GetAll()
                .Where(b => BookmarkRecordRules.HasLibraryItem(b) && b.DetachedUtc is null
                    && (updated.Contains(b.ItemId) || (b.Identity.SeriesId is { } series && updated.Contains(series)) || (b.Identity.SeasonId is { } season && updated.Contains(season))))
                .Select(b => b.ItemId)
                .Distinct()
                .ToList();
            if (ids.Count > 0)
            {
                await RefreshAsync("Refresh", ids, cancellationToken).ConfigureAwait(false);
            }
        }

        // 4. Added and updated items: the detached records re-resolved with Bookmarks' relink policy.
        if ((batch.Added.Count > 0 || batch.Updated.Count > 0) && _store.GetAll().Any(b => b.DetachedUtc is not null && BookmarkRecordRules.HasLibraryItem(b)))
        {
            await _detached().ResolveDetachedAsync(this, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<Guid> GetAttachedItemIds()
        => _store.IsReadOnly
            ? []
            : _store.GetAll().Where(b => BookmarkRecordRules.HasLibraryItem(b) && b.DetachedUtc is null).Select(b => b.ItemId).Distinct().ToList();

    /// <inheritdoc />
    public async Task<int> DetachAsync(IReadOnlyCollection<Guid> itemIds, DateTime detachedUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count == 0 || !_gate.MayWrite(BackgroundWriter.IdentityService))
        {
            return 0;
        }

        var ids = itemIds.ToHashSet();
        var detached = 0;
        await _store.UpdateDocumentAsync(
            document =>
            {
                detached = 0;
                var records = document.Bookmarks.Select(b =>
                {
                    if (b.DetachedUtc is not null || !BookmarkRecordRules.HasLibraryItem(b) || !ids.Contains(b.ItemId))
                    {
                        return b;
                    }

                    detached++;
                    return b with { DetachedUtc = detachedUtc };
                }).ToList();
                return detached == 0 ? document : document with { Bookmarks = records };
            },
            cancellationToken).ConfigureAwait(false);
        return detached;
    }

    /// <inheritdoc />
    public IReadOnlyList<DetachedRecord> GetDetached()
        => _store.IsReadOnly
            ? []
            : _store.GetAll()
                .Where(b => b.DetachedUtc is not null && BookmarkRecordRules.HasLibraryItem(b))
                .Select(b => new DetachedRecord
                {
                    Id = Key(b.Id),
                    UserId = b.UserId,
                    Identity = SnapshotOf(b),
                    AttachedUtc = b.AttachedUtc,
                    DetachedUtc = b.DetachedUtc!.Value
                })
                .ToList();

    /// <inheritdoc />
    public bool ContainsRecord(string id) => Guid.TryParse(id, out var guid) && _store.Get(guid) is not null;

    /// <summary>
    /// Gets the relink policy of one record: <see cref="ImportRelinkPolicy"/> for a Jellyfin Enhanced
    /// import, <see cref="RelinkPolicy"/> for every other record (a native one, an upgraded 1.0 one, one the store no longer
    /// holds). A live record's origin is read from the store, an archived one's from its archived copy.
    /// </summary>
    /// <param name="record">The record about to be resolved.</param>
    /// <returns>The policy.</returns>
    public RelinkPolicy GetRelinkPolicy(DetachedRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var origin = record is ArchivedRecord archived
            ? Read(archived)?.Origin
            : Guid.TryParse(record.Id, out var id) ? _store.Get(id)?.Origin : null;
        return origin == BookmarkOrigin.JellyfinEnhanced ? ImportRelinkPolicy : RelinkPolicy;
    }

    /// <inheritdoc />
    public async Task<int> ApplyResolutionsAsync(IReadOnlyList<DetachedResolution> resolutions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolutions);
        var relinks = resolutions
            .Where(r => r.Result.ShouldRelink && r.Result.ItemId is not null)
            .GroupBy(r => r.Record.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        if (relinks.Count == 0 || !_gate.MayWrite(BackgroundWriter.IdentityService))
        {
            return 0;
        }

        var relinked = 0;
        var strong = 0;
        await _store.UpdateDocumentAsync(
            document =>
            {
                relinked = 0;
                strong = 0;
                var now = Now();
                var records = document.Bookmarks.Select(b =>
                {
                    // Only a record still detached as the resolver saw it: a user's edit, a relink or a new detach since wins.
                    if (!relinks.TryGetValue(Key(b.Id), out var resolution) || b.DetachedUtc is null
                        || b.DetachedUtc != resolution.Record.DetachedUtc || !BookmarkRecordRules.HasLibraryItem(b))
                    {
                        return b;
                    }

                    relinked++;
                    strong += resolution.Result.Strength == MatchStrength.Exact ? 0 : 1;
                    return Relink(b, resolution.Result, now);
                }).ToList();
                return relinked == 0 ? document : document with { Bookmarks = records };
            },
            cancellationToken).ConfigureAwait(false);
        if (strong > 0)
        {
            Logger.LogInformation("{Strong} of {Relinked} relinked bookmarks went to another file of their video (a provider-id match): their media source was cleared and they are marked \"Relinked, check position\"", strong, relinked);
        }

        return relinked;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArchivedRecord>> ArchiveAsync(DateTime detachedBefore, Action<IReadOnlyList<ArchivedRecord>> append, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(append);
        IReadOnlyList<ArchivedRecord> archived = [];
        if (!_gate.MayWrite(BackgroundWriter.IdentityService))
        {
            return archived;
        }

        await _store.UpdateDocumentAsync(
            document =>
            {
                var due = document.Bookmarks.Where(b => IsDetachedWithoutClipFile(b) && b.DetachedUtc <= detachedBefore).ToList();
                if (due.Count == 0)
                {
                    archived = [];
                    return document;
                }

                archived = due.Select(ToArchived).ToList();

                // The archive is written and flushed first; if that throws, nothing leaves the store.
                append(archived);
                var gone = due.Select(b => b.Id).ToHashSet();
                return document with { Bookmarks = document.Bookmarks.Where(b => !gone.Contains(b.Id)).ToList() };
            },
            cancellationToken).ConfigureAwait(false);
        return archived;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> RestoreFromArchiveAsync(IReadOnlyList<DetachedResolution> matches, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(matches);
        var restored = new List<string>();
        if (!_gate.MayWrite(BackgroundWriter.IdentityService))
        {
            return restored;
        }

        await _store.UpdateDocumentAsync(
            document =>
            {
                restored.Clear();
                var now = Now();
                var live = document.Bookmarks.Select(b => b.Id).ToHashSet();
                var added = new List<Bookmark>();
                foreach (var match in matches)
                {
                    // A record archived for a reason of Bookmarks' own (a deleted bookmark's missing clip) waits for no
                    // video: the kit never re-resolves one, and none comes back here.
                    if (match.Record is not ArchivedRecord { Reason: null } archived || !match.Result.ShouldRelink || match.Result.ItemId is null
                        || Read(archived) is not { } record || !BookmarkRecordRules.HasLibraryItem(record) || !live.Add(record.Id))
                    {
                        continue;
                    }

                    added.Add(Relink(record with { DetachedUtc = record.DetachedUtc ?? archived.DetachedUtc }, match.Result, now));
                    restored.Add(Key(record.Id));
                }

                return added.Count == 0 ? document : document with { Bookmarks = [.. document.Bookmarks, .. added] };
            },
            cancellationToken).ConfigureAwait(false);
        return restored;
    }

    /// <inheritdoc />
    public async Task<string?> SnapshotBeforeAsync(string operation, CancellationToken cancellationToken)
        => await _store.Documents.PreOpSnapshotAsync(_snapshotDirectory, operation, cancellationToken).ConfigureAwait(false) is { } path
            ? System.IO.Path.GetFileName(path)
            : null;

    /// <inheritdoc />
    public async Task<int> PurgeDetachedAsync(DateTime? detachedBefore, CancellationToken cancellationToken)
    {
        if (!_gate.MayWrite(BackgroundWriter.IdentityService))
        {
            return 0;
        }

        var purged = 0;
        await _store.UpdateDocumentAsync(
            document =>
            {
                var kept = document.Bookmarks
                    .Where(b => !IsDetachedWithoutClipFile(b) || (detachedBefore is { } cutoff && b.DetachedUtc > cutoff))
                    .ToList();
                purged = document.Bookmarks.Count - kept.Count;
                return purged == 0 ? document : document with { Bookmarks = kept };
            },
            cancellationToken).ConfigureAwait(false);
        if (purged > 0)
        {
            Logger.LogWarning("An admin purged {Purged} detached bookmarks", purged);
        }

        return purged;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        ItemMovedHookV1.Detach(_hook);
        _catchUpLock.Dispose();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        // Read-only data (a newer schema's file, a failed upgrade): idle for the rest of the process.
        if (!_gate.MayWrite(BackgroundWriter.IdentityService))
        {
            return;
        }

        ItemMovedHookV1.Attach(_hook);
        try
        {
            // The journal first, so records that moved while the server (or Bookmarks) was off are on their new ids before the
            // backfill looks them up.
            await CatchUpAsync(stoppingToken).ConfigureAwait(false);
            await BackfillAsync(stoppingToken).ConfigureAwait(false);
            lock (_statusLock)
            {
                _startupDone = true;
            }

            while (await _catchUpRequests.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                while (_catchUpRequests.Reader.TryRead(out _))
                {
                    // Every request waiting is answered by the one catch-up below.
                }

                await CatchUpAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ItemMovedHookV1.Detach(_hook);
        }
    }

    /// <summary>
    /// Gets a detached bookmark as the kit's archive keeps it: its resolution fields and the record itself in
    /// the store's JSON shape. The retention step archives through it, and since 1.1 an import of an export's
    /// <c>Archive</c> (<see cref="Storage.BookmarkImportAdapter"/>).
    /// </summary>
    /// <param name="bookmark">A detached bookmark on a library item.</param>
    /// <returns>The archived record.</returns>
    internal static ArchivedRecord ToArchived(Bookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        return new ArchivedRecord
        {
            Id = Key(bookmark.Id),
            UserId = bookmark.UserId,
            Identity = SnapshotOf(bookmark),
            AttachedUtc = bookmark.AttachedUtc,
            DetachedUtc = bookmark.DetachedUtc ?? throw new ArgumentException("Only a detached bookmark is archived.", nameof(bookmark)),
            Record = JsonSerializer.SerializeToNode(bookmark, JsonStoreFormat.Options)!.AsObject()
        };
    }

    /// <summary>
    /// Gets a bookmark as the kit's archive keeps one archived for a reason of Bookmarks' own (since 1.2.0): a deleted
    /// bookmark whose clip file has been missing longer than <c>MissingClipRetentionDays</c>
    /// (<see cref="Clips.MissingClipSweep.ArchiveReason"/>), whose video may well still be in the library. Its archive line's
    /// detach time is when its reason began, and the kit never re-resolves it.
    /// </summary>
    /// <param name="bookmark">The record.</param>
    /// <param name="reason">The reason.</param>
    /// <param name="sinceUtc">When the reason began (the clip file found missing).</param>
    /// <returns>The archived record.</returns>
    internal static ArchivedRecord ToArchived(Bookmark bookmark, string reason, DateTime sinceUtc)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new ArchivedRecord
        {
            Id = Key(bookmark.Id),
            UserId = bookmark.UserId,
            Identity = SnapshotOf(bookmark),
            AttachedUtc = bookmark.AttachedUtc,
            DetachedUtc = sinceUtc,
            Record = JsonSerializer.SerializeToNode(bookmark, JsonStoreFormat.Options)!.AsObject(),
            Reason = reason
        };
    }

    private static string Key(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static IdentitySnapshot SnapshotOf(Bookmark bookmark)
        => bookmark.Identity.ItemId == bookmark.ItemId ? bookmark.Identity : bookmark.Identity with { ItemId = bookmark.ItemId };

    private static bool IsDetachedWithoutClipFile(Bookmark bookmark)
        => bookmark.DetachedUtc is not null && BookmarkRecordRules.HasLibraryItem(bookmark) && string.IsNullOrEmpty(bookmark.Clip?.FilePath);

    private static bool IsRemovedByAny(IdentitySnapshot snapshot, Dictionary<Guid, ItemChange> removed)
        => (snapshot.SeriesId is { } series && removed.ContainsKey(series))
            || (snapshot.SeasonId is { } season && removed.ContainsKey(season))
            || (snapshot.AlbumId is { } album && removed.ContainsKey(album));

    private static bool IsItemsOwnSource(string? mediaSourceId, Guid itemId)
        => Guid.TryParse(mediaSourceId, out var source) && source == itemId;

    private static Bookmark? Read(ArchivedRecord archived)
    {
        try
        {
            return archived.Record.Deserialize<Bookmark>(JsonStoreFormat.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Moves the records the lines concern: a line moves a record still on its OldId when the record was attached
    // before the line, or when an earlier line of this catch-up moved it there; a series or season line moves container ids.
    private static (List<Bookmark> Records, int Relinked, int Containers) ApplyMoves(IReadOnlyList<Bookmark> bookmarks, IReadOnlyList<MoveJournalLine> lines)
    {
        var records = bookmarks.ToList();
        var movedHere = new HashSet<Guid>();
        var containerMoved = new HashSet<Guid>();
        foreach (var line in lines)
        {
            for (var i = 0; i < records.Count; i++)
            {
                var b = records[i];
                if (!BookmarkRecordRules.HasLibraryItem(b)
                    || (!movedHere.Contains(b.Id) && b.AttachedUtc is { } attached && line.Utc <= attached))
                {
                    continue;
                }

                var snapshot = SnapshotOf(b);
                var moved = ItemIdentity.ApplyMove(snapshot, line);
                if (ReferenceEquals(moved, snapshot))
                {
                    continue;
                }

                if (b.ItemId == line.OldId)
                {
                    records[i] = b with
                    {
                        ItemId = line.NewId,
                        Identity = moved,
                        AttachedUtc = line.Utc,
                        DetachedUtc = null,
                        MediaSourceId = IsItemsOwnSource(b.MediaSourceId, line.OldId) ? line.NewId.ToString("N", CultureInfo.InvariantCulture) : b.MediaSourceId
                    };
                    movedHere.Add(b.Id);
                }
                else
                {
                    records[i] = b with { Identity = moved };
                    containerMoved.Add(b.Id);
                }
            }
        }

        return (records, movedHere.Count, containerMoved.Count(id => !movedHere.Contains(id)));
    }

    private DateTime Now() => TimeProvider.GetUtcNow().UtcDateTime;

    private IdentityPassResult Remember(IdentityPassResult result)
    {
        lock (_statusLock)
        {
            _lastCatchUp = result;
        }

        return result;
    }

    // The new item id and a fresh snapshot (or, for a pending journal relink, the stored one moved to the predicted
    // id), attached since now (or the journal line's time). Exact keeps the media source, rewritten when it was the old
    // item's own id, and the audio track; Strong clears both and sets the "Relinked, check position" mark.
    private Bookmark Relink(Bookmark bookmark, ResolveResult result, DateTime now)
    {
        var newId = result.ItemId!.Value;
        var identity = result.Item is { } item ? CaptureOrKeep(item, bookmark, newId) : SnapshotOf(bookmark) with { ItemId = newId };
        var attached = result.Source == MatchSource.Journal && result.Chain?.Steps is { Count: > 0 } steps ? steps[^1].Utc : now;
        var exact = result.Strength == MatchStrength.Exact;
        Logger.LogDebug("Bookmark {BookmarkId} relinked ({Strength}, {Source})", bookmark.Id, result.Strength, result.Source);
        return bookmark with
        {
            ItemId = newId,
            Identity = identity,
            AttachedUtc = attached,
            DetachedUtc = null,
            MediaSourceId = exact
                ? (IsItemsOwnSource(bookmark.MediaSourceId, bookmark.ItemId) ? newId.ToString("N", CultureInfo.InvariantCulture) : bookmark.MediaSourceId)
                : null,
            AudioStreamIndex = exact ? bookmark.AudioStreamIndex : null,
            RelinkedUtc = exact ? bookmark.RelinkedUtc : now,
            RelinkStrength = exact ? bookmark.RelinkStrength : MatchStrength.Strong
        };
    }

    private IdentitySnapshot CaptureOrKeep(BaseItem item, Bookmark bookmark, Guid newId)
    {
        try
        {
            return ItemIdentity.Capture(item, _library) with { Extra = bookmark.Identity.Extra };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogDebug(ex, "Could not capture the identity of item {ItemId}; bookmark {BookmarkId} keeps its snapshot", newId, bookmark.Id);
            return SnapshotOf(bookmark) with { ItemId = newId };
        }
    }

    private async Task DetachRemovedAsync(Dictionary<Guid, ItemChange> removed, CancellationToken cancellationToken)
    {
        var now = Now();
        var byItem = 0;
        var byContainer = 0;
        var completed = 0;
        await _store.UpdateDocumentAsync(
            document =>
            {
                byItem = 0;
                byContainer = 0;
                completed = 0;
                var records = document.Bookmarks.Select(b =>
                {
                    if (!BookmarkRecordRules.HasLibraryItem(b))
                    {
                        return b;
                    }

                    var snapshot = SnapshotOf(b);
                    if (b.DetachedUtc is not null)
                    {
                        // Detached already, by the verify sweep another consumer of this plugin ran for the same batch first:
                        // the snapshot still takes what the monitor captured of its removed video.
                        if (removed.TryGetValue(b.ItemId, out var earlier) && Complete(snapshot, earlier.Identity) is var more && !more.Equals(snapshot))
                        {
                            completed++;
                            return b with { Identity = more };
                        }

                        return b;
                    }

                    if (removed.TryGetValue(b.ItemId, out var change))
                    {
                        byItem++;
                        return b with { DetachedUtc = now, Identity = Complete(snapshot, change.Identity) };
                    }

                    if (IsRemovedByAny(snapshot, removed))
                    {
                        byContainer++;
                        return b with { DetachedUtc = now };
                    }

                    return b;
                }).ToList();
                return byItem + byContainer + completed > 0 ? document with { Bookmarks = records } : document;
            },
            cancellationToken).ConfigureAwait(false);
        if (byItem + byContainer > 0)
        {
            Logger.LogInformation("{ByItem} bookmarks detached whose video was removed from the library, {ByContainer} whose series or season was", byItem, byContainer);
        }

        if (completed > 0)
        {
            Logger.LogDebug("{Completed} bookmarks detached before their removal was handled took the removed videos' snapshots", completed);
        }
    }

    // Looks the items up once each, in batches with a yield, and writes the new snapshots of the attached records on them,
    // one store write per batch, skipping records whose snapshot didn't change.
    private async Task<IdentityPassResult> RefreshAsync(string pass, IReadOnlyList<Guid> itemIds, CancellationToken cancellationToken)
    {
        var result = new IdentityPassResult { Pass = pass, StartedUtc = Now() };
        var looked = 0;
        var missing = 0;
        var errors = 0;
        var updatedTotal = 0;
        for (var start = 0; start < itemIds.Count; start += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captured = new Dictionary<Guid, IdentitySnapshot>();
            foreach (var id in itemIds.Skip(start).Take(BatchSize))
            {
                looked++;
                try
                {
                    if (id != Guid.Empty && _library.GetItemById(id) is Video video)
                    {
                        captured[id] = ItemIdentity.Capture(video, _library);
                    }
                    else
                    {
                        missing++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors++;
                    Logger.LogDebug(ex, "Could not look up item {ItemId} for the bookmarks' identity", id);
                }
            }

            if (captured.Count > 0 && _gate.MayWrite(BackgroundWriter.IdentityService))
            {
                var updated = 0;
                await _store.UpdateDocumentAsync(
                    document =>
                    {
                        updated = 0;
                        var records = document.Bookmarks.Select(b =>
                        {
                            if (b.DetachedUtc is not null || !BookmarkRecordRules.HasLibraryItem(b) || !captured.TryGetValue(b.ItemId, out var identity)
                                || identity.Equals(b.Identity))
                            {
                                return b;
                            }

                            updated++;
                            return b with { Identity = identity with { Extra = b.Identity.Extra } };
                        }).ToList();
                        return updated == 0 ? document : document with { Bookmarks = records };
                    },
                    cancellationToken).ConfigureAwait(false);
                updatedTotal += updated;
            }

            await Task.Yield();
        }

        return result with { Checked = looked, Updated = updatedTotal, Missing = missing, Errors = errors, Milliseconds = (long)(Now() - result.StartedUtc).TotalMilliseconds };
    }
}
