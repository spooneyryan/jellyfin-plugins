using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Integration;
using Jellyfin.Plugin.UserRatings.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserRatings.Lifecycle;

/// <summary>
/// Keeps every rating on its item: the kit's library changes in the kit's order (Removed, then
/// the verify sweep when folders were removed, then Added and Updated), the move-journal catch-up (at startup and when
/// Library Maintenance calls <see cref="ItemMovedHookV1"/>), the ratings store as the kit's record owner (the verify sweep,
/// the re-resolution of detached rows), and the Ambiguous banner with its Activity Log entry.
/// </summary>
/// <remarks>
/// <para><b>Never detached because a library is briefly unavailable</b> (the data-safety rule). Before a rating detaches,
/// whether its item was removed (step 1) or the verify sweep found it missing, <see cref="LibraryAvailability"/> looks at its
/// stored path: while its library folder is missing, can't be read or is empty (an unmounted drive,
/// an external one for example; a mount point with nothing in it), while the file or folder is still on disk (a folder Jellyfin didn't
/// recognise), or while no library folder is known, the rating <b>stays attached</b> to the id it has. Jellyfin's item ids
/// come from their paths, so an item that comes back gets the same id and its ratings show again; one that comes back at
/// the same path under another id takes them there (an Exact match by path). Held ratings are listed in Diagnostics
/// (<c>Lifecycle.Held</c>, with when each was first held; <c>HeldCheckedUtc</c> is null until a verify sweep has looked
/// since the server started) and looked at again by every sweep, which detaches them once their folder is back and their
/// item is really gone. A hold never expires; an admin can detach held ratings at once (<see cref="DetachHeldAsync"/>, Data →
/// Detached &amp; ambiguous: Detach now). Nothing here ever deletes a rating.</para>
/// <para><b>Every rated item of a library folder at once</b>: a library folder that is there but holds
/// another drive's contents (its letter given to another volume) reads as available. When one pass (a batch's removals, or
/// a verify sweep) would detach every rated item of a library folder at once, at least <see cref="MassDetachMinItems"/> of
/// them and none of the folder's other rated items still there, they are held as
/// <see cref="LibraryAvailability.FolderUnavailable"/> instead (<c>Held[].MassDetach</c>, and a warning in the log). The
/// kit's resolver still looks at them for a batch's added and updated items, so a renamed folder that held every rated item
/// takes them along as it would detached ones; and at the startup and daily re-resolution
/// (<see cref="GetResolvable"/>) and right after a verify sweep holds them for the first time since the server started, so
/// the rename is followed when its batch never came (a restart between the scan and the batch).
/// A held rating moves only to an item in the library folder it was held in, and never onto an item its member rated too:
/// a copy in another library (which may be another drive's film), or the member's own rating there, leaves it held where it
/// is and touches nothing else, so it comes back when its item does. A former member's held rating, which the kit resolves
/// without a view, takes a copy anywhere, as their detached ones do. A mass removal split across two passes is not guarded
/// (a design limit: an unmounted drive goes in one post-scan pass, and ratings detached that way still come back by
/// id).</para>
/// <para><b>Another item at a held rating's path</b>: one of the rating's kind under another id takes it,
/// unless their strong ids contradict (another film put at the same path); a file of another kind (a Movie that came back
/// as a Video) takes it only when a strong id agrees. Otherwise the rating stops being held and is left <c>Ambiguous</c>
/// with that item as its candidate, where the admin sees it. The kit's resolver refuses such an Exact match too
/// (<see cref="RelinkPolicy.RefuseConflictingExact"/>).</para>
/// <para><b>Library changes</b>: (1) the ratings on a removed item, or on an item of a removed series, season
/// or album (one event for the folder), detach as <c>Removed</c>, their snapshot completed from the one the monitor
/// captured in the handler; (2) a batch with removed folders awaits the verify sweep, which detaches what a removed folder
/// took with it silently (a music artist's albums) as <c>Missing</c>; (3) updated items refresh the snapshots of the
/// ratings on them, compared with the stored ones after normalising the runtime (which has no column), so a metadata
/// refresh rewrites nothing; an item merged into another version (<c>PrimaryVersionId</c>) takes its ratings to the
/// primary, logged <c>Relink</c>, each one whose member can open the primary; (4) added and updated items take back held ratings at their path, then the detached
/// ratings they could match (the same id, the same path, a shared strong key, a move-journal line) are re-resolved through
/// the kit: an Exact match, or exactly one Strong candidate, relinks (<see cref="RelinkPolicy.Default"/>); several
/// candidates mark the rating <c>Ambiguous</c>. A renamed folder therefore detaches and relinks in one batch.</para>
/// <para><b>The conflict rule.</b> A rating that would move onto an item where the same member already has an attached
/// rating: the newer <c>UpdatedUtc</c> stays attached (the one already there on a tie), and the older one is detached as
/// <c>Ambiguous</c> and logged. A relink never changes what the member wrote, its stars, its times or its edit count.</para>
/// <para><b>At startup</b>, after the move-journal catch-up, the detached ratings are re-resolved once,
/// as the daily maintenance does, so a rating whose item came back while the server was off doesn't wait for 03:20.</para>
/// <para><b>Ambiguous</b>: a batch (or a catch-up, or a maintenance run) that creates new Ambiguous rows sets
/// <c>Meta.AmbiguousUtc</c> and writes one Activity Log entry; the dashboard shows the banner while
/// <c>Meta.AmbiguousUtc</c> is after <c>Meta.AmbiguousSeenUtc</c>, which the Data tab sets when the admin opens it.</para>
/// <para><b>Move journal</b>: Committed lines after the stored position (<c>Meta.MoveJournalId</c>,
/// <c>Meta.LastMoveSeq</c>), each moving the attached and detached ratings still on its <c>OldId</c> that were attached
/// before the line (a path used again gives an old id again) or moved there by an earlier line of the same catch-up,
/// under the conflict rule; a series or season line moves the container ids. A new journal id, or a journal behind the
/// stored position, replays it from the start.</para>
/// <para><b>Read-only data</b>: every pass checks the store first and does nothing while it is read-only; the kit
/// leaves a read-only owner alone.</para>
/// <para><b>The archive</b> (the detached ratings' retention): the maintenance's Archive step moves ratings detached longer than
/// <see cref="DetachedRetentionDays"/> into the kit's archive, whole and without a tombstone, and its Reattach step puts one
/// back, attached under the conflict rule, when its item is found again; held ratings are attached, so they are never
/// archived. A purge of archived lines is <see cref="ArchivedLinePurges"/>'s. The maintenance's own MergedVersions step (<see cref="MergeVersionsAsync"/>) moves
/// the ratings of versions merged while the server was off, as step 3 would have.</para>
/// <para>Logs name ratings and items by id and count them; names, review text and paths never reach Information.</para>
/// </remarks>
internal sealed class RatingsLibraryConsumer : KitBackgroundService, ILibraryChangeConsumer, IDetachedRecordOwner
{
    /// <summary>The owner's name: the archive's (<c>archive/userratings-archived.jsonl</c>) and Diagnostics'.</summary>
    public const string StoreName = "userratings";

    /// <summary>The longest <see cref="DetachedRetentionDays"/> taken (100 years); a larger setting is read as this.</summary>
    public const int MaxRetentionDays = 36500;

    /// <summary>The consumer's name in logs and Diagnostics <c>LibraryChanges</c>.</summary>
    public const string ConsumerName = "User Ratings";

    /// <summary>The <c>Meta</c> key of the move journal's id.</summary>
    public const string MoveJournalIdKey = "MoveJournalId";

    /// <summary>The <c>Meta</c> key of the last move-journal line applied.</summary>
    public const string LastMoveSeqKey = "LastMoveSeq";

    /// <summary>The <c>Meta</c> key of when new Ambiguous rows were last created.</summary>
    public const string AmbiguousUtcKey = "AmbiguousUtc";

    /// <summary>The <c>Meta</c> key of when the admin last opened Data → Detached &amp; ambiguous.</summary>
    public const string AmbiguousSeenUtcKey = "AmbiguousSeenUtc";

    /// <summary>The Activity Log entry's type.</summary>
    public const string ActivityType = "UserRatingsAmbiguous";

    /// <summary>The moderation log's reason for a rating moved to its item's primary version.</summary>
    public const string MergedVersionReason = "MergedVersion";

    /// <summary>The name of the maintenance's merged-version step (<see cref="MergeVersionsAsync"/>).</summary>
    public const string MergedVersionsStep = "MergedVersions";

    /// <summary>How many items a refresh looks up between two store writes.</summary>
    public const int BatchSize = 100;

    /// <summary>
    /// How many rated items of one library folder must be gone at once, with none of its rated items left, for the
    /// mass-detach guard to hold them (as Bookmarks' <c>AllMissingThreshold</c>): two, so one deleted item
    /// still detaches.
    /// </summary>
    public const int MassDetachMinItems = 2;

    /// <summary>The Activity Log entry's type for held ratings an admin detached.</summary>
    public const string HeldDetachedActivityType = "UserRatingsHeldDetached";

    /// <summary>The most rating ids one Activity Log entry names.</summary>
    public const int MaxIdsInActivity = 50;

    /// <summary>
    /// How many <c>PrimaryVersionId</c> links a relink follows to the primary version at the end of a chain
    /// (10.11.5 leaves A → B when B is merged into C later); a longer chain, or a cycle, leaves the rating on
    /// its version, logged once.
    /// </summary>
    public const int MaxVersionHops = 16;

    // The kinds whose item is a file, for which an item of another kind at the same path can be its replacement.
    private static readonly HashSet<string> FileKinds = new(StringComparer.Ordinal)
    {
        nameof(BaseItemKind.Movie), nameof(BaseItemKind.Episode), nameof(BaseItemKind.Video), nameof(BaseItemKind.MusicVideo),
        nameof(BaseItemKind.Trailer), nameof(BaseItemKind.Audio), nameof(BaseItemKind.AudioBook), nameof(BaseItemKind.Book)
    };

    private readonly IRatingsStore _store;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly MoveJournalReader _moves;
    private readonly Func<AttachedRecordVerifier> _verifier;
    private readonly Func<DetachedRecords> _detached;
    private readonly IActivityManager? _activity;
    private readonly Func<int> _retentionDays;
    private readonly SemaphoreSlim _catchUpLock = new(1, 1);
    private readonly Channel<bool> _catchUpRequests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly Action _hook;
    private readonly AsyncLocal<AmbiguousTally?> _tally = new();
    private readonly object _statusLock = new();
    private readonly Dictionary<Guid, HeldItem> _held = [];
    private readonly HashSet<Guid> _brokenChains = [];
    private (long Generation, IReadOnlyList<RatingsUserDataOwner.PendingPurge> Users, IReadOnlySet<Guid> Ids)? _pendingPurges;
    private IDictionary<string, object?>? _lastBatch;
    private IDictionary<string, object?>? _lastCatchUp;
    private IDictionary<string, object?>? _startupResolve;
    private bool _startupDone;
    private int _lastSweepHeld;
    private (DateTime Utc, int Count)? _archiveSuperseded;

    /// <summary>
    /// Initializes a new instance of the <see cref="RatingsLibraryConsumer"/> class.
    /// </summary>
    /// <param name="applicationHost">The server, for the startup gate.</param>
    /// <param name="store">The ratings store.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="users">The user manager, for the users the kit resolves in.</param>
    /// <param name="moves">The kit's move-journal reader.</param>
    /// <param name="verifier">The kit's verify sweep, resolved when first needed.</param>
    /// <param name="detached">The kit's detached records, resolved when first needed.</param>
    /// <param name="activity">Jellyfin's Activity Log, or null where there is none.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The lifecycle's clock (the kit's <c>Clock</c>).</param>
    /// <param name="detachedRetentionDays">The setting <c>DetachedRetentionDays</c>, read at every maintenance run;
    /// null keeps detached ratings live (0).</param>
    public RatingsLibraryConsumer(
        IServerApplicationHost applicationHost,
        IRatingsStore store,
        ILibraryManager library,
        IUserManager users,
        MoveJournalReader moves,
        Func<AttachedRecordVerifier> verifier,
        Func<DetachedRecords> detached,
        IActivityManager? activity,
        ILogger logger,
        TimeProvider? timeProvider = null,
        Func<int>? detachedRetentionDays = null)
        : base(applicationHost, logger, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(moves);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(detached);
        _store = store;
        _library = library;
        _users = users;
        _moves = moves;
        _verifier = verifier;
        _detached = detached;
        _activity = activity;
        _retentionDays = detachedRetentionDays ?? (static () => 0);
        _hook = RequestCatchUp;
        Filter = new LibraryChangeFilter
        {
            // The kinds members rate by default, the ones an admin may add (tracks, box sets, trailers), and the
            // containers whose removal takes rated items with them (series, seasons, albums: one event for the folder). A
            // removed music artist folder reaches RemovedFolders whatever the kinds, and the verify sweep; MusicArtist isn't
            // listed, so an artist by name that Jellyfin's own post-scan task deletes or creates never reaches this
            // consumer, and a rating an admin allowed on one is handled by the daily sweep.
            Kinds = new HashSet<BaseItemKind>
            {
                BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.Video,
                BaseItemKind.MusicVideo, BaseItemKind.Trailer, BaseItemKind.MusicAlbum, BaseItemKind.Audio, BaseItemKind.Book,
                BaseItemKind.AudioBook, BaseItemKind.BoxSet
            },
        };
    }

    // What an item at a held rating's path does with it (Judge).
    private enum AtPath
    {
        // It takes the rating (an Exact match by path).
        Takes,

        // Another title (strong ids that contradict): left Ambiguous.
        ConflictingProviderIds,

        // A file of another kind without a strong id in common: left Ambiguous.
        KindChanged,

        // A folder of another kind: no replacement, the hold stays.
        Ignored
    }

    /// <inheritdoc />
    public string Name => StoreName;

    /// <inheritdoc />
    public LibraryChangeFilter Filter { get; }

    /// <inheritdoc />
    public bool IsReadOnly => _store.IsReadOnly;

    /// <summary>
    /// Gets User Ratings' relink policy: an Exact match, or exactly one Strong candidate; runtimes
    /// aren't looked at (they have no column). An Exact match whose strong ids contradict the rating's (another film put
    /// at the same path gets the same id back) is refused and left Ambiguous unless one Strong candidate is found
    /// (<see cref="RelinkPolicy.RefuseConflictingExact"/>).
    /// </summary>
    public RelinkPolicy RelinkPolicy { get; } = RelinkPolicy.Default with { RefuseConflictingExact = true };

    /// <summary>
    /// Gets how long detached ratings stay in the live table before the kit's archive takes them (the
    /// detached ratings' retention): the setting <c>DetachedRetentionDays</c> (90 by default, the age of core's
    /// <c>CleanupUserDataTask</c>), read at every run; 0 keeps them live, and a larger value than
    /// <see cref="MaxRetentionDays"/> is read as it. Never a delete: archived ratings are exported and still reattach when
    /// their item comes back. Held ratings are attached, so they are never archived.
    /// </summary>
    public int DetachedRetentionDays => Math.Clamp(_retentionDays(), 0, MaxRetentionDays);

    /// <inheritdoc />
    string ILibraryChangeConsumer.Name => ConsumerName;

    /// <summary>
    /// Gets the ratings' archive (the kit's <c>archive/userratings-archived.jsonl</c>), for the users' lifecycle
    /// (<see cref="RatingsUserDataOwner"/>).
    /// </summary>
    internal DetachedArchive Archive => _detached().ArchiveOf(this);

    /// <summary>Gets the snapshot folder, where a purge of the archive puts its copy first.</summary>
    internal string ArchiveSnapshotDirectory => _detached().SnapshotDirectory;

    /// <inheritdoc />
    protected override string ServiceName => "The ratings lifecycle";

    /// <summary>
    /// Completes a stored snapshot with the one captured when its item was removed (the monitor captures it in the
    /// handler): each field the capture has wins, the others are kept, so a rating's reattach has the provider ids and
    /// path its item had last.
    /// </summary>
    /// <param name="stored">The rating's snapshot.</param>
    /// <param name="captured">The snapshot captured at removal, or null.</param>
    /// <returns>The completed snapshot; <paramref name="stored"/> when the capture is of another item or missing.</returns>
    public static IdentitySnapshot Complete(IdentitySnapshot stored, IdentitySnapshot? captured)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (captured is null || captured.ItemId != stored.ItemId)
        {
            return stored;
        }

        var completed = Normalize(stored with
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
            Path = captured.Path ?? stored.Path
        });
        return completed.Equals(stored) ? stored : completed;
    }

    /// <summary>
    /// Gets a snapshot as the store keeps it: without the runtime, which has no column, so a fresh capture compares
    /// equal to the stored row it came from (<see cref="IdentitySnapshot.Equals(IdentitySnapshot)"/> compares runtimes).
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The snapshot without its runtime.</returns>
    public static IdentitySnapshot Normalize(IdentitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.RunTimeTicks is null ? snapshot : snapshot with { RunTimeTicks = null };
    }

    /// <summary>
    /// Describes the lifecycle for Diagnostics (<c>Lifecycle</c>): the startup work (the catch-up, then one re-resolution of
    /// the detached ratings), the move-journal position and the hook's calls, the last catch-up and batch, the ratings held
    /// attached, why and since when (<c>HeldCheckedUtc</c>: when a verify sweep last looked at them, null when none has since
    /// the server started, so an empty list means "not checked yet"), the detached ratings by reason, and the Ambiguous
    /// banner's state.
    /// </summary>
    /// <returns>A plain dictionary; counts, ids, reasons and times only.</returns>
    public IDictionary<string, object?> Describe()
    {
        IDictionary<string, object?>? lastBatch;
        IDictionary<string, object?>? lastCatchUp;
        IDictionary<string, object?>? startupResolve;
        bool startupDone;
        lock (_statusLock)
        {
            lastBatch = _lastBatch;
            lastCatchUp = _lastCatchUp;
            startupResolve = _startupResolve;
            startupDone = _startupDone;
        }

        var described = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ReadOnly"] = IsReadOnly,
            ["StartupDone"] = startupDone,
            ["CatchUp"] = lastCatchUp,
            ["StartupResolve"] = startupResolve,
            ["LastBatch"] = lastBatch,
            ["Held"] = DescribeHeld(),
            ["HeldCheckedUtc"] = HeldCheckedUtc(),
            ["ArchiveSuperseded"] = DescribeArchiveSuperseded(),
            ["Failures"] = Failures
        };

        try
        {
            if (_store.IsReadOnly)
            {
                described["Journal"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["HookCalls"] = ItemMovedHookV1.Calls };
                return described;
            }

            var detached = _store.GetDetached();
            var byReason = RatingValues.DetachReason.All.Order(StringComparer.Ordinal)
                .ToDictionary(r => r, r => (object?)detached.Count(d => string.Equals(d.DetachReason, r, StringComparison.Ordinal)), StringComparer.Ordinal);
            described["Detached"] = byReason;
            described["Journal"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["JournalId"] = _store.GetMeta(MoveJournalIdKey),
                ["LastMoveSeq"] = ParseSeq(_store.GetMeta(LastMoveSeqKey)),
                ["HookCalls"] = ItemMovedHookV1.Calls
            };

            // The archive rewrites of deleted users that failed after their policy's commit: the daily
            // maintenance's ArchivePurges step runs them again.
            described["ArchivePurgesPending"] = RatingsUserDataOwner.PendingPurgesOf(_store.GetMeta(RatingsUserDataOwner.ArchivePurgesKey)).Count;

            // The ratings whose review a moderator or the member removed, whose archive lines are still to go.
            described["ArchiveIdPurgesPending"] = ArchivedLinePurges.PendingOf(_store.GetMeta(ArchivedLinePurges.Key)).Count;
            var count = detached.Count(d => string.Equals(d.DetachReason, RatingValues.DetachReason.Ambiguous, StringComparison.Ordinal));
            var since = ParseTime(_store.GetMeta(AmbiguousUtcKey));
            var seen = ParseTime(_store.GetMeta(AmbiguousSeenUtcKey));
            var banner = count > 0 && since is { } s && (seen is null || s > seen);
            described["Ambiguous"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Count"] = count,
                ["SinceUtc"] = since,
                ["SeenUtc"] = seen,
                ["Banner"] = banner,
                ["Message"] = banner ? AmbiguousBanner(count) : null
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            described["Error"] = ex.Message;
        }

        return described;
    }

    /// <summary>
    /// The dashboard banner's text (it names the other title at a rating's place too).
    /// </summary>
    /// <param name="count">The Ambiguous rows.</param>
    /// <returns>The sentence.</returns>
    public static string AmbiguousBanner(int count)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{count} {(count == 1 ? "rating" : "ratings")} could not be relinked automatically because several copies match or another title is at their place. Review them in Data → Detached & ambiguous.");

    /// <summary>
    /// The name of the Activity Log entry for held ratings an admin detached.
    /// </summary>
    /// <param name="count">The ratings detached.</param>
    /// <returns>The name.</returns>
    public static string HeldDetachedActivityName(int count)
        => count == 1
            ? "User Ratings: an admin detached 1 kept rating"
            : string.Create(CultureInfo.InvariantCulture, $"User Ratings: an admin detached {count} kept ratings");

    /// <summary>
    /// The Activity Log entry's name.
    /// </summary>
    /// <param name="count">The new Ambiguous rows.</param>
    /// <returns>The name.</returns>
    public static string AmbiguousActivityName(int count)
        => count == 1
            ? "User Ratings: 1 rating needs a manual relink"
            : string.Create(CultureInfo.InvariantCulture, $"User Ratings: {count} ratings need a manual relink");

    /// <summary>
    /// Asks for a move-journal catch-up without waiting (the hook's target). Requests made while one runs are merged into
    /// the next.
    /// </summary>
    public void RequestCatchUp() => _catchUpRequests.Writer.TryWrite(true);

    /// <summary>
    /// Records that the admin opened Data → Detached &amp; ambiguous (<c>Meta.AmbiguousSeenUtc</c>), which ends the banner
    /// until new Ambiguous rows come.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>A task.</returns>
    /// <exception cref="Kit.Api.PluginException">503 while the ratings database is read-only.</exception>
    public async Task MarkAmbiguousSeenAsync(CancellationToken cancellationToken)
    {
        _store.ThrowIfReadOnly();
        await _store.SetMetaAsync(AmbiguousSeenUtcKey, FormatTime(Now()), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the move-journal lines after the stored position and stores the new position, in one transaction.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>What was done, for Diagnostics; nothing while the data is read-only.</returns>
    public async Task<IDictionary<string, object?>> CatchUpAsync(CancellationToken cancellationToken)
    {
        var started = Now();
        if (_store.IsReadOnly)
        {
            return RememberCatchUp(Pass("CatchUp", started, "ReadOnly"));
        }

        await _catchUpLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var journal = _moves.Read();
            var storedId = ParseJournalId(_store.GetMeta(MoveJournalIdKey));
            var storedSeq = ParseSeq(_store.GetMeta(LastMoveSeqKey)) ?? 0;
            var planned = MoveJournalReader.CatchUp(journal, storedId, storedSeq);
            var result = Pass("CatchUp", started, null);
            result["JournalStatus"] = journal.Status.ToString();
            if (!planned.HasChanges(storedId, storedSeq))
            {
                return RememberCatchUp(result);
            }

            var outcome = await _store.WriteAsync(w => ApplyJournal(w, journal, Now()), cancellationToken).ConfigureAwait(false);
            if (outcome.Reset is not MoveJournalReset.None)
            {
                Logger.LogInformation("The move journal was replayed from the start ({Reset})", outcome.Reset);
            }

            LogMoves(outcome, "Move journal");
            if (outcome.Moved.Count + outcome.ContainersMoved > 0)
            {
                Logger.LogInformation(
                    "Move journal: {Relinked} ratings followed their moved items and {Containers} their moved series or seasons ({Lines} lines, up to {Seq})",
                    outcome.Moved.Count,
                    outcome.ContainersMoved,
                    outcome.Lines,
                    outcome.LastMoveSeq);
            }
            else
            {
                Logger.LogDebug("Move journal: {Lines} new lines, no rating on them (up to {Seq})", outcome.Lines, outcome.LastMoveSeq);
            }

            await NoteAmbiguousAsync(outcome.NewAmbiguous, cancellationToken).ConfigureAwait(false);
            result["Lines"] = outcome.Lines;
            result["Relinked"] = outcome.Moved.Count;
            result["Containers"] = outcome.ContainersMoved;
            result["Ambiguous"] = outcome.NewAmbiguous;
            result["LastMoveSeq"] = outcome.LastMoveSeq;
            return RememberCatchUp(result);
        }
        finally
        {
            _catchUpLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task HandleAsync(LibraryChangeBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (_store.IsReadOnly)
        {
            return;
        }

        var started = Now();
        var summary = Pass("Batch", started, null);
        summary["FullPass"] = batch.FullPass;
        summary["AfterFullPass"] = batch.AfterFullPass;
        summary["Added"] = batch.Added.Count;
        summary["Updated"] = batch.Updated.Count;
        summary["Removed"] = batch.Removed.Count;
        summary["RemovedFolders"] = batch.RemovedFolders.Count;
        var tally = new AmbiguousTally();
        var outer = _tally.Value;
        _tally.Value = tally;
        try
        {
            // 1. Removed: the ratings on a removed item, or on an item of a removed series, season or album (one event for
            // the folder), unless the item's library folder is unavailable.
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
                var (detached, held) = await DetachRemovedAsync(removed, cancellationToken).ConfigureAwait(false);
                summary["Detached"] = detached;
                summary["Held"] = held;
            }

            // 2. The verify sweep, for what a removed folder took with it silently (a music artist's albums).
            if (batch.RemovedFolders.Count > 0)
            {
                var sweep = await _verifier().SweepAsync(VerifySweepReason.RemovedFolders, cancellationToken).ConfigureAwait(false);
                var ours = sweep.Owners.FirstOrDefault(o => string.Equals(o.Owner, StoreName, StringComparison.Ordinal));
                summary["Swept"] = ours?.Detached ?? 0;

                // The ratings the sweep kept attached count in Held with step 1's; the sweep calls
                // DetachAsync only when it found items missing.
                var sweptHeld = ours is { Missing: > 0 } ? Volatile.Read(ref _lastSweepHeld) : 0;
                summary["SweptHeld"] = sweptHeld;
                summary["Held"] = (summary.TryGetValue("Held", out var stepOne) && stepOne is int held ? held : 0) + sweptHeld;
            }

            // 3. Updated items: fresh snapshots for the ratings on them (or on the episodes of an updated series or season,
            // the tracks of an updated album), and merged versions moved to their primary.
            if (batch.Updated.Count > 0)
            {
                var (refreshed, merged) = await RefreshAsync(batch.Updated.Select(c => c.ItemId).Where(id => id != Guid.Empty).ToHashSet(), cancellationToken).ConfigureAwait(false);
                summary["Refreshed"] = refreshed;
                summary["Merged"] = merged;
            }

            // 4. Added and updated items: held ratings back at their path, then the detached ratings these items could match.
            if (batch.Added.Count > 0 || batch.Updated.Count > 0)
            {
                var ids = batch.Added.Concat(batch.Updated).Select(c => c.ItemId).Where(id => id != Guid.Empty).ToHashSet();
                var (relocated, resolve) = await ReattachAsync(ids, cancellationToken).ConfigureAwait(false);
                summary["Relocated"] = relocated;
                summary["Resolve"] = resolve?.ToDictionary();
            }
        }
        finally
        {
            _tally.Value = outer;
        }

        // One Activity Log entry for the whole batch, whichever step created the Ambiguous rows.
        summary["Ambiguous"] = tally.New;
        await ReportAmbiguousAsync(tally.New, cancellationToken).ConfigureAwait(false);
        summary["Milliseconds"] = (long)(Now() - started).TotalMilliseconds;
        lock (_statusLock)
        {
            _lastBatch = summary;
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<Guid> GetAttachedItemIds() => _store.IsReadOnly ? [] : _store.GetAttachedItemIds();

    /// <summary>
    /// The verify sweep's detach: the ratings on items the library no longer has detach as <c>Missing</c>,
    /// except where <see cref="LibraryAvailability"/> says to keep them (their library folder unavailable, their file still on
    /// disk: then an item at the same path under another id takes them, an Exact match, unless it is another title or another
    /// kind, which leaves them Ambiguous) or where they would be every rated item of their library folder at once (the
    /// mass-detach guard). Held ratings that the sweep no longer lists are no longer held.
    /// </summary>
    /// <param name="itemIds">The items the sweep didn't find.</param>
    /// <param name="detachedUtc">The sweep's time.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>How many ratings were detached.</returns>
    public async Task<int> DetachAsync(IReadOnlyCollection<Guid> itemIds, DateTime detachedUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var ids = itemIds.Where(id => id != Guid.Empty).ToHashSet();
        lock (_statusLock)
        {
            foreach (var gone in _held.Keys.Where(id => !ids.Contains(id)).ToList())
            {
                _held.Remove(gone);
            }
        }

        Volatile.Write(ref _lastSweepHeld, 0);
        if (ids.Count == 0 || _store.IsReadOnly)
        {
            return 0;
        }

        var availability = LibraryAvailability.Read(_library, Logger);
        var rows = _store.GetOn(ids, includeDetached: false).Where(r => ids.Contains(r.ItemId)).ToList();
        var detach = new Dictionary<Guid, string?>();
        var relocate = new Dictionary<Guid, BaseItem>();
        var ambiguous = new Dictionary<Guid, (BaseItem Item, AtPath Why)>();
        var heldCount = 0;
        foreach (var item in rows.GroupBy(r => r.ItemId))
        {
            var first = item.First();
            var path = item.Select(r => r.Identity.Path).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            var reason = availability.HoldReason(path);
            if (reason is null)
            {
                detach[item.Key] = path;
                continue;
            }

            if (reason == LibraryAvailability.StillOnDisk && FindAnyAtPath(path) is { } atPath && atPath.Id != item.Key)
            {
                var judged = Judge(first.Identity, atPath);
                if (judged == AtPath.Takes)
                {
                    relocate[item.Key] = atPath;
                    continue;
                }

                if (judged != AtPath.Ignored)
                {
                    ambiguous[item.Key] = (atPath, judged);
                    continue;
                }
            }

            Hold(item.Key, reason, path, first.Identity.Kind, item.Select(r => r.Id).ToList(), detachedUtc);
            heldCount += item.Count();
        }

        HashSet<Guid> wasGuarded;
        lock (_statusLock)
        {
            wasGuarded = _held.Where(h => h.Value.MassDetach).Select(h => h.Key).ToHashSet();
        }

        var newlyGuarded = new HashSet<Guid>();
        foreach (var guarded in GuardMassDetach(availability, detach, "the verify sweep"))
        {
            var on = rows.Where(r => r.ItemId == guarded).ToList();
            Hold(guarded, LibraryAvailability.FolderUnavailable, detach[guarded], on[0].Identity.Kind, on.Select(r => r.Id).ToList(), detachedUtc, massDetach: true);
            heldCount += on.Count;
            detach.Remove(guarded);
            if (!wasGuarded.Contains(guarded))
            {
                newlyGuarded.Add(guarded);
            }
        }

        Volatile.Write(ref _lastSweepHeld, heldCount);
        await ResolveNewlyGuardedAsync(newlyGuarded, cancellationToken).ConfigureAwait(false);
        if (detach.Count == 0 && relocate.Count == 0 && ambiguous.Count == 0)
        {
            return 0;
        }

        var views = new Dictionary<Guid, Func<BaseItem, bool>?>();
        var outcome = await _store.WriteAsync(
            w =>
            {
                var result = new WriteOutcome();
                foreach (var row in w.GetOn(detach.Keys.Concat(relocate.Keys).Concat(ambiguous.Keys).ToList(), includeDetached: false))
                {
                    if (detach.ContainsKey(row.ItemId))
                    {
                        w.Upsert(row with { DetachedUtc = detachedUtc, DetachReason = RatingValues.DetachReason.Missing });
                        result.Detached.Add((row.Id, row.ItemId, RatingValues.DetachReason.Missing));
                    }
                    else if (relocate.TryGetValue(row.ItemId, out var target))
                    {
                        Relocate(w, row, target, detachedUtc, result, views);
                    }
                    else if (ambiguous.TryGetValue(row.ItemId, out var candidate))
                    {
                        LeaveAmbiguous(w, row, candidate.Item, candidate.Why, detachedUtc, result);
                    }
                }

                return Ambiguous(w, result, detachedUtc);
            },
            cancellationToken).ConfigureAwait(false);
        Unhold(outcome.Detached.Select(d => d.ItemId).Concat(relocate.Keys).Concat(ambiguous.Keys));
        LogDetached(outcome, "the verify sweep didn't find their item");
        LogMoves(outcome, "Path");
        LogAmbiguous(outcome);
        await NoteAmbiguousAsync(outcome.NewAmbiguous, cancellationToken).ConfigureAwait(false);
        return outcome.Detached.Count;
    }

    /// <summary>
    /// Detaches held ratings now (Data → Detached &amp; ambiguous: Detach now): a hold never expires, so an
    /// admin who knows the items are gone for good detaches the ratings kept on them, as <c>Missing</c>, each one logged in the
    /// moderation log (<c>Detach</c>, the admin, the reason it was held for and since when), the whole call in one Activity
    /// Log entry by id. An item that isn't held now is left alone: back in the library (it is no longer held), or not checked
    /// since the server started (Diagnostics' <c>HeldCheckedUtc</c> is null until the startup sweep). Whether an item is back
    /// is asked again under the write, so one back between the first look and the write keeps its
    /// ratings and counts as no longer held.
    /// </summary>
    /// <param name="itemIds">The held items whose ratings to detach.</param>
    /// <param name="adminId">The admin who asked; null for an API key.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>How many items and ratings were detached, and how many of the items asked for weren't held.</returns>
    /// <exception cref="Kit.Api.PluginException">503 while the ratings database is read-only.</exception>
    public async Task<(int Items, int Ratings, int NotHeld)> DetachHeldAsync(IReadOnlyCollection<Guid> itemIds, Guid? adminId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        _store.ThrowIfReadOnly();
        var requested = itemIds.Where(id => id != Guid.Empty).ToHashSet();
        var held = new Dictionary<Guid, HeldItem>();
        lock (_statusLock)
        {
            foreach (var id in requested)
            {
                if (_held.TryGetValue(id, out var item))
                {
                    held[id] = item;
                }
            }
        }

        // One that came back since is no longer held: never detached from an item that is there.
        var back = held.Keys.Where(id => Lookup(id) is not null).ToList();
        Unhold(back);
        foreach (var id in back)
        {
            held.Remove(id);
        }

        var notHeld = requested.Count - held.Count;
        if (held.Count == 0)
        {
            return (0, 0, notHeld);
        }

        var now = Now();
        var backNow = new HashSet<Guid>();
        var outcome = await _store.WriteAsync(
            w =>
            {
                var result = new WriteOutcome();
                var looked = new Dictionary<Guid, bool>();
                backNow.Clear();
                foreach (var row in w.GetOn(held.Keys.ToList(), includeDetached: false))
                {
                    if (!held.TryGetValue(row.ItemId, out var item))
                    {
                        continue;
                    }

                    // Asked again under the write: an item back since the look above keeps its ratings.
                    if (!looked.TryGetValue(row.ItemId, out var isBack))
                    {
                        isBack = Lookup(row.ItemId) is not null;
                        looked[row.ItemId] = isBack;
                    }

                    if (isBack)
                    {
                        backNow.Add(row.ItemId);
                        continue;
                    }

                    w.Upsert(row with { DetachedUtc = now, DetachReason = RatingValues.DetachReason.Missing });
                    result.Detached.Add((row.Id, row.ItemId, RatingValues.DetachReason.Missing));
                    w.AppendModerationLog(new ModerationLogRecord
                    {
                        AtUtc = now,
                        ActorUserId = adminId,
                        Action = RatingValues.ModerationAction.Detach,
                        RatingId = row.Id,
                        TargetUserId = row.UserId,
                        ItemId = row.ItemId,
                        Reason = item.Reason,
                        Details = string.Create(CultureInfo.InvariantCulture, $"{{\"HeldSinceUtc\":\"{FormatTime(item.SinceUtc)}\",\"MassDetach\":{(item.MassDetach ? "true" : "false")}}}")
                    });
                }

                return result;
            },
            cancellationToken).ConfigureAwait(false);
        Unhold(held.Keys);
        LogDetached(outcome, "an admin detached them from Data → Detached & ambiguous");
        var items = outcome.Detached.Select(d => d.ItemId).Distinct().Count();
        await ReportHeldDetachedAsync(outcome.Detached, adminId, cancellationToken).ConfigureAwait(false);
        return (items, outcome.Detached.Count, notHeld + backNow.Count);
    }

    /// <summary>
    /// Gets the detached ratings as the kit resolves them: with the member's id when the server knows the member (resolved in
    /// their view), without one for a former or orphaned member's (whom the kit couldn't resolve in), attached
    /// since <see cref="RatingRecord.AttachedSinceUtc"/> (for the move journal's lines).
    /// </summary>
    /// <returns>The detached ratings, oldest detach first.</returns>
    public IReadOnlyList<DetachedRecord> GetDetached() => _store.IsReadOnly ? [] : ToDetached(_store.GetDetached());

    /// <summary>
    /// Gets the ratings a full re-resolution resolves (the daily step and the startup one): the
    /// detached ones, and those the mass-detach guard holds, as detached since they were first held (as a batch's step 4
    /// passes them), so a renamed folder's ratings follow the rename even when the batch that would have taken them never
    /// came (the server restarted between the scan and the batch). <see cref="ApplyResolutionsAsync"/> moves a held one inside
    /// its library folder, onto an item its member hasn't rated, or leaves it held; Diagnostics' detached count doesn't include
    /// them.
    /// </summary>
    /// <returns>The detached ratings, then the held ones.</returns>
    public IReadOnlyList<DetachedRecord> GetResolvable()
    {
        var held = GuardHeldAsDetached(null);
        return held.Count == 0 ? GetDetached() : [.. GetDetached(), .. held];
    }

    /// <inheritdoc />
    public bool ContainsRecord(string id) => Guid.TryParse(id, out var guid) && _store.Get(guid) is not null;

    /// <inheritdoc />
    /// <remarks>An archived rating of a deleted user whose policy's rewrite is pending
    /// (<see cref="RatingsUserDataOwner.ArchivePurgesKey"/>), or of a rating whose review was removed
    /// (<see cref="ArchivedLinePurges.Key"/>), is in no export and no count. The two Meta values are read once per store
    /// generation; a store that can't be read holds nothing back (the export then fails on its own).</remarks>
    public bool IsPurgePending(ArchivedRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var (users, ids) = PendingPurges();
        return ArchivedLinePurges.Matches(ids, record) || users.Any(p => p.Matches(record));
    }

    private (IReadOnlyList<RatingsUserDataOwner.PendingPurge> Users, IReadOnlySet<Guid> Ids) PendingPurges()
    {
        var generation = _store.Generation;
        lock (_statusLock)
        {
            if (_pendingPurges is { } cached && cached.Generation == generation)
            {
                return (cached.Users, cached.Ids);
            }
        }

        try
        {
            var users = RatingsUserDataOwner.PendingPurgesOf(_store.GetMeta(RatingsUserDataOwner.ArchivePurgesKey));
            var ids = ArchivedLinePurges.PendingOf(_store.GetMeta(ArchivedLinePurges.Key)).ToHashSet();
            lock (_statusLock)
            {
                _pendingPurges = (generation, users, ids);
            }

            return (users, ids);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogDebug(ex, "The pending archive purges couldn't be read");
            return ([], new HashSet<Guid>());
        }
    }

    /// <inheritdoc />
    /// <remarks>A detached rating relinks under the conflict rule, or is marked Ambiguous. A rating the mass-detach guard holds
    /// (<see cref="GetResolvable"/>) relinks only inside the library folder it was held in and only onto an item its member
    /// has no other rating on; otherwise it stays held, attached to its own item. A relink onto a
    /// merged version lands on its primary when the rating's member can open it, on the version itself otherwise.</remarks>
    public async Task<int> ApplyResolutionsAsync(IReadOnlyList<DetachedResolution> resolutions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolutions);
        if (resolutions.Count == 0 || _store.IsReadOnly)
        {
            return 0;
        }

        // The ratings the mass-detach guard holds, which a full re-resolution and a batch's added and updated items may take
        // (GetResolvable and ReattachAsync pass them with the time they were held as their DetachedUtc), and their paths.
        Dictionary<Guid, (DateTime SinceUtc, string? Path)> guarded;
        lock (_statusLock)
        {
            guarded = _held.Where(h => h.Value.MassDetach).ToDictionary(h => h.Key, h => (h.Value.SinceUtc, h.Value.Path));
        }

        // Where each relink lands: the item, or the primary version it was merged into when the rating's member can open it
        // (so a rating never reattaches to a hidden copy, nor to one its member can't see).
        var views = new Dictionary<Guid, Func<BaseItem, bool>?>();
        var landings = new Dictionary<(Guid ItemId, Guid? UserId), Landing>();
        LibraryAvailability? availability = null;
        var elsewhere = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resolution in resolutions)
        {
            if (resolution.Result is not { ShouldRelink: true, ItemId: { } newId } result)
            {
                continue;
            }

            var record = resolution.Record;
            if (!landings.TryGetValue((newId, record.UserId), out var landing))
            {
                landing = result.Item is { } item ? Land(item, record.UserId, views) : new Landing(newId, null, null);
                landings[(newId, record.UserId)] = landing;
            }

            // (a) A held rating goes only to an item in the library folder the guard held it in (a renamed or moved folder
            // inside it, the guard's reason to resolve them): a copy in another library may be another drive's film, and the
            // held rating would never come back when its own item does. Every held rating, a former or
            // unknown member's too, which the kit resolves without a view.
            if (guarded.TryGetValue(record.Identity.ItemId, out var guard) && guard.SinceUtc == record.DetachedUtc)
            {
                availability ??= LibraryAvailability.Read(_library, Logger);
                if (!availability.InSameFolder(guard.Path ?? record.Identity.Path, landing.Path))
                {
                    elsewhere.Add(record.Id);
                }
            }
        }

        var now = Now();
        var outcome = await _store.WriteAsync(
            w =>
            {
                var written = new WriteOutcome();
                foreach (var resolution in resolutions)
                {
                    // Only a rating still detached as the resolver saw it: a member's edit, a relink or a new detach since wins.
                    // A rating the mass-detach guard holds (still attached to the item it was held on, held since the time it
                    // was passed with) moves only inside its library folder and only where its member has no other rating:
                    // otherwise it stays held where it is, and nothing else is touched. It is never detached, never marked
                    // Ambiguous, and never pushes out another rating through the conflict rule; it stops being held only once
                    // it has moved.
                    if (!Guid.TryParse(resolution.Record.Id, out var id) || w.Get(id) is not { } row)
                    {
                        continue;
                    }

                    var stillDetached = row.DetachedUtc is { } detachedUtc && detachedUtc == resolution.Record.DetachedUtc;
                    var held = row.DetachedUtc is null && resolution.Record.Identity.ItemId == row.ItemId
                        && guarded.TryGetValue(row.ItemId, out var guard) && guard.SinceUtc == resolution.Record.DetachedUtc;
                    if (!stillDetached && !held)
                    {
                        continue;
                    }

                    var result = resolution.Result;
                    if (result is { ShouldRelink: true, ItemId: { } newId })
                    {
                        if (held && elsewhere.Contains(resolution.Record.Id))
                        {
                            continue;
                        }

                        var target = landings[(newId, resolution.Record.UserId)];
                        var attached = result.Source == MatchSource.Journal && result.Chain?.Last is { } last ? last.Utc : now;
                        var from = row.ItemId;

                        // (b) A held rating never goes through the conflict rule: the member's own rating there stays as it is.
                        var moved = !(held && MemberRatedOther(w, row, target.Id))
                            && Attach(w, row, target.Id, target.Identity ?? row.Identity with { ItemId = target.Id }, attached, now, written);
                        if (moved)
                        {
                            written.Moved.Add((row.Id, from, target.Id, result.Strength.ToString(), result.Source.ToString()));
                        }

                        // (c) Released from its hold only when it moved.
                        if (held && moved)
                        {
                            written.Unheld.Add((from, row.Id));
                        }
                    }
                    else if (stillDetached && result.Outcome == ResolveOutcome.Ambiguous && row.DetachReason != RatingValues.DetachReason.Ambiguous)
                    {
                        w.Upsert(row with { DetachReason = RatingValues.DetachReason.Ambiguous });
                        written.NewAmbiguous++;
                        written.Ambiguous.Add((row.Id, row.ItemId, result.Reason.ToString(), result.Candidates.Select(c => c.ItemId).ToList()));
                    }
                }

                return Ambiguous(w, written, now);
            },
            cancellationToken).ConfigureAwait(false);
        LogMoves(outcome, "Resolve");
        LogAmbiguous(outcome);
        Release(outcome.Unheld);
        if (elsewhere.Count > 0)
        {
            Logger.LogDebug("{Count} ratings the mass-detach guard holds stay held: their match is in another library folder", elsewhere.Count);
        }

        await NoteAmbiguousAsync(outcome.NewAmbiguous, cancellationToken).ConfigureAwait(false);
        return outcome.Moved.Count;
    }

    /// <inheritdoc />
    /// <remarks>The kit's archive step (the detached ratings' retention): the ratings detached at or before the cutoff, of every
    /// reason (Missing, Ambiguous, a JE import that never matched), each still detached as it was listed, go to
    /// <c>archive/userratings-archived.jsonl</c> whole (the row in the line's <c>Record</c>, <see cref="ArchivedRating"/>) and
    /// leave the live table without a tombstone. A rating the mass-detach guard holds, or one held while its library folder
    /// is unavailable, is attached, so it is never archived.</remarks>
    public async Task<IReadOnlyList<ArchivedRecord>> ArchiveAsync(DateTime detachedBefore, Action<IReadOnlyList<ArchivedRecord>> append, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(append);
        if (_store.IsReadOnly)
        {
            return [];
        }

        var due = _store.GetDetached().Where(r => r.DetachedUtc <= detachedBefore).ToList();
        if (due.Count == 0)
        {
            return [];
        }

        var known = due.Select(r => r.UserId).Distinct().ToDictionary(u => u, KnowsUser);
        var archived = await _store.WriteAsync(
            w =>
            {
                // Only a rating still detached as it was listed: a relink or a member's write since keeps it live.
                var rows = due.Select(r => (Listed: r, Row: w.Get(r.Id)))
                    .Where(p => p.Row is { DetachedUtc: { } d } && d == p.Listed.DetachedUtc)
                    .Select(p => p.Row!)
                    .ToList();
                if (rows.Count == 0)
                {
                    return (IReadOnlyList<ArchivedRecord>)[];
                }

                var records = rows.Select(r => ArchivedRating.ToArchive(r, known[r.UserId])).ToList();

                // The archive's write and flush, inside the transaction: if it throws, nothing leaves the table.
                append(records);
                foreach (var row in rows)
                {
                    w.RemoveArchived(row.Id);
                }

                return (IReadOnlyList<ArchivedRecord>)records;
            },
            cancellationToken).ConfigureAwait(false);
        foreach (var record in archived)
        {
            Logger.LogInformation("Rating {RatingId} archived: detached since {DetachedUtc:O}, longer than {Days} days", Guid.Parse(record.Id), record.DetachedUtc, DetachedRetentionDays);
        }

        return archived;
    }

    /// <inheritdoc />
    /// <remarks>The kit's reattach from the archive: an archived rating whose item was found goes back into the live
    /// table, attached to it, on a merged version's primary when its member can open it. One that would only lose the
    /// conflict rule there (its member's rating of the item is as new or newer) stays in the archive and is counted in
    /// Diagnostics (<c>ArchiveSuperseded</c>; back as Ambiguous with its old detach time, it was
    /// archived again the next day, and came back again). One whose id is live again, whose record this version can't read,
    /// whose line the archive no longer holds as the resolver read it (purged or re-keyed for a deleted or remapped member
    /// since), or whose id has a tombstone (a deleted member's rating whose line a
    /// pending rewrite hasn't taken out yet), stays where it is.</remarks>
    public async Task<IReadOnlyList<string>> RestoreFromArchiveAsync(IReadOnlyList<DetachedResolution> matches, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Count == 0 || _store.IsReadOnly)
        {
            return [];
        }

        var views = new Dictionary<Guid, Func<BaseItem, bool>?>();
        var landings = new Dictionary<(Guid ItemId, Guid? UserId), Landing>();
        var rows = new List<(string Id, long Seq, RatingRecord Row, Landing Target, ResolveResult Result)>();
        foreach (var match in matches)
        {
            if (match.Record is not ArchivedRecord archived || match.Result is not { ShouldRelink: true, ItemId: { } newId } result)
            {
                continue;
            }

            if (ArchivedRating.FromArchive(archived) is not { } row)
            {
                Logger.LogWarning("Archived rating {RatingId} is not a rating this version reads; it stays in the archive", archived.Id);
                continue;
            }

            if (!landings.TryGetValue((newId, archived.UserId), out var landing))
            {
                landing = result.Item is { } item ? Land(item, archived.UserId, views) : new Landing(newId, null, null);
                landings[(newId, archived.UserId)] = landing;
            }

            rows.Add((archived.Id, archived.Seq, row, landing, result));
        }

        if (rows.Count == 0)
        {
            return [];
        }

        var now = Now();
        var archive = _detached().ArchiveOf(this);
        var superseded = 0;
        var (outcome, restored) = await _store.WriteAsync(
            w =>
            {
                var written = new WriteOutcome();
                var back = new List<string>();
                superseded = 0;

                // The lines as they are now, under the store's lock that the users' lifecycle writes the archive under too.
                var lines = archive.Read().Records.ToDictionary(r => r.Id, r => r.Seq, StringComparer.Ordinal);
                foreach (var (id, seq, row, target, result) in rows)
                {
                    if (w.Get(row.Id) is not null || !lines.TryGetValue(id, out var lineSeq) || lineSeq != seq || w.HasTombstone(row.Id))
                    {
                        continue;
                    }

                    // Its member's rating of the item is as new or newer: back, it would only be detached again as Ambiguous.
                    if (w.GetAttached(row.UserId, target.Id) is { } other && other.Id != row.Id && row.UpdatedUtc <= other.UpdatedUtc)
                    {
                        superseded++;
                        continue;
                    }

                    // Back as it was archived (detached), then attached as a resolver's relink is.
                    w.Upsert(row);
                    var attached = result.Source == MatchSource.Journal && result.Chain?.Last is { } last ? last.Utc : now;
                    if (Attach(w, row, target.Id, target.Identity ?? row.Identity with { ItemId = target.Id }, attached, now, written))
                    {
                        written.Moved.Add((row.Id, row.ItemId, target.Id, result.Strength.ToString(), result.Source.ToString()));
                    }

                    back.Add(id);
                }

                return (Ambiguous(w, written, now), (IReadOnlyList<string>)back);
            },
            cancellationToken).ConfigureAwait(false);
        LogMoves(outcome, "Reattach from the archive");
        LogAmbiguous(outcome);
        lock (_statusLock)
        {
            _archiveSuperseded = (now, superseded);
        }

        if (superseded > 0)
        {
            Logger.LogInformation("{Count} archived ratings stay in the archive: their members rated the items again since", superseded);
        }

        await NoteAmbiguousAsync(outcome.NewAmbiguous, cancellationToken).ConfigureAwait(false);
        return restored;
    }

    /// <summary>
    /// The daily maintenance's merged-version step (<see cref="MergedVersionsStep"/>): versions merged while
    /// the server was off, or by a change no batch carried, never reach step 3 of a batch, so every rated item is looked up
    /// and the attached ratings on one that is now a merged version (<c>PrimaryVersionId</c>, its primary in the library)
    /// move to the primary as step 3 moves them: under the conflict rule, each one only when its member can open the primary
    /// (the others stay on the version and are counted in <c>Stayed</c>), logged <c>Relink</c> with the
    /// reason <c>MergedVersion</c>, with one Activity Log entry for the Ambiguous rows the run created. Nothing while the
    /// data is read-only.
    /// </summary>
    /// <param name="cancellationToken">Cancels the step between items.</param>
    /// <returns>What was done, for Diagnostics (the maintenance's result).</returns>
    public async Task<IDictionary<string, object?>> MergeVersionsAsync(CancellationToken cancellationToken)
    {
        var started = Now();
        if (_store.IsReadOnly)
        {
            return Pass(MergedVersionsStep, started, "ReadOnly");
        }

        var ids = _store.GetAttachedItemIds();
        var merged = new HashSet<Guid>();
        var views = new Dictionary<Guid, Func<BaseItem, bool>?>();
        for (var i = 0; i < ids.Count; i++)
        {
            if (i % BatchSize == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            if (Lookup(ids[i]) is { } item && PrimaryOf(item, null, views) is not null)
            {
                merged.Add(ids[i]);
            }
        }

        var result = Pass(MergedVersionsStep, started, null);
        result["Checked"] = ids.Count;
        result["MergedItems"] = merged.Count;
        var moved = 0;
        var tally = new AmbiguousTally();
        if (merged.Count > 0 && !_store.IsReadOnly)
        {
            var outer = _tally.Value;
            _tally.Value = tally;
            try
            {
                (_, moved) = await RefreshAsync(merged, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _tally.Value = outer;
            }

            await ReportAmbiguousAsync(tally.New, cancellationToken).ConfigureAwait(false);
        }

        result["Moved"] = moved;
        result["Stayed"] = merged.Count == 0 ? 0 : _store.GetOn(merged, includeDetached: false).Count(r => merged.Contains(r.ItemId));
        result["Ambiguous"] = tally.New;
        if (moved > 0)
        {
            Logger.LogInformation("Merged versions: {Moved} ratings moved to the primary versions of {Items} merged items", moved, merged.Count);
        }

        return result;
    }

    /// <inheritdoc />
    /// <remarks>None: no route purges through this owner.</remarks>
    public Task<string?> SnapshotBeforeAsync(string operation, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <inheritdoc />
    /// <remarks>Nothing: this owner deletes no detached rating.</remarks>
    public Task<int> PurgeDetachedAsync(DateTime? detachedBefore, CancellationToken cancellationToken) => Task.FromResult(0);

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
        ItemMovedHookV1.Attach(_hook);
        try
        {
            // The ratings that moved while the server (or User Ratings) was off follow their items first (a catch-up does
            // nothing while the data is read-only; the next hook call or start tries again).
            await CatchUpAsync(stoppingToken).ConfigureAwait(false);

            // Then the detached ratings once: an item back while the server was off takes its ratings now,
            // not at the next 03:20.
            var resolved = await ResolveAtStartupAsync(stoppingToken).ConfigureAwait(false);
            lock (_statusLock)
            {
                _startupResolve = resolved;
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

    private static Guid? ParseJournalId(string? text) => Guid.TryParse(text, out var id) ? id : null;

    private static long? ParseSeq(string? text) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq) ? seq : null;

    private static DateTime? ParseTime(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            ? time.Kind switch
            {
                DateTimeKind.Utc => time,
                DateTimeKind.Local => time.ToUniversalTime(),
                _ => DateTime.SpecifyKind(time, DateTimeKind.Utc)
            }
            : null;
    }

    // A Meta time as the store writes its times (round-trip, UTC), without naming RatingsSql: only the store's cores and
    // its writer may, since RatingsSql names SQLite types (PublicBoundaryTests).
    private static string FormatTime(DateTime utc)
        => (utc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc.ToUniversalTime()).ToString("O", CultureInfo.InvariantCulture);

    // Moves the ratings the lines concern: a line moves a rating still on its OldId when the rating was attached
    // before the line, or when an earlier line of this catch-up moved it there; a series or season line moves container ids.
    private static List<(RatingRecord Row, IdentitySnapshot Identity, DateTime? MovedUtc)> ApplyMoves(IReadOnlyList<RatingRecord> rows, IReadOnlyList<MoveJournalLine> lines)
    {
        var current = rows.Select(r => (Row: r, Identity: r.Identity, MovedUtc: (DateTime?)null)).ToList();
        foreach (var line in lines)
        {
            for (var i = 0; i < current.Count; i++)
            {
                var (row, identity, movedUtc) = current[i];
                if (movedUtc is null && line.Utc <= row.AttachedSinceUtc)
                {
                    continue;
                }

                var moved = ItemIdentity.ApplyMove(identity, line);
                if (!ReferenceEquals(moved, identity))
                {
                    current[i] = (row, moved, identity.ItemId == line.OldId ? line.Utc : movedUtc);
                }
            }
        }

        return current.Where(c => !ReferenceEquals(c.Identity, c.Row.Identity)).ToList();
    }

    // The conflict rule: puts a rating on an item unless the member has another attached rating there; then the newer
    // UpdatedUtc stays attached (the one already there on a tie) and the older one is detached as Ambiguous. The rating's own
    // fields (stars, text, times, edit count) never change. Returns whether it moved.
    private static bool Attach(IRatingsWriter writer, RatingRecord row, Guid itemId, IdentitySnapshot identity, DateTime attachedUtc, DateTime now, WriteOutcome outcome)
    {
        var other = writer.GetAttached(row.UserId, itemId);
        if (other is not null && other.Id != row.Id)
        {
            if (row.UpdatedUtc > other.UpdatedUtc)
            {
                writer.Upsert(other with { DetachedUtc = now, DetachReason = RatingValues.DetachReason.Ambiguous });
                outcome.NewAmbiguous++;
                outcome.Conflicts.Add((other.Id, itemId, row.Id));
            }
            else
            {
                if (row.DetachReason != RatingValues.DetachReason.Ambiguous)
                {
                    writer.Upsert(row with { DetachedUtc = row.DetachedUtc ?? now, DetachReason = RatingValues.DetachReason.Ambiguous });
                    outcome.NewAmbiguous++;
                }

                outcome.Conflicts.Add((row.Id, itemId, other.Id));
                return false;
            }
        }

        writer.Upsert(row with { Identity = Normalize(identity with { ItemId = itemId }), DetachedUtc = null, DetachReason = null, AttachedUtc = attachedUtc });
        return true;
    }

    // Records when new Ambiguous rows were created (the banner's Meta.AmbiguousUtc), in the transaction that created them.
    private static T Ambiguous<T>(IRatingsWriter writer, T outcome, DateTime now)
        where T : WriteOutcome
    {
        if (outcome.NewAmbiguous > 0)
        {
            writer.SetMeta(AmbiguousUtcKey, FormatTime(now));
        }

        return outcome;
    }

    private static Dictionary<string, object?> Pass(string pass, DateTime started, string? skipped) => new(StringComparer.Ordinal)
    {
        ["Pass"] = pass,
        ["StartedUtc"] = started,
        ["Skipped"] = skipped
    };

    private DateTime Now() => TimeProvider.GetUtcNow().UtcDateTime;

    private IDictionary<string, object?> RememberCatchUp(IDictionary<string, object?> result)
    {
        lock (_statusLock)
        {
            _lastCatchUp = result;
        }

        return result;
    }

    // The last reattach from the archive's ratings left there because their members rated the items again since.
    private Dictionary<string, object?>? DescribeArchiveSuperseded()
    {
        lock (_statusLock)
        {
            return _archiveSuperseded is { } last
                ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["LastRunUtc"] = last.Utc, ["Count"] = last.Count }
                : null;
        }
    }

    private List<IDictionary<string, object?>> DescribeHeld()
    {
        PruneHeld();
        lock (_statusLock)
        {
            return _held.OrderBy(h => h.Value.SinceUtc).ThenBy(h => h.Key)
                .Select(h => (IDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ItemId"] = h.Key,
                    ["Reason"] = h.Value.Reason,
                    ["MassDetach"] = h.Value.MassDetach,
                    ["SinceUtc"] = h.Value.SinceUtc,
                    ["RatingIds"] = h.Value.RatingIds
                })
                .ToList();
        }
    }

    // A rating kept attached although its item is gone (the data-safety rule): logged once when it starts, with ids only.
    // It is held since the first time, whatever the reason later.
    private void Hold(Guid itemId, string reason, string? path, string kind, IReadOnlyList<Guid> ratingIds, DateTime now, bool massDetach = false)
    {
        bool known;
        lock (_statusLock)
        {
            var exists = _held.TryGetValue(itemId, out var before);
            known = exists && before!.Reason == reason && before.MassDetach == massDetach;
            _held[itemId] = new HeldItem(reason, exists ? before!.SinceUtc : now, path, kind, ratingIds, massDetach);
        }

        if (known)
        {
            Logger.LogDebug("{Count} ratings on item {ItemId} stay attached ({Reason})", ratingIds.Count, itemId, reason);
        }
        else
        {
            Logger.LogInformation(
                "{Count} ratings on item {ItemId} stay attached although the item is gone: {Reason} ({RatingIds})",
                ratingIds.Count,
                itemId,
                reason,
                string.Join(", ", ratingIds));
        }
    }

    // Held items the library has again (back with their own id) are no longer held.
    private void PruneHeld()
    {
        List<Guid> held;
        lock (_statusLock)
        {
            held = [.. _held.Keys];
        }

        Unhold(held.Where(id => Lookup(id) is not null).ToList());
    }

    private void Unhold(IEnumerable<Guid> itemIds)
    {
        lock (_statusLock)
        {
            foreach (var id in itemIds)
            {
                _held.Remove(id);
            }
        }
    }

    // Ratings that left their held items (moved, or detached as Ambiguous): each leaves its item's list (Diagnostics'
    // Held[].RatingIds, which the Data tab counts), and an item none of whose ratings is attached to it any more is no
    // longer held. A rating that stayed is never released.
    private void Release(IReadOnlyCollection<(Guid ItemId, Guid RatingId)> left)
    {
        if (left.Count == 0)
        {
            return;
        }

        lock (_statusLock)
        {
            foreach (var item in left.GroupBy(l => l.ItemId))
            {
                if (_held.TryGetValue(item.Key, out var held))
                {
                    var gone = item.Select(l => l.RatingId).ToHashSet();
                    _held[item.Key] = held with { RatingIds = held.RatingIds.Where(id => !gone.Contains(id)).ToList() };
                }
            }
        }

        UnholdEmptied(left.Select(l => l.ItemId));
    }

    // Held items whose ratings all moved elsewhere (the kit's resolver took the mass-detach guard's) are no longer held.
    private void UnholdEmptied(IEnumerable<Guid> itemIds)
    {
        var ids = itemIds.Distinct().ToList();
        if (ids.Count == 0 || _store.IsReadOnly)
        {
            return;
        }

        var still = _store.GetOn(ids, includeDetached: false).Select(r => r.ItemId).ToHashSet();
        Unhold(ids.Where(id => !still.Contains(id)));
    }

    // When a verify sweep last looked at the held ratings: the last sweep's start, provided it got as far as this owner; null
    // while none has since the server started (the list is built by the sweeps and kept in memory only).
    private DateTime? HeldCheckedUtc()
    {
        try
        {
            return _verifier().LastSweep is { } sweep
                && sweep.Owners.Any(o => string.Equals(o.Owner, StoreName, StringComparison.Ordinal) && o.Skipped is null && o.Error is null)
                ? sweep.StartedUtc
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogDebug(ex, "Could not read the verify sweep's last run");
            return null;
        }
    }

    // The ratings the mass-detach guard holds (on the given items, or all of them), as detached since each item was first
    // held: the kit's resolver takes them as it takes a detached rating, and ApplyResolutionsAsync knows them by that time.
    private List<DetachedRecord> GuardHeldAsDetached(IReadOnlySet<Guid>? itemIds)
    {
        var rows = GuardHeldRows()
            .Where(g => itemIds is null || itemIds.Contains(g.Row.ItemId))
            .Select(g => g.Row with { DetachedUtc = g.SinceUtc })
            .ToList();
        return rows.Count == 0 ? [] : ToDetached(rows);
    }

    // The ratings a verify sweep's mass-detach guard holds for the first time since the server started, resolved at once:
    // after a restart the startup sweep holds a renamed folder's ratings again, and they follow the
    // rename now rather than at the next daily run. A failure is logged; the daily resolve tries again.
    private async Task ResolveNewlyGuardedAsync(IReadOnlySet<Guid> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return;
        }

        var held = GuardHeldAsDetached(itemIds);
        if (held.Count == 0)
        {
            return;
        }

        try
        {
            var result = await _detached().ResolveDetachedAsync(this, held, cancellationToken).ConfigureAwait(false);
            if (result.Relinked > 0)
            {
                Logger.LogInformation("{Relinked} of {Checked} ratings the mass-detach guard holds followed their items", result.Relinked, result.Checked);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "The ratings the mass-detach guard holds couldn't be resolved now; the daily maintenance tries again");
        }
    }

    // The ratings the mass-detach guard holds, with the time each item was first held, for the kit's resolver.
    private List<(RatingRecord Row, DateTime SinceUtc)> GuardHeldRows()
    {
        Dictionary<Guid, DateTime> guarded;
        lock (_statusLock)
        {
            guarded = _held.Where(h => h.Value.MassDetach).ToDictionary(h => h.Key, h => h.Value.SinceUtc);
        }

        if (guarded.Count == 0 || _store.IsReadOnly)
        {
            return [];
        }

        return _store.GetOn(guarded.Keys.ToList(), includeDetached: false)
            .Where(r => guarded.ContainsKey(r.ItemId))
            .Select(r => (r, guarded[r.ItemId]))
            .ToList();
    }

    // The mass-detach guard: of the items one pass would detach, those that are every rated item of their
    // library folder at once, at least MassDetachMinItems of them, with none of the folder's other rated items there (held
    // for a reason of its own, a file still on disk, proves the folder). Items the guard already holds count as gone. A
    // path in no library folder is not guarded.
    private HashSet<Guid> GuardMassDetach(LibraryAvailability availability, IReadOnlyDictionary<Guid, string?> wouldDetach, string pass)
    {
        var guardedFolders = wouldDetach
            .Select(d => (Item: d.Key, Folder: availability.FolderOf(d.Value)))
            .Where(d => d.Folder is not null)
            .GroupBy(d => d.Folder!, LibraryAvailability.PathComparer)
            .ToList();
        if (guardedFolders.Count == 0)
        {
            return [];
        }

        HashSet<Guid> alreadyGuarded;
        lock (_statusLock)
        {
            alreadyGuarded = _held.Where(h => h.Value.MassDetach).Select(h => h.Key).ToHashSet();
        }

        // Every attached rated item, by library folder.
        var attached = _store.GetOn(_store.GetAttachedItemIds(), includeDetached: false)
            .GroupBy(r => r.ItemId)
            .Select(g => (Item: g.Key, Folder: availability.FolderOf(g.Select(r => r.Identity.Path).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)))))
            .Where(a => a.Folder is not null)
            .ToLookup(a => a.Folder!, a => a.Item, LibraryAvailability.PathComparer);
        var held = new HashSet<Guid>();
        foreach (var folder in guardedFolders)
        {
            var gone = folder.Select(d => d.Item).ToHashSet();
            var others = attached[folder.Key].Where(id => !gone.Contains(id)).ToList();
            var there = others.Count(id => !alreadyGuarded.Contains(id));
            var total = gone.Count + (others.Count - there);
            if (there > 0 || total < MassDetachMinItems)
            {
                continue;
            }

            held.UnionWith(gone);
            Logger.LogWarning(
                "{Items} rated items of one library folder would detach at once in {Pass} ({Total} rated items there in all, none left), so their ratings stay attached as {Reason}: the folder may hold another drive's contents. Data → Detached & ambiguous lists them and can detach them",
                gone.Count,
                pass,
                total,
                LibraryAvailability.FolderUnavailable);
        }

        return held;
    }

    // Any item at a path, its kind judged by the caller (Judge), alternate versions and owned items included (Jellyfin 12
    // hides them from FindByPath); a blank path is never looked up (10.11.5's FindByPath then answers with the newest item
    // of the whole library).
    private BaseItem? FindAnyAtPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return KitItemQueries.FindAnyByPath(_library, path, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogDebug(ex, "Could not look up the item at a rated item's path");
            return null;
        }
    }

    private List<DetachedRecord> ToDetached(IReadOnlyList<RatingRecord> rows)
    {
        var known = new Dictionary<Guid, bool>();
        return rows.Where(r => r.DetachedUtc is not null).Select(r =>
        {
            if (!known.TryGetValue(r.UserId, out var isKnown))
            {
                isKnown = KnowsUser(r.UserId);
                known[r.UserId] = isKnown;
            }

            return new DetachedRecord
            {
                Id = r.Id.ToString("D", CultureInfo.InvariantCulture),
                UserId = isKnown ? r.UserId : null,
                Identity = r.Identity,
                AttachedUtc = r.AttachedSinceUtc,
                DetachedUtc = r.DetachedUtc!.Value
            };
        }).ToList();
    }

    private bool KnowsUser(Guid userId)
    {
        try
        {
            return userId != Guid.Empty && _users.GetUserById(userId) is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogDebug(ex, "Could not look up user {UserId}; their detached ratings are resolved without their view", userId);
            return false;
        }
    }

    // The item, when the library still has it and every parent up its chain (the kit's
    // LibraryPresence): 10.11.5's cache keeps a deleted music artist folder's albums and tracks, which no query lists.
    private BaseItem? Lookup(Guid id)
    {
        try
        {
            return LibraryPresence.Find(_library, id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogDebug(ex, "Could not look up item {ItemId} for the ratings' identity", id);
            return null;
        }
    }

    private IdentitySnapshot? CaptureOrNull(BaseItem item)
    {
        try
        {
            return Normalize(ItemIdentity.Capture(item, _library));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogDebug(ex, "Could not capture the identity of item {ItemId}", item.Id);
            return null;
        }
    }

    // Whether the rating's member has another attached rating on the item (the conflict rule's case).
    private static bool MemberRatedOther(IRatingsWriter writer, RatingRecord row, Guid itemId)
        => writer.GetAttached(row.UserId, itemId) is { } other && other.Id != row.Id;

    // Where a member's rating lands on an item: the item itself, or its primary version when it was merged into another and
    // the member can open the primary (PrimaryOf), with the landing item's snapshot and path. A rating with no member the
    // server knows (null) is looked at without a view, as the kit resolves it.
    private Landing Land(BaseItem item, Guid? userId, IDictionary<Guid, Func<BaseItem, bool>?> views)
    {
        var landing = PrimaryOf(item, userId, views) ?? item;
        return new Landing(landing.Id, CaptureOrNull(landing), landing.Path);
    }

    // The primary version an item was merged into (PrimaryVersionId: a string on 10.11.5, a Guid? on 12), followed to the end of a chain
    // (10.11.5 leaves A → B when B is merged into C later) while the library has the next one (the
    // kit's LibraryPresence), and, for a member the server knows, when that member can open the last one
    // (a primary in a library the member can't open would hide their rating from them); null otherwise, and the rating
    // stays on the item. A chain longer than MaxVersionHops, or a cycle, leaves it on the item too, logged once an item.
    private BaseItem? PrimaryOf(BaseItem item, Guid? userId, IDictionary<Guid, Func<BaseItem, bool>?> views)
    {
        var visited = new HashSet<Guid> { item.Id };
        var current = item;
        var hops = 0;
        while (current is Video video && KitItemQueries.PrimaryVersionGuid(video) is { } nextId && nextId != Guid.Empty && nextId != current.Id)
        {
            if (hops == MaxVersionHops || !visited.Add(nextId))
            {
                bool first;
                lock (_statusLock)
                {
                    first = _brokenChains.Add(item.Id);
                }

                if (first)
                {
                    Logger.LogWarning("Item {ItemId}'s merged versions form {What}; its ratings stay on it", item.Id, hops == MaxVersionHops ? "a chain longer than " + MaxVersionHops.ToString(CultureInfo.InvariantCulture) : "a cycle");
                }

                return null;
            }

            if (Lookup(nextId) is not { } next)
            {
                break;
            }

            current = next;
            hops++;
        }

        if (ReferenceEquals(current, item))
        {
            return null;
        }

        return userId is not { } id || CanOpen(id, current, views) ? current : null;
    }

    // Whether a member can open an item (the kit's IdentityResolver.VisibleTo: in the library, and GetItemById in their view);
    // a member the server doesn't know (a former or orphaned one) has no view, and only the library is asked. A member the
    // server couldn't be asked about can open nothing, so the rating stays on its version.
    private bool CanOpen(Guid userId, BaseItem item, IDictionary<Guid, Func<BaseItem, bool>?> views)
    {
        if (!views.TryGetValue(userId, out var visible))
        {
            try
            {
                var user = userId == Guid.Empty ? null : _users.GetUserById(userId);
                visible = user is null ? null : IdentityResolver.VisibleTo(_library, user);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogDebug(ex, "Could not look up user {UserId}; their rating stays on its version", userId);
                visible = static _ => false;
            }

            views[userId] = visible;
        }

        try
        {
            return visible is null || visible(item);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogDebug(ex, "Could not tell whether user {UserId} can open item {ItemId}", userId, item.Id);
            return false;
        }
    }

    // What an item at a held rating's path under another id does with it: one of the rating's kind takes it
    // unless their strong ids contradict (another film); a file of another kind (a Movie back as a Video) takes it only when a
    // strong id agrees, and otherwise leaves it Ambiguous. An item of another kind where the rating's is a folder (a series and
    // its season, an album) is no replacement and leaves the hold as it is.
    private static AtPath Judge(IdentitySnapshot identity, BaseItem atPath)
    {
        if (string.Equals(ItemIdentity.KindOf(atPath), identity.Kind, StringComparison.Ordinal))
        {
            return IdentityResolver.ConflictsWith(identity, atPath) ? AtPath.ConflictingProviderIds : AtPath.Takes;
        }

        if (atPath.IsFolder || !FileKinds.Contains(identity.Kind))
        {
            return AtPath.Ignored;
        }

#if JELLYFIN_12
        // Jellyfin 12 re-creates a local alternate version stored as a Video under a Movie as a Movie, under a new id
        // (its id hashes the type): the same file, still a version, so it takes the rating unless strong ids contradict.
        if (identity.Kind == nameof(BaseItemKind.Video) && atPath is Video version && KitItemQueries.PrimaryVersionGuid(version) is not null)
        {
            return IdentityResolver.ConflictsWith(identity, atPath) ? AtPath.ConflictingProviderIds : AtPath.Takes;
        }
#endif

        return IdentityResolver.CompareIds(identity, atPath) == MatchStrength.Strong ? AtPath.Takes : AtPath.KindChanged;
    }

    // A held rating that the item at its path can't take: it stops being held and is left Ambiguous, with that item as its
    // candidate, for the admin. What the member wrote never changes.
    private static void LeaveAmbiguous(IRatingsWriter writer, RatingRecord row, BaseItem candidate, AtPath why, DateTime now, WriteOutcome outcome)
    {
        writer.Upsert(row with { DetachedUtc = now, DetachReason = RatingValues.DetachReason.Ambiguous });
        outcome.NewAmbiguous++;
        outcome.Ambiguous.Add((row.Id, row.ItemId, why.ToString(), [candidate.Id]));
    }

    // An item back at a held rating's path under another id takes it: an Exact match by path (the same file, so the same
    // library folder), under the conflict rule; a merged item's primary only when the member can open it (Land).
    private void Relocate(IRatingsWriter writer, RatingRecord row, BaseItem target, DateTime now, WriteOutcome outcome, IDictionary<Guid, Func<BaseItem, bool>?> views)
    {
        var landing = Land(target, row.UserId, views);
        var from = row.ItemId;
        if (Attach(writer, row, landing.Id, landing.Identity ?? row.Identity with { ItemId = landing.Id, Path = target.Path }, now, now, outcome))
        {
            outcome.Moved.Add((row.Id, from, landing.Id, nameof(MatchStrength.Exact), nameof(MatchSource.Path)));
        }
    }

    private async Task<(int Detached, int Held)> DetachRemovedAsync(Dictionary<Guid, ItemChange> removed, CancellationToken cancellationToken)
    {
        var rows = _store.GetOn(removed.Keys, includeDetached: false);
        if (rows.Count == 0)
        {
            return (0, 0);
        }

        var now = Now();
        var availability = LibraryAvailability.Read(_library, Logger);
        var detach = new Dictionary<Guid, IdentitySnapshot>();
        var paths = new Dictionary<Guid, string?>();
        var heldCount = 0;
        foreach (var item in rows.GroupBy(r => r.ItemId))
        {
            // An item that is there (re-added in the same scan, or an episode whose old season was replaced) keeps its
            // ratings: only an item the library no longer has detaches them.
            if (Lookup(item.Key) is not null)
            {
                continue;
            }

            var identities = item.ToDictionary(r => r.Id, r => removed.TryGetValue(r.ItemId, out var change) ? Complete(r.Identity, change.Identity) : r.Identity);
            var path = identities.Values.Select(i => i.Path).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            if (availability.HoldReason(path) is { } reason)
            {
                Hold(item.Key, reason, path, item.First().Identity.Kind, item.Select(r => r.Id).ToList(), now);
                heldCount += item.Count();
                continue;
            }

            foreach (var (id, identity) in identities)
            {
                detach[id] = identity;
            }

            paths[item.Key] = path;
        }

        foreach (var guarded in GuardMassDetach(availability, paths, "a library change"))
        {
            var on = rows.Where(r => r.ItemId == guarded).ToList();
            Hold(guarded, LibraryAvailability.FolderUnavailable, paths[guarded], on[0].Identity.Kind, on.Select(r => r.Id).ToList(), now, massDetach: true);
            heldCount += on.Count;
            foreach (var row in on)
            {
                detach.Remove(row.Id);
            }
        }

        if (detach.Count == 0)
        {
            return (0, heldCount);
        }

        var outcome = await _store.WriteAsync(
            w =>
            {
                var result = new WriteOutcome();
                foreach (var row in w.GetOn(removed.Keys, includeDetached: false))
                {
                    // Only a rating still on the item the decision was made on (a catch-up may have moved it since).
                    if (detach.TryGetValue(row.Id, out var identity) && identity.ItemId == row.ItemId)
                    {
                        var completed = Complete(row.Identity, removed.TryGetValue(row.ItemId, out var change) ? change.Identity : null);
                        w.Upsert(row with { Identity = completed, DetachedUtc = now, DetachReason = RatingValues.DetachReason.Removed });
                        result.Detached.Add((row.Id, row.ItemId, RatingValues.DetachReason.Removed));
                    }
                }

                return result;
            },
            cancellationToken).ConfigureAwait(false);
        Unhold(outcome.Detached.Select(d => d.ItemId));
        LogDetached(outcome, "their item, or its series, season or album, was removed from the library");
        return (outcome.Detached.Count, heldCount);
    }

    // Fresh snapshots for the attached ratings on updated items, written only where the stored one differs (after normalising
    // the runtime), one write per BatchSize items; an item merged into another version takes its ratings to the primary.
    private async Task<(int Refreshed, int Merged)> RefreshAsync(HashSet<Guid> updated, CancellationToken cancellationToken)
    {
        var rows = _store.GetOn(updated, includeDetached: false);
        var itemIds = rows.Select(r => r.ItemId).Distinct().ToList();
        var refreshed = 0;
        var merged = 0;
        for (var start = 0; start < itemIds.Count; start += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captured = new Dictionary<Guid, IdentitySnapshot>();
            var merges = new Dictionary<Guid, (Guid Id, IdentitySnapshot? Identity)>();
            var views = new Dictionary<Guid, Func<BaseItem, bool>?>();
            var moving = new HashSet<(Guid ItemId, Guid UserId)>();
            foreach (var id in itemIds.Skip(start).Take(BatchSize))
            {
                if (Lookup(id) is not { } item)
                {
                    // Gone: the verify sweep's (and LibraryAvailability's) to decide.
                    continue;
                }

                // A merged version's ratings go to the primary, each only when its member can open it;
                // the others stay on the version and take its snapshot.
                if (PrimaryOf(item, null, views) is { } primary)
                {
                    merges[id] = (primary.Id, CaptureOrNull(primary));
                    foreach (var row in rows.Where(r => r.ItemId == id))
                    {
                        if (CanOpen(row.UserId, primary, views))
                        {
                            moving.Add((id, row.UserId));
                        }
                    }
                }

                if (CaptureOrNull(item) is { } fresh)
                {
                    captured[id] = fresh;
                }
            }

            bool Moves(RatingRecord r) => merges.ContainsKey(r.ItemId) && moving.Contains((r.ItemId, r.UserId));
            var changed = rows.Any(r => Moves(r) || (captured.TryGetValue(r.ItemId, out var fresh) && !fresh.Equals(r.Identity)));
            if (changed && !_store.IsReadOnly)
            {
                var now = Now();
                var outcome = await _store.WriteAsync(
                    w =>
                    {
                        var result = new WriteOutcome();
                        foreach (var row in w.GetOn(captured.Keys.Concat(merges.Keys).Distinct().ToList(), includeDetached: false))
                        {
                            if (!Moves(row))
                            {
                                if (captured.TryGetValue(row.ItemId, out var fresh) && !fresh.Equals(row.Identity))
                                {
                                    w.Upsert(row with { Identity = fresh });
                                    result.Refreshed++;
                                }
                            }
                            else if (merges.TryGetValue(row.ItemId, out var target))
                            {
                                var from = row.ItemId;
                                if (Attach(w, row, target.Id, target.Identity ?? row.Identity with { ItemId = target.Id }, now, now, result))
                                {
                                    result.Moved.Add((row.Id, from, target.Id, nameof(MatchStrength.Exact), MergedVersionReason));
                                    w.AppendModerationLog(new ModerationLogRecord
                                    {
                                        AtUtc = now,
                                        Action = RatingValues.ModerationAction.Relink,
                                        RatingId = row.Id,
                                        TargetUserId = row.UserId,
                                        ItemId = target.Id,
                                        Reason = MergedVersionReason,
                                        Details = string.Create(CultureInfo.InvariantCulture, $"{{\"FromItemId\":\"{from:N}\"}}")
                                    });
                                }
                            }
                        }

                        return Ambiguous(w, result, now);
                    },
                    cancellationToken).ConfigureAwait(false);
                refreshed += outcome.Refreshed;
                merged += outcome.Moved.Count;
                LogMoves(outcome, "Merged version");
                await NoteAmbiguousAsync(outcome.NewAmbiguous, cancellationToken).ConfigureAwait(false);
            }

            await Task.Yield();
        }

        if (refreshed > 0)
        {
            Logger.LogDebug("{Refreshed} ratings took their updated items' snapshots", refreshed);
        }

        return (refreshed, merged);
    }

    // Step 4: held ratings whose item came back with its id are no longer held; those whose path an added or updated item has
    // under another id move there; then the detached ratings these items could match go to the kit's resolver: the same id
    // back, the same path, a strong key in common, or a move-journal line from their id.
    private async Task<(int Relocated, DetachedRunResult? Resolve)> ReattachAsync(HashSet<Guid> ids, CancellationToken cancellationToken)
    {
        Unhold(ids);
        var items = new List<(BaseItem Item, IdentitySnapshot Identity)>();
        foreach (var id in ids)
        {
            if (Lookup(id) is { } item && CaptureOrNull(item) is { } identity)
            {
                items.Add((item, identity));
            }
        }

        var paths = new Dictionary<string, BaseItem>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (item, identity) in items)
        {
            if (LibraryAvailability.TryNormalize(identity.Path, out var full))
            {
                paths.TryAdd(full, item);
            }
        }

        var relocated = await RelocateHeldAsync(paths, cancellationToken).ConfigureAwait(false);

        var detached = _store.IsReadOnly ? [] : _store.GetDetached();
        var guarded = GuardHeldRows();
        if (detached.Count == 0 && guarded.Count == 0)
        {
            return (relocated, null);
        }

        var keys = items.SelectMany(i => ItemIdentity.StrongKeys(i.Identity)).ToHashSet(StringComparer.Ordinal);
        var journal = _moves.Read();
        var moved = journal.Lines.Select(l => l.OldId).ToHashSet();
        bool CouldMatch(RatingRecord r) =>
            ids.Contains(r.ItemId)
            || moved.Contains(r.ItemId)
            || (LibraryAvailability.TryNormalize(r.Identity.Path, out var full) && paths.ContainsKey(full))
            || (keys.Count > 0 && ItemIdentity.StrongKeys(r.Identity).Any(keys.Contains));
        var candidates = detached.Where(CouldMatch).ToList();

        // The mass-detach guard's ratings too (a renamed folder that held every rated item), as detached since they were held.
        var heldCandidates = guarded.Where(g => CouldMatch(g.Row)).Select(g => g.Row with { DetachedUtc = g.SinceUtc }).ToList();
        if (candidates.Count == 0 && heldCandidates.Count == 0)
        {
            return (relocated, null);
        }

        var result = await _detached().ResolveDetachedAsync(this, [.. ToDetached(candidates), .. ToDetached(heldCandidates)], cancellationToken).ConfigureAwait(false);
        return (relocated, result);
    }

    // Held ratings whose path an added or updated item has under another id: judged (Judge) on each rating, so one of the
    // rating's kind takes it, another title or another kind without an id in common leaves it Ambiguous, and a folder of
    // another kind leaves the hold as it is.
    private async Task<int> RelocateHeldAsync(Dictionary<string, BaseItem> paths, CancellationToken cancellationToken)
    {
        var atPaths = new Dictionary<Guid, BaseItem>();
        lock (_statusLock)
        {
            foreach (var (itemId, held) in _held)
            {
                if (LibraryAvailability.TryNormalize(held.Path, out var full) && paths.TryGetValue(full, out var item) && item.Id != itemId)
                {
                    atPaths[itemId] = item;
                }
            }
        }

        if (atPaths.Count == 0 || _store.IsReadOnly)
        {
            return 0;
        }

        var now = Now();
        var views = new Dictionary<Guid, Func<BaseItem, bool>?>();
        var outcome = await _store.WriteAsync(
            w =>
            {
                var result = new WriteOutcome();
                foreach (var row in w.GetOn(atPaths.Keys.ToList(), includeDetached: false))
                {
                    if (!atPaths.TryGetValue(row.ItemId, out var target))
                    {
                        continue;
                    }

                    // Either way the rating leaves the held item: moved there, or (the member's newer rating there, or
                    // another title or kind) detached as Ambiguous.
                    switch (Judge(row.Identity, target))
                    {
                        case AtPath.Takes:
                            Relocate(w, row, target, now, result, views);
                            result.Unheld.Add((row.ItemId, row.Id));
                            break;
                        case AtPath.Ignored:
                            break;
                        case var why:
                            LeaveAmbiguous(w, row, target, why, now, result);
                            result.Unheld.Add((row.ItemId, row.Id));
                            break;
                    }
                }

                return Ambiguous(w, result, now);
            },
            cancellationToken).ConfigureAwait(false);
        Release(outcome.Unheld);
        LogMoves(outcome, "Path");
        LogAmbiguous(outcome);
        await NoteAmbiguousAsync(outcome.NewAmbiguous, cancellationToken).ConfigureAwait(false);
        return outcome.Moved.Count;
    }

    // The catch-up's transaction: the stored position read again under the writer, the lines applied to the attached and
    // detached ratings still on their old ids (under the conflict rule), the new position stored.
    private JournalOutcome ApplyJournal(IRatingsWriter writer, MoveJournal journal, DateTime now)
    {
        var storedId = ParseJournalId(writer.GetMeta(MoveJournalIdKey));
        var storedSeq = ParseSeq(writer.GetMeta(LastMoveSeqKey)) ?? 0;
        var applied = MoveJournalReader.CatchUp(journal, storedId, storedSeq);
        var outcome = new JournalOutcome { Reset = applied.Reset, Lines = applied.Lines.Count, LastMoveSeq = applied.LastMoveSeq };
        if (!applied.HasChanges(storedId, storedSeq))
        {
            return outcome;
        }

        if (applied.Lines.Count > 0)
        {
            var rows = writer.GetOn(applied.Lines.Select(l => l.OldId).Distinct().ToList(), includeDetached: true);
            foreach (var (row, identity, movedUtc) in ApplyMoves(rows, applied.Lines))
            {
                // The row as it is now, inside this transaction (an earlier move of this catch-up may have detached it).
                if (writer.Get(row.Id) is not { } current)
                {
                    continue;
                }

                if (identity.ItemId != current.ItemId && movedUtc is { } utc)
                {
                    var from = current.ItemId;
                    if (Attach(writer, current, identity.ItemId, identity, utc, now, outcome))
                    {
                        outcome.Moved.Add((current.Id, from, identity.ItemId, nameof(MatchStrength.Exact), nameof(MatchSource.Journal)));
                    }
                }
                else
                {
                    writer.Upsert(current with { Identity = Normalize(identity with { ItemId = current.ItemId }) });
                    outcome.ContainersMoved++;
                }
            }
        }

        if (applied.JournalId is { } journalId)
        {
            writer.SetMeta(MoveJournalIdKey, journalId.ToString("N", CultureInfo.InvariantCulture));
        }

        writer.SetMeta(LastMoveSeqKey, applied.LastMoveSeq.ToString(CultureInfo.InvariantCulture));
        return Ambiguous(writer, outcome, now);
    }

    private void LogAmbiguous(WriteOutcome outcome)
    {
        foreach (var (ratingId, itemId, reason, candidates) in outcome.Ambiguous)
        {
            Logger.LogInformation(
                "Rating {RatingId} (item {ItemId}) needs a manual relink: {Reason}, candidates {Candidates}",
                ratingId,
                itemId,
                reason,
                string.Join(", ", candidates));
        }
    }

    // The startup re-resolution of the detached ratings, after the catch-up, with the ratings the
    // mass-detach guard holds by then (GetResolvable); a failure is logged and the daily maintenance tries
    // again.
    private async Task<IDictionary<string, object?>?> ResolveAtStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _detached().ResolveDetachedAsync(this, cancellationToken).ConfigureAwait(false);
            if (result.Relinked > 0)
            {
                Logger.LogInformation("At startup {Relinked} of {Checked} detached ratings were relinked", result.Relinked, result.Checked);
            }

            return result.ToDictionary();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "The detached ratings couldn't be re-resolved at startup; the daily maintenance tries again");
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["Error"] = ex.Message };
        }
    }

    // One Activity Log entry for the held ratings an admin detached, by id.
    private async Task ReportHeldDetachedAsync(IReadOnlyList<(Guid RatingId, Guid ItemId, string Reason)> detached, Guid? adminId, CancellationToken cancellationToken)
    {
        if (detached.Count == 0 || _activity is null)
        {
            return;
        }

        var ratings = detached.Select(d => d.RatingId).ToList();
        var items = detached.Select(d => d.ItemId).Distinct().ToList();
        var named = string.Join(", ", ratings.Take(MaxIdsInActivity)) + (ratings.Count > MaxIdsInActivity ? string.Create(CultureInfo.InvariantCulture, $" and {ratings.Count - MaxIdsInActivity} more") : string.Empty);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _activity.CreateAsync(new ActivityLog(HeldDetachedActivityName(ratings.Count), HeldDetachedActivityType, adminId ?? Guid.Empty)
            {
                ShortOverview = "An admin detached ratings kept on items that are gone.",
                Overview = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Detached from Data → Detached & ambiguous (Detach now): ratings {named} on {items.Count} {(items.Count == 1 ? "item" : "items")} ({string.Join(", ", items.Take(MaxIdsInActivity))})."),
                LogSeverity = LogLevel.Information,
                DateCreated = Now()
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "The Activity Log entry for {Count} held ratings an admin detached couldn't be written", ratings.Count);
        }
    }

    private void LogDetached(WriteOutcome outcome, string why)
    {
        foreach (var item in outcome.Detached.GroupBy(d => (d.ItemId, d.Reason)))
        {
            Logger.LogInformation(
                "{Count} ratings on item {ItemId} detached ({Reason}: {Why}): {RatingIds}",
                item.Count(),
                item.Key.ItemId,
                item.Key.Reason,
                why,
                string.Join(", ", item.Select(d => d.RatingId)));
        }
    }

    private void LogMoves(WriteOutcome outcome, string how)
    {
        foreach (var (ratingId, from, to, strength, source) in outcome.Moved)
        {
            if (from == to)
            {
                // Its item back with the same id (never "moved from X to X").
                Logger.LogInformation("Rating {RatingId} reattached to item {ItemId} ({How}: {Strength}, {Source})", ratingId, to, how, strength, source);
            }
            else
            {
                Logger.LogInformation("Rating {RatingId} moved from item {FromItemId} to item {ToItemId} ({How}: {Strength}, {Source})", ratingId, from, to, how, strength, source);
            }
        }

        foreach (var (loserId, itemId, winnerId) in outcome.Conflicts)
        {
            Logger.LogInformation(
                "Rating {RatingId} was detached as Ambiguous: its member's newer rating {WinnerId} is on item {ItemId} (one rating per member and item: the newer one stays)",
                loserId,
                winnerId,
                itemId);
        }
    }

    // Inside a batch the batch counts the new Ambiguous rows and writes one Activity Log entry at its end; elsewhere (the
    // verify sweep, the catch-up, the daily re-resolution) each pass writes its own.
    private Task NoteAmbiguousAsync(int count, CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return Task.CompletedTask;
        }

        if (_tally.Value is { } tally)
        {
            tally.Add(count);
            return Task.CompletedTask;
        }

        return ReportAmbiguousAsync(count, cancellationToken);
    }

    /// <summary>
    /// Writes the Activity Log entry for new Ambiguous rows, as a batch does (also for a
    /// Remap by name that made some), and its Warning; nothing for none.
    /// </summary>
    /// <param name="count">The new Ambiguous rows.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task.</returns>
    internal async Task ReportAmbiguousAsync(int count, CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return;
        }

        Logger.LogWarning("{Count} ratings need a manual relink: several copies match, the member already rated the copy, or another title is at their place (Data → Detached & ambiguous)", count);
        if (_activity is null)
        {
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _activity.CreateAsync(new ActivityLog(AmbiguousActivityName(count), ActivityType, Guid.Empty)
            {
                ShortOverview = "Several copies match, or another title is at their place, so they were left detached.",
                Overview = "User Ratings couldn't relink these ratings automatically because several copies of their items match, the member already rated the copy, or the item now at their place is another title (or another kind without an id in common). Review them in Dashboard → Plugins → User Ratings → Data → Detached & ambiguous.",
                LogSeverity = LogLevel.Warning,
                DateCreated = Now()
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "The Activity Log entry for {Count} ratings that need a manual relink couldn't be written", count);
        }
    }

    // Where a relink lands (Land): the item's id, its snapshot (null when it couldn't be captured) and its path.
    private readonly record struct Landing(Guid Id, IdentitySnapshot? Identity, string? Path);

    // A rated item kept attached although it is gone: why, since when, its stored path and kind (for the relocation and the
    // guard's library folder; the path is never logged), the ratings on it, and whether the mass-detach guard holds it.
    private sealed record HeldItem(string Reason, DateTime SinceUtc, string? Path, string Kind, IReadOnlyList<Guid> RatingIds, bool MassDetach);

    // The new Ambiguous rows a batch created, across its steps (and the verify sweep it awaited).
    private sealed class AmbiguousTally
    {
        private int _new;

        public int New => Volatile.Read(ref _new);

        public void Add(int count) => Interlocked.Add(ref _new, count);
    }

    // What one transaction did, logged after it committed.
    private class WriteOutcome
    {
        public List<(Guid RatingId, Guid FromItemId, Guid ToItemId, string Strength, string Source)> Moved { get; } = [];

        public List<(Guid RatingId, Guid ItemId, string Reason)> Detached { get; } = [];

        public List<(Guid LoserId, Guid ItemId, Guid WinnerId)> Conflicts { get; } = [];

        public List<(Guid RatingId, Guid ItemId, string Reason, List<Guid> Candidates)> Ambiguous { get; } = [];

        // The ratings that left their held items (moved, or left Ambiguous), each item unheld once none of its ratings is
        // left on it.
        public List<(Guid ItemId, Guid RatingId)> Unheld { get; } = [];

        public int Refreshed { get; set; }

        public int NewAmbiguous { get; set; }
    }

    private sealed class JournalOutcome : WriteOutcome
    {
        public MoveJournalReset Reset { get; init; }

        public int Lines { get; init; }

        public long LastMoveSeq { get; init; }

        public int ContainersMoved { get; set; }
    }
}
