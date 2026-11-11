using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserRatings.Lifecycle;

/// <summary>
/// The ratings store as the kit's <see cref="IUserDataOwner"/>: the deleted-user policy
/// on Jellyfin's <c>UserDeletedEventArgs</c> and the admin's <i>Apply deleted-user policy</i>, the reconciler's counts and
/// names (it never changes a rating), and <i>Remap by name</i>, over the live table and
/// the kit's archive (<c>archive/userratings-archived.jsonl</c>) too.
/// </summary>
/// <remarks>
/// <para><b>Only an explicit deletion changes ratings</b> (the kit calls <see cref="HandleUserDeletedAsync"/> only then,
/// after a <c>user-deleted</c> or <c>apply-policy</c> pre-operation snapshot of the database, <see cref="SnapshotBeforeAsync"/>,
/// taken only when the policy changes a rating, <see cref="WouldChangeRecords"/>). Each policy is one unit of work over
/// every rating of the id, attached, detached and archived, with one moderation-log row that counts them
/// (<c>{"Ratings","Reviews","Detached","Archived"}</c>, the live ones and the archived ones) and holds no text:
/// <see cref="DeletedUserPolicy.Delete"/> deletes them with <c>UserDeleted</c> tombstones (so an import never brings them
/// back), the archived ones too, whose lines leave the archive file (rewritten, the earlier lines of the user's ratings with
/// it, after a <c>user-deleted</c> copy), and logs <c>UserDeleted</c>; <see cref="DeletedUserPolicy.Anonymize"/> adds a
/// <c>FormerUsers</c> row (a new random id, "Former member", then "Former member 2" …, the lowest number free) and re-keys
/// the ratings to it, the archived ones re-appended under it (the later line supersedes the earlier), so they come back as
/// the former member's when their items do, and logs <c>UserAnonymized</c>; the ratings count and show as that name.
/// <see cref="DeletedUserPolicy.KeepAsOrphan"/> changes no rating (they count nowhere and show only to moderation) and logs
/// <c>UserOrphaned</c> once: applied again, it writes nothing, whatever was logged about the user in between. Delete and
/// Anonymize forget the user's name in <see cref="KnownUsers"/>; KeepAsOrphan keeps it, the only link back. A policy or a
/// remap that would change the archive while it can't be read is refused (503) before anything is written.</para>
/// <para><b>The archive's rewrite runs after the commit</b>: the unit of work records it in
/// <c>Meta.ArchivePurgesPending</c> (<see cref="ArchivePurgesKey"/>; the user, and the ids of every rating of the user, live
/// and archived, so lines of a rating that once had another id of the member's, a remap's, go too, for Anonymize
/// too), so the tombstones always exist first; then the archive is rewritten without those lines
/// (Delete), or without them but for the former member's re-appended ones (Anonymize), after a <c>user-deleted</c> copy,
/// and the record is cleared. One that fails stays
/// pending: the daily maintenance's <see cref="ArchivePurgesStep"/> runs it again, Diagnostics (<c>Lifecycle</c>,
/// <c>ArchivePurgesPending</c>) counts it, and meanwhile the archived ratings it names count for nobody and never come back
/// (their ids are tombstoned). Every policy that changed something then asks for a checkpoint of the database's write-ahead
/// log (<see cref="RatingsCheckpoint"/>, in the background), so once it is done no earlier version of a deleted review's page
/// stays in the <c>-wal</c>.</para>
/// <para><b>The reconciler</b> sees every stored user id except the former members' (<see cref="CountRecordsByUser"/>: the
/// store's <c>GROUP BY UserId</c> and the archived ratings' members, kept in memory until the next write or archive change),
/// so the daily maintenance's Users step marks unknown ids as orphans and never touches a former member or a rating, a
/// member whose only ratings are archived is still counted (and marked deleted when deleted), and the names of such ids are
/// kept. While the archive can't be read the counts throw (503), never leaving the archived
/// ratings out: the kit then skips this owner and keeps its marks and names.</para>
/// <para><b>Remap by name</b> (<see cref="RemapUsersAsync"/>) gives an old id's ratings to the new one, its archived ones
/// re-appended under the new id. Where the new id already has an attached rating of the same item, the relink's conflict
/// rule applies: the newer <c>UpdatedUtc</c> stays attached (the one already there on a tie) and the older is detached as
/// <c>Ambiguous</c>, which raises the dashboard's banner and writes the Activity Log entry a
/// batch writes; each old id gets a <c>UserRemapped</c> moderation-log row (ids and counts only). A former member's ratings
/// are never remapped.</para>
/// <para><b>Snapshots</b>: the newest pre-operation snapshot is reused while the store's
/// generation is the one it was taken at, so a burst of operations that changed nothing in between takes one.</para>
/// <para><b>Names</b> (<see cref="KnownUsers"/>): the last seen name of each stored id, in <c>Meta.KnownUsers</c>
/// as the kit's JSON. A member's write records the member's name (<see cref="RecordName"/>), and the reconciler refreshes
/// them (<see cref="RefreshKnownUsersAsync"/>, writing only when one changed).</para>
/// <para>While the store is read-only the kit leaves the owner alone (<see cref="IsReadOnly"/>), and every method that writes
/// checks it again and refuses (a store that turned read-only is a failure, never "done"). Logs name users and ratings by id
/// and count them; names and review text never reach the log.</para>
/// </remarks>
internal sealed class RatingsUserDataOwner : IUserDataOwner
{
    /// <summary>The <c>Meta</c> key of the last seen names (<see cref="KnownUsers"/>).</summary>
    public const string KnownUsersKey = "KnownUsers";

    /// <summary>The name a former member's ratings show; the second is "Former member 2", and so on.</summary>
    public const string FormerMemberName = "Former member";

    /// <summary>The 503 of a policy or remap that would change the archive while it can't be read: nothing was written.</summary>
    public const string ArchiveUnavailableMessage = "The ratings' archive can't be read or written now, so nothing was changed. The plugin's Diagnostics (Detached) say why; try again once it can be.";

    /// <summary>
    /// The <c>Meta</c> key of the archive rewrites a deleted-user policy left to do after its commit: a JSON
    /// array of <c>{"UserId", "Ids"}</c>, empty when none is pending.
    /// </summary>
    public const string ArchivePurgesKey = "ArchivePurgesPending";

    /// <summary>The daily maintenance's step that runs the archive rewrites left pending (<see cref="RunPendingPurgesAsync"/>).</summary>
    public const string ArchivePurgesStep = "ArchivePurges";

    /// <summary>
    /// How long the pre-operation snapshots of the database, and the archive's copies, are kept (the
    /// kit's <c>keepWithin</c>, up to its 50): none older, the newest five neither, and the daily
    /// snapshot deletes them once older.
    /// </summary>
    public static readonly TimeSpan PreOpKeepWithin = TimeSpan.FromDays(7);

    private static readonly AsyncLocal<UsersTally?> CurrentTally = new();

    private readonly IRatingsStore _store;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Func<RatingsLibraryConsumer>? _lifecycle;
    private readonly Lock _lock = new();
    private Cached? _cached;
    private (long Generation, string Path)? _lastSnapshot;

    /// <summary>
    /// Initializes a new instance of the <see cref="RatingsUserDataOwner"/> class.
    /// </summary>
    /// <param name="store">The ratings store.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    /// <param name="lifecycle">The ratings lifecycle, resolved when first needed, for its archive and its Activity Log entry;
    /// null leaves the archive out (tests of the live table only).</param>
    public RatingsUserDataOwner(IRatingsStore store, ILogger logger, TimeProvider? timeProvider = null, Func<RatingsLibraryConsumer>? lifecycle = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifecycle = lifecycle;
    }

    /// <inheritdoc />
    public string Name => RatingsLibraryConsumer.StoreName;

    /// <inheritdoc />
    public bool IsReadOnly => _store.IsReadOnly;

    /// <inheritdoc />
    public KnownUsers KnownUsers
    {
        get
        {
            try
            {
                return Current().Names;
            }
            catch (PluginException ex)
            {
                _logger.LogDebug(ex, "The ratings' user names can't be read now");
                return _cached?.Names ?? KnownUsers.Empty;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, int> CountRecordsByUser() => Current().Counts;

    /// <summary>
    /// Records a member's name in <c>Meta.KnownUsers</c> inside a unit of work that writes the member's rating (names
    /// are refreshed on writes); nothing when the name is known already. Never throws for a stored value it can't read.
    /// </summary>
    /// <param name="writer">The unit of work.</param>
    /// <param name="userId">The member.</param>
    /// <param name="name">The member's name now.</param>
    public static void RecordName(IRatingsWriter writer, Guid userId, string? name)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var names = Parse(writer.GetMeta(KnownUsersKey));
        var next = names.With(userId, name);
        if (!ReferenceEquals(next, names))
        {
            writer.SetMeta(KnownUsersKey, Serialize(next));
        }
    }

    /// <summary>
    /// Starts counting what the policies and remaps of this asynchronous flow do beyond the kit's report (the archived
    /// ratings, the Ambiguous rows a remap made), for the admin's routes to answer with.
    /// </summary>
    /// <returns>The tally, filled as the kit calls this owner.</returns>
    public static UsersTally BeginTally()
    {
        var tally = new UsersTally();
        CurrentTally.Value = tally;
        return tally;
    }

    /// <summary>
    /// The member an archived rating belongs to: its record's <c>UserId</c> (a former member's too), or the archive line's.
    /// </summary>
    /// <param name="record">The archived record.</param>
    /// <returns>The member, or null.</returns>
    public static Guid? ArchivedUserIdOf(ArchivedRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Record.TryGetPropertyValue("UserId", out var node) && node is JsonValue value)
        {
            if (value.TryGetValue<Guid>(out var id) || (value.TryGetValue<string>(out var text) && Guid.TryParse(text, out id)))
            {
                return id == Guid.Empty ? null : id;
            }
        }

        return record.UserId;
    }

    /// <inheritdoc />
    public async Task RefreshKnownUsersAsync(IReadOnlyDictionary<Guid, string> currentNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentNames);
        if (_store.IsReadOnly)
        {
            return;
        }

        var current = Current();
        if (current.Names.Refresh(current.Counts.Keys, currentNames).Equals(current.Names))
        {
            return;
        }

        await _store.WriteAsync(
            w =>
            {
                var names = Parse(w.GetMeta(KnownUsersKey));
                var next = names.Refresh(current.Counts.Keys, currentNames);
                if (!next.Equals(names))
                {
                    w.SetMeta(KnownUsersKey, Serialize(next));
                }

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>The newest pre-operation snapshot this owner took is reused while the store's generation is the one it was
    /// taken at (nothing was written since), so a burst takes one where it can.</remarks>
    public async Task<string?> SnapshotBeforeAsync(string operation, CancellationToken cancellationToken)
    {
        var generation = _store.Generation;
        lock (_lock)
        {
            if (_lastSnapshot is { } last && last.Generation == generation && File.Exists(last.Path))
            {
                _logger.LogDebug("Nothing was written since the pre-operation snapshot {Snapshot}; it is the one before {Operation} too", Path.GetFileName(last.Path), operation);
                return Path.GetFileName(last.Path);
            }
        }

        if (await _store.PreOpSnapshotAsync(operation, cancellationToken).ConfigureAwait(false) is not { } path)
        {
            return null;
        }

        lock (_lock)
        {
            _lastSnapshot = (generation, path);
        }

        return Path.GetFileName(path);
    }

    /// <inheritdoc />
    /// <remarks>Only <see cref="DeletedUserPolicy.Delete"/> and <see cref="DeletedUserPolicy.Anonymize"/> change ratings, and
    /// only of a user who still has some (live or archived) that isn't a former member.</remarks>
    public bool WouldChangeRecords(Guid userId, DeletedUserPolicy policy)
        => policy is DeletedUserPolicy.Delete or DeletedUserPolicy.Anonymize && Current().Counts.ContainsKey(userId);

    /// <inheritdoc />
    public async Task<int> HandleUserDeletedAsync(Guid userId, DeletedUserPolicy policy, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            return 0;
        }

        // A store that turned read-only since the kit looked is a failure, never "done".
        _store.ThrowIfReadOnly();
        var archive = policy == DeletedUserPolicy.KeepAsOrphan ? TryArchive() : ArchiveToWrite();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var outcome = await _store.WriteAsync(w => ApplyPolicy(w, userId, policy, now, archive), cancellationToken).ConfigureAwait(false);
        if (outcome == PolicyOutcome.None)
        {
            return 0;
        }

        // Committed, the tombstones with it: now the archive's rewrite it recorded, then the write-ahead log's checkpoint.
        if (archive is not null && policy is DeletedUserPolicy.Delete or DeletedUserPolicy.Anonymize)
        {
            await RunPendingPurgesAsync(cancellationToken).ConfigureAwait(false);
        }

        RatingsCheckpoint.RequestAfter(_store, _logger, "the deleted-user policy " + policy + " of user " + userId.ToString("N"));

        if (CurrentTally.Value is { } tally)
        {
            tally.Archived += outcome.Archived;
        }

        switch (policy)
        {
            case DeletedUserPolicy.Delete:
                _logger.LogInformation("Deleted user {UserId} (Delete): {Ratings} ratings deleted with tombstones, {Reviews} of them with reviews, and {Archived} archived ones", userId, outcome.Ratings, outcome.Reviews, outcome.Archived);
                return outcome.Ratings + outcome.Archived;
            case DeletedUserPolicy.Anonymize:
                _logger.LogInformation("Deleted user {UserId} (Anonymize): {Ratings} ratings and {Archived} archived ones kept as former member {FormerUserId}", userId, outcome.Ratings, outcome.Archived, outcome.FormerUserId);
                return outcome.Ratings + outcome.Archived;
            default:
                _logger.LogInformation("Deleted user {UserId} (KeepAsOrphan): {Ratings} ratings and {Archived} archived ones kept under the id, counted nowhere", userId, outcome.Ratings, outcome.Archived);
                return 0;
        }
    }

    /// <inheritdoc />
    public async Task<int> RemapUsersAsync(IReadOnlyDictionary<Guid, Guid> map, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        var pairs = map.Where(p => p.Key != Guid.Empty && p.Value != Guid.Empty && p.Key != p.Value).ToList();
        if (pairs.Count == 0)
        {
            return 0;
        }

        _store.ThrowIfReadOnly();
        var archive = ArchiveToWrite();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var outcome = await _store.WriteAsync(
            w =>
            {
                var formers = w.GetFormerUsers();
                var contents = archive?.Archive.Read();
                var pending = PendingPurgesOf(w.GetMeta(ArchivePurgesKey));
                var result = new RemapOutcome();
                var rekeyed = new List<ArchivedRecord>();
                foreach (var (oldId, newId) in pairs)
                {
                    if (formers.ContainsKey(oldId) || formers.ContainsKey(newId))
                    {
                        continue;
                    }

                    var moved = 0;
                    var conflicts = result.Conflicts.Count;
                    foreach (var row in w.GetByUser(oldId))
                    {
                        var moving = row with { UserId = newId };
                        if (row.DetachedUtc is null && w.GetAttached(newId, row.ItemId) is { } other)
                        {
                            // The conflict rule: the newer stays attached, the one already there on a tie.
                            if (row.UpdatedUtc > other.UpdatedUtc)
                            {
                                w.Upsert(other with { DetachedUtc = now, DetachReason = RatingValues.DetachReason.Ambiguous });
                                result.Conflicts.Add((other.Id, row.ItemId, row.Id));
                            }
                            else
                            {
                                moving = moving with { DetachedUtc = now, DetachReason = RatingValues.DetachReason.Ambiguous };
                                result.Conflicts.Add((row.Id, row.ItemId, other.Id));
                            }
                        }

                        w.Upsert(moving);
                        moved++;
                    }

                    // The archived ones under the new id (a member the server knows: the kit resolves them in its view).
                    var archived = contents is null ? [] : OfUser(contents, oldId, pending);
                    rekeyed.AddRange(archived.Select(a => Rekeyed(a, newId, known: true)));
                    if (moved + archived.Count == 0)
                    {
                        continue;
                    }

                    result.Moved += moved + archived.Count;
                    result.Archived += archived.Count;
                    w.AppendModerationLog(new ModerationLogRecord
                    {
                        AtUtc = now,
                        Action = RatingValues.ModerationAction.UserRemapped,
                        TargetUserId = oldId,
                        Details = string.Create(CultureInfo.InvariantCulture, $"{{\"NewUserId\":\"{newId:N}\",\"Ratings\":{moved},\"Archived\":{archived.Count},\"Ambiguous\":{result.Conflicts.Count - conflicts}}}")
                    });
                }

                if (result.Conflicts.Count > 0)
                {
                    w.SetMeta(RatingsLibraryConsumer.AmbiguousUtcKey, FormatTime(now));
                }

                var names = Parse(w.GetMeta(KnownUsersKey));
                var next = names.Remap(map);
                if (!ReferenceEquals(next, names))
                {
                    w.SetMeta(KnownUsersKey, Serialize(next));
                }

                // The archive last, inside the transaction: if it can't be written, nothing of the remap is.
                if (rekeyed.Count > 0)
                {
                    WriteArchive(() => archive!.Archive.Append(rekeyed));
                }

                return result;
            },
            cancellationToken).ConfigureAwait(false);

        foreach (var (detached, itemId, kept) in outcome.Conflicts)
        {
            _logger.LogInformation("Remap by name: rating {RatingId} on item {ItemId} detached as Ambiguous; the member's rating {KeptId} there is newer", detached, itemId, kept);
        }

        if (CurrentTally.Value is { } tally)
        {
            tally.Ambiguous += outcome.Conflicts.Count;
            tally.Archived += outcome.Archived;
        }

        _logger.LogInformation("Remap by name: {Ratings} ratings of {Users} user ids moved ({Archived} of them archived), {Ambiguous} detached as Ambiguous", outcome.Moved, pairs.Count, outcome.Archived, outcome.Conflicts.Count);
        await ReportAmbiguousAsync(outcome.Conflicts.Count, cancellationToken).ConfigureAwait(false);
        return outcome.Moved;
    }

    /// <summary>
    /// Counts the Ambiguous rows a remap would make (its dry run's answer): the old ids' attached
    /// ratings on items the new ids rated too. A former member's are left out, as the remap leaves them.
    /// </summary>
    /// <param name="map">Old id to new id.</param>
    /// <returns>The count.</returns>
    public int CountRemapConflicts(IReadOnlyDictionary<Guid, Guid> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return map.Sum(p => CountRemapConflicts(p.Key, p.Value));
    }

    /// <inheritdoc />
    /// <remarks>The old id's attached ratings on items the new id rated too, each of which the remap would detach (the older
    /// one) as Ambiguous; the kit hashes it into the remap's plan token, so one that turned up
    /// since the dry run makes the real run 409. 0 for a former member, as the remap leaves them.</remarks>
    public int CountRemapConflicts(Guid oldId, Guid newId)
    {
        if (oldId == newId || oldId == Guid.Empty || newId == Guid.Empty)
        {
            return 0;
        }

        var formers = _store.GetFormerUsers();
        if (formers.ContainsKey(oldId) || formers.ContainsKey(newId))
        {
            return 0;
        }

        var taken = _store.GetAttachedForUser(newId).Select(r => r.ItemId).ToHashSet();
        return _store.GetAttachedForUser(oldId).Count(r => taken.Contains(r.ItemId));
    }

    /// <summary>
    /// Counts the archived ratings a remap would move (its dry run's <c>ArchivedRecords</c>): the
    /// old ids' archived ratings, a former member's left out as the remap leaves them, and those of a rewrite still pending.
    /// </summary>
    /// <param name="map">Old id to new id.</param>
    /// <returns>The count.</returns>
    /// <exception cref="PluginException">503 while the archive can't be read.</exception>
    public int CountRemapArchived(IReadOnlyDictionary<Guid, Guid> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var pairs = map.Where(p => p.Key != Guid.Empty && p.Value != Guid.Empty && p.Key != p.Value).ToList();
        if (pairs.Count == 0)
        {
            return 0;
        }

        var contents = ArchiveForCounts();
        var formers = _store.GetFormerUsers();
        var pending = PendingPurgesOf(_store.GetMeta(ArchivePurgesKey));
        return pairs.Where(p => !formers.ContainsKey(p.Key) && !formers.ContainsKey(p.Value)).Sum(p => OfUser(contents, p.Key, pending).Count);
    }

    /// <summary>
    /// Runs the archive rewrites the deleted-user policies left pending (<see cref="ArchivePurgesKey"/>): right after each
    /// policy's commit, and as the daily maintenance's <see cref="ArchivePurgesStep"/> for those that failed.
    /// Each takes the lines of its user (and for Delete of its rating ids) out of the archive, the earlier ones
    /// too, after a <c>user-deleted</c> copy; one done is cleared, one that fails stays and is logged.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The step's report: <c>Pending</c>, <c>Done</c>, <c>Failed</c>, <c>Purged</c> (archived ratings deleted).</returns>
    public async Task<IDictionary<string, object?>> RunPendingPurgesAsync(CancellationToken cancellationToken)
    {
        var report = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Pass"] = ArchivePurgesStep };
        if (_store.IsReadOnly)
        {
            report["Skipped"] = "ReadOnly";
            return report;
        }

        var pending = PendingPurgesOf(_store.GetMeta(ArchivePurgesKey));
        report["Pending"] = pending.Count;
        if (pending.Count == 0 || _lifecycle is null)
        {
            return report;
        }

        ArchiveAccess? access = null;
        try
        {
            var lifecycle = _lifecycle();
            access = new ArchiveAccess(lifecycle.Archive, lifecycle.ArchiveSnapshotDirectory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The ratings' archive can't be reached, so {Count} archive rewrites of deleted users stay pending; the daily maintenance tries them again", pending.Count);
        }

        var done = new List<PendingPurge>();
        var purged = 0;
        foreach (var purge in access is null ? [] : pending)
        {
            try
            {
                var (count, copy) = access!.Archive.Purge(purge.Matches, access.SnapshotDirectory, SnapshotOperations.UserDeleted, includeHistory: true, keepWithin: PreOpKeepWithin);
                purged += count;
                done.Add(purge);
                if (copy is not null)
                {
                    _logger.LogInformation("Deleted user {UserId}: {Purged} archived ratings and their earlier lines left the archive, after the copy {Copy}", purge.UserId, count, copy);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Deleted user {UserId}: their lines couldn't be taken out of the ratings' archive; they count for nobody meanwhile, and the daily maintenance tries again", purge.UserId);
            }
        }

        if (done.Count > 0)
        {
            await _store.WriteAsync(
                w =>
                {
                    var left = PendingPurgesOf(w.GetMeta(ArchivePurgesKey)).Where(p => !done.Contains(p)).ToList();
                    w.SetMeta(ArchivePurgesKey, SerializePending(left));
                    return 0;
                },
                cancellationToken).ConfigureAwait(false);
            RatingsCheckpoint.RequestAfter(_store, _logger, "the archive rewrites of " + done.Count.ToString(CultureInfo.InvariantCulture) + " deleted users");
        }

        report["Done"] = done.Count;
        report["Failed"] = pending.Count - done.Count;
        report["Purged"] = purged;
        return report;
    }

    /// <summary>
    /// The archive rewrites pending in <c>Meta.ArchivePurgesPending</c>; none for an empty or unreadable value.
    /// </summary>
    /// <param name="json">The stored value.</param>
    /// <returns>The rewrites.</returns>
    internal static IReadOnlyList<PendingPurge> PendingPurgesOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<PendingPurge>>(json)?.Where(p => p.UserId != Guid.Empty).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The name of the next former member: "Former member" while no former member has it, otherwise "Former member N" with
    /// the lowest N from 2 that none has.
    /// </summary>
    /// <param name="taken">The former members' names.</param>
    /// <returns>The name.</returns>
    internal static string NextFormerMemberName(IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var names = taken.ToHashSet(StringComparer.Ordinal);
        if (!names.Contains(FormerMemberName))
        {
            return FormerMemberName;
        }

        for (var n = 2; ; n++)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"{FormerMemberName} {n}");
            if (!names.Contains(name))
            {
                return name;
            }
        }
    }

    // The user's archived ratings, but those a pending rewrite takes out: deleted (and tombstoned) already.
    private static List<ArchivedRecord> OfUser(ArchiveContents contents, Guid userId, IReadOnlyList<PendingPurge> pending)
        => contents.Records.Where(r => ArchivedUserIdOf(r) == userId && !pending.Any(p => p.Matches(r))).ToList();

    private static string SerializePending(IEnumerable<PendingPurge> pending) => JsonSerializer.Serialize(pending.ToList());

    // A rewrite recorded with those pending already: one per user, the ids merged, the former member kept.
    private static List<PendingPurge> WithPending(IReadOnlyList<PendingPurge> pending, Guid userId, IEnumerable<Guid> ids, Guid? keepUserId = null)
    {
        var merged = pending.Where(p => p.UserId == userId).SelectMany(p => p.Ids).Concat(ids).Distinct().Order().ToList();
        var keep = keepUserId ?? pending.Where(p => p.UserId == userId).Select(p => p.KeepUserId).FirstOrDefault(k => k is not null);
        return [.. pending.Where(p => p.UserId != userId), new PendingPurge(userId, merged, keep)];
    }

    // An archived rating under another member: the line's UserId (null for a former member, whom the kit resolves without a
    // view, as ArchivedRating.ToArchive writes it) and the record's.
    private static ArchivedRecord Rekeyed(ArchivedRecord record, Guid userId, bool known)
    {
        var json = (JsonObject)record.Record.DeepClone();
        json["UserId"] = JsonValue.Create(userId);
        return record with { UserId = known ? userId : null, Record = json };
    }

    // The archive's write inside a unit of work: one that fails is a 503, and the transaction rolls back.
    private static void WriteArchive(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginException(PluginErrorKind.Unavailable, ArchiveUnavailableMessage, ex);
        }
    }

    private static KnownUsers Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return KnownUsers.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<KnownUsers>(json) ?? KnownUsers.Empty;
        }
        catch (JsonException)
        {
            // A cache of names, never worth failing a write for (the kit's KnownUsers remarks).
            return KnownUsers.Empty;
        }
    }

    private static string Serialize(KnownUsers names) => JsonSerializer.Serialize(names);

    // A Meta time as the store writes its times (round-trip, UTC), without naming RatingsSql (PublicBoundaryTests).
    private static string FormatTime(DateTime utc)
        => (utc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc.ToUniversalTime()).ToString("O", CultureInfo.InvariantCulture);

    private PolicyOutcome ApplyPolicy(IRatingsWriter w, Guid userId, DeletedUserPolicy policy, DateTime now, ArchiveAccess? archive)
    {
        var formers = w.GetFormerUsers();
        if (formers.ContainsKey(userId))
        {
            return PolicyOutcome.None;
        }

        var rows = w.GetByUser(userId);
        var pending = PendingPurgesOf(w.GetMeta(ArchivePurgesKey));
        var archived = archive is null ? [] : OfUser(archive.Archive.Read(), userId, pending);
        if (rows.Count == 0 && archived.Count == 0)
        {
            return PolicyOutcome.None;
        }

        var reviews = rows.Count(r => r.ReviewText is not null);
        var detached = rows.Count(r => r.DetachedUtc is not null);
        Guid? formerId = null;
        string action;
        switch (policy)
        {
            case DeletedUserPolicy.Delete:
                // Each tombstone no older than its rating: a restore keeps it.
                foreach (var row in rows)
                {
                    w.Delete(row.Id, RatingValues.DeletedBy.UserDeleted, RatingsService.WriteTimeAfter(now, row.UpdatedUtc));
                }

                foreach (var record in archived)
                {
                    if (Guid.TryParse(record.Id, out var ratingId))
                    {
                        w.AddTombstone(ratingId, userId, record.Identity.ItemId, RatingValues.DeletedBy.UserDeleted, RatingsService.WriteTimeAfter(now, ArchivedRating.FromArchive(record)?.UpdatedUtc));
                    }
                }

                action = RatingValues.ModerationAction.UserDeleted;
                break;
            case DeletedUserPolicy.Anonymize:
                var id = Guid.NewGuid();
                while (id == Guid.Empty || id == userId || formers.ContainsKey(id))
                {
                    id = Guid.NewGuid();
                }

                w.AddFormerUser(id, userId, NextFormerMemberName(formers.Values), now);
                foreach (var row in rows)
                {
                    w.Upsert(row with { UserId = id });
                }

                formerId = id;
                action = RatingValues.ModerationAction.UserAnonymized;
                break;
            default:
                // Applied again (the admin's Apply on the listed orphan): nothing changes, so nothing is logged, even
                // when a moderator logged something about the user since.
                if (w.HasModerationAction(userId, RatingValues.ModerationAction.UserOrphaned))
                {
                    return PolicyOutcome.None;
                }

                action = RatingValues.ModerationAction.UserOrphaned;
                break;
        }

        var details = formerId is { } former
            ? string.Create(CultureInfo.InvariantCulture, $"{{\"Ratings\":{rows.Count},\"Reviews\":{reviews},\"Detached\":{detached},\"Archived\":{archived.Count},\"FormerUserId\":\"{former:N}\"}}")
            : string.Create(CultureInfo.InvariantCulture, $"{{\"Ratings\":{rows.Count},\"Reviews\":{reviews},\"Detached\":{detached},\"Archived\":{archived.Count}}}");
        w.AppendModerationLog(new ModerationLogRecord { AtUtc = now, Action = action, TargetUserId = userId, Details = details });

        if (policy is DeletedUserPolicy.Delete or DeletedUserPolicy.Anonymize)
        {
            var names = Parse(w.GetMeta(KnownUsersKey));
            var next = names.Without(userId);
            if (!ReferenceEquals(next, names))
            {
                w.SetMeta(KnownUsersKey, Serialize(next));
            }
        }

        // The archive's rewrite after the commit: recorded here, so the tombstones exist first and one
        // that fails is run again daily. Both take the user's lines and every line of the user's rating ids (live and
        // archived: an earlier line under another id of the member's, a remap's, goes too, for Anonymize
        // too); Anonymize keeps the lines it re-appends under the former member (inside the transaction, last:
        // if they can't be written, nothing of the policy is), whom the kit resolves without a view.
        var ratingIds = rows.Select(r => r.Id).Concat(archived.Select(r => Guid.TryParse(r.Id, out var id) ? id : Guid.Empty)).Where(id => id != Guid.Empty).ToList();
        if (archive is not null && policy == DeletedUserPolicy.Delete)
        {
            w.SetMeta(ArchivePurgesKey, SerializePending(WithPending(pending, userId, ratingIds)));
        }
        else if (archive is not null && formerId is { } anonymous)
        {
            if (archived.Count > 0)
            {
                WriteArchive(() => archive.Archive.Append(archived.Select(r => Rekeyed(r, anonymous, known: false)).ToList()));
            }

            w.SetMeta(ArchivePurgesKey, SerializePending(WithPending(pending, userId, ratingIds, anonymous)));
        }

        return new PolicyOutcome(rows.Count, reviews, formerId, archived.Count);
    }

    // The archive for a policy or a remap that changes it, read as it is now; null without the lifecycle (tests of the live
    // table). One that can't be reached or read refuses the operation before anything is written (503, fail closed).
    private ArchiveAccess? ArchiveToWrite()
    {
        if (_lifecycle is null)
        {
            return null;
        }

        ArchiveAccess access;
        try
        {
            var lifecycle = _lifecycle();
            access = new ArchiveAccess(lifecycle.Archive, lifecycle.ArchiveSnapshotDirectory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PluginException)
        {
            _logger.LogError(ex, "The ratings' archive can't be reached, so a deleted-user policy or a remap was refused");
            throw new PluginException(PluginErrorKind.Unavailable, ArchiveUnavailableMessage, ex);
        }

        if (access.Archive.Read().Problem is { } problem)
        {
            _logger.LogWarning("The ratings' archive can't be read, so a deleted-user policy or a remap was refused: {Problem}", problem);
            throw PluginException.Unavailable(ArchiveUnavailableMessage);
        }

        return access;
    }

    // The archive for KeepAsOrphan, which only counts it: null when it can't be read.
    private ArchiveAccess? TryArchive()
    {
        try
        {
            return ArchiveToWrite();
        }
        catch (PluginException)
        {
            return null;
        }
    }

    // The archived ratings as they are now, for the counts: none without the lifecycle (tests of the live table). While the
    // archive can't be reached or read, a 503: counts without its ratings would let the
    // reconciler drop the names of members whose only ratings are archived, so the kit skips this owner instead.
    private ArchiveContents ArchiveForCounts()
    {
        if (_lifecycle is null)
        {
            return ArchiveContents.Empty;
        }

        ArchiveContents contents;
        try
        {
            contents = _lifecycle().Archive.Read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "The ratings' archive can't be reached for the users' counts");
            throw new PluginException(PluginErrorKind.Unavailable, ArchiveUnavailableMessage, ex);
        }

        if (contents.Problem is { } problem)
        {
            _logger.LogDebug("The ratings' archive can't be read for the users' counts: {Problem}", problem);
            throw PluginException.Unavailable(ArchiveUnavailableMessage);
        }

        return contents;
    }

    private async Task ReportAmbiguousAsync(int count, CancellationToken cancellationToken)
    {
        if (count <= 0 || _lifecycle is null)
        {
            return;
        }

        try
        {
            await _lifecycle().ReportAmbiguousAsync(count, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The Activity Log entry for {Count} ratings a remap detached as Ambiguous couldn't be written", count);
        }
    }

    // The counts and names as of the store's generation and the archive's contents: read again only after a write, a
    // restore, a recovery or an archive change, so the reconciler's and Diagnostics' calls cost no query in between (the kit
    // asks for memory reads; the archive caches its contents by the file's length and time).
    private Cached Current()
    {
        var generation = _store.Generation;
        var archive = ArchiveForCounts();
        lock (_lock)
        {
            if (_cached is { } cached && cached.Generation == generation && ReferenceEquals(cached.Archive, archive))
            {
                return cached;
            }
        }

        var counts = new Dictionary<Guid, int>(_store.CountByUser());
        if (archive.Records.Count > 0)
        {
            var formers = _store.GetFormerUsers();
            var pending = PendingPurgesOf(_store.GetMeta(ArchivePurgesKey));
            foreach (var record in archive.Records)
            {
                if (ArchivedUserIdOf(record) is { } id && !formers.ContainsKey(id) && !pending.Any(p => p.Matches(record)))
                {
                    counts[id] = counts.GetValueOrDefault(id) + 1;
                }
            }
        }

        var fresh = new Cached(generation, archive, counts, Parse(_store.GetMeta(KnownUsersKey)));
        lock (_lock)
        {
            _cached = fresh;
        }

        return fresh;
    }

    /// <summary>What the policies and remaps of one admin request did beyond the kit's report (<see cref="BeginTally"/>).</summary>
    internal sealed class UsersTally
    {
        /// <summary>Gets or sets the archived ratings deleted, anonymized or remapped.</summary>
        public int Archived { get; set; }

        /// <summary>Gets or sets the Ambiguous rows a remap made.</summary>
        public int Ambiguous { get; set; }
    }

    private sealed record Cached(long Generation, ArchiveContents Archive, IReadOnlyDictionary<Guid, int> Counts, KnownUsers Names);

    private sealed record PolicyOutcome(int Ratings, int Reviews, Guid? FormerUserId, int Archived)
    {
        public static PolicyOutcome None { get; } = new(0, 0, null, 0);
    }

    private sealed record ArchiveAccess(DetachedArchive Archive, string SnapshotDirectory);

    /// <summary>
    /// An archive rewrite a deleted-user policy left to do after its commit (<see cref="ArchivePurgesKey"/>): the user's
    /// lines, and every line of the user's rating ids (Delete, and Anonymize too, but for the lines of the former
    /// member it re-appended, so a line under an id the member had before a remap goes).
    /// </summary>
    /// <param name="UserId">The deleted user.</param>
    /// <param name="Ids">The rating ids, live and archived; none for an Anonymize recorded before 0.3.15.</param>
    /// <param name="KeepUserId">Anonymize's former member, whose lines stay; null for Delete.</param>
    internal sealed record PendingPurge(Guid UserId, IReadOnlyList<Guid> Ids, Guid? KeepUserId = null)
    {
        /// <summary>
        /// Tells whether an archived line goes: one of the user's, or of one of the ids, unless it is the former member's.
        /// </summary>
        /// <param name="record">The archived record.</param>
        /// <returns>Whether it goes.</returns>
        public bool Matches(ArchivedRecord record)
            => (ArchivedUserIdOf(record) == UserId || (Ids.Count > 0 && Guid.TryParse(record.Id, out var id) && Ids.Contains(id)))
                && (KeepUserId is not { } keep || ArchivedUserIdOf(record) != keep);

        /// <inheritdoc />
        public bool Equals(PendingPurge? other) => other is not null && other.UserId == UserId && other.KeepUserId == KeepUserId && other.Ids.SequenceEqual(Ids);

        /// <inheritdoc />
        public override int GetHashCode() => UserId.GetHashCode();
    }

    private sealed class RemapOutcome
    {
        public int Moved { get; set; }

        public int Archived { get; set; }

        public List<(Guid Detached, Guid ItemId, Guid Kept)> Conflicts { get; } = [];
    }
}
