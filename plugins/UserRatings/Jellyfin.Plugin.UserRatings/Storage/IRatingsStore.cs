using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.UserRatings.Domain;

namespace Jellyfin.Plugin.UserRatings.Storage;

/// <summary>
/// The ratings store: CRUD on <c>Ratings</c> with their provider keys and container ids, the per-user
/// summary rows, tombstones, the moderation log, the import ledger and the plugin's meta values. Writes go through the
/// kit's single writer, reads through pooled connections; every write is one unit of work (<see cref="WriteAsync{T}"/>),
/// one transaction, after which <see cref="Changed"/> names the items it touched. While the database is read-only (a newer
/// schema, a failed upgrade, damage) writes answer 503 and reads go on where the database allows them; a database
/// another connection keeps locked for more than 5 s answers 503 too.
/// </summary>
/// <remarks>
/// No member names a SQLite type (the kit's isolation): services and controllers use this interface
/// only, and a server whose Microsoft.Data.Sqlite this build can't bind answers 503 here instead of failing elsewhere.
/// </remarks>
internal interface IRatingsStore
{
    /// <summary>
    /// Raised after every unit of work that wrote ratings (<see cref="WriteAsync{T}"/>, and every write method below, which
    /// are units of one operation), once the kit has committed it and raised <see cref="Generation"/>, with the items it
    /// touched; also after one that rolled back. The summary cache drops their entries (<c>SummaryCache</c>).
    /// Handlers run on the writer's thread after the writer lock is released, and must not throw (an exception is logged).
    /// </summary>
    event EventHandler<RatingsChangedEventArgs>? Changed;

    /// <summary>
    /// Gets the store's generation: raised by every committed write, restore and recovery (the kit's
    /// <c>SqliteDatabase.Generation</c>). The summary cache inserts a fill only while the generation it read before its
    /// query is unchanged (<c>SummaryCache</c>).
    /// </summary>
    long Generation { get; }

    /// <summary>
    /// Gets how many times the data was replaced by a restore (the kit's <c>SqliteDatabase.RestoreCount</c>: an admin's
    /// snapshot restore, a corruption restore, the restore at open). The summary cache compares it on every read and starts
    /// over when it moved, since no write named the items a restore changed.
    /// </summary>
    long RestoreCount { get; }

    /// <summary>
    /// Gets a value indicating whether the store refuses writes: data from a newer plugin version, a failed upgrade, damage,
    /// or a database that can't be opened.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Gets the banner's sentence while the store is anything but Ok or Migrated; null otherwise.
    /// </summary>
    string? StatusMessage { get; }

    /// <summary>
    /// Gets the folder of the database file (<c>data/userratings</c>), where the Data tab's <c>tmp/</c> holds an
    /// import's upload and an export job's file.
    /// </summary>
    string DataDirectory { get; }

    /// <summary>
    /// Throws the 503 every write throws while the store is read-only, and does nothing while it is writable: for a request
    /// that looks something up before it writes.
    /// </summary>
    /// <exception cref="Kit.Api.PluginException">503: the database is read-only or can't be opened.</exception>
    void ThrowIfReadOnly();

    /// <summary>
    /// Runs one unit of work: <paramref name="work"/> gets an <see cref="IRatingsWriter"/> on the open
    /// transaction of the kit's single writer (<c>BEGIN IMMEDIATE</c>), so its reads, the checks that depend on them and its
    /// writes are one transaction: it commits when <paramref name="work"/> returns and rolls back when it throws. Then
    /// <see cref="Changed"/> names the items it touched.
    /// </summary>
    /// <typeparam name="T">The result; to return the write's generation, include <see cref="IRatingsWriter.Generation"/>.</typeparam>
    /// <param name="work">The unit of work; synchronous, it must not keep the writer.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer; a started unit always finishes.</param>
    /// <returns>Its result.</returns>
    /// <exception cref="Kit.Api.PluginException">503 while read-only, damaged or busy (another connection held the database
    /// for more than 5 s); whatever <paramref name="work"/> throws.</exception>
    Task<T> WriteAsync<T>(Func<IRatingsWriter, T> work, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a rating by its id, attached or detached.
    /// </summary>
    /// <param name="ratingId">The rating's id.</param>
    /// <returns>The rating, or null.</returns>
    RatingRecord? Get(Guid ratingId);

    /// <summary>
    /// Gets a member's attached rating of an item (at most one).
    /// </summary>
    /// <param name="userId">The member.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>The rating, or null.</returns>
    RatingRecord? GetAttached(Guid userId, Guid itemId);

    /// <summary>
    /// Gets every attached rating of an item, newest change first (the details read: one indexed query).
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The ratings.</returns>
    IReadOnlyList<RatingRecord> GetAttachedForItem(Guid itemId);

    /// <summary>
    /// Gets every attached rating of a member, newest change first (<c>Mine/Inaccessible</c>; the index
    /// <c>IX_Ratings_User</c>).
    /// </summary>
    /// <param name="userId">The member.</param>
    /// <returns>The ratings.</returns>
    IReadOnlyList<RatingRecord> GetAttachedForUser(Guid userId);

    /// <summary>
    /// Gets the ratings on items or inside them: the rows whose item, series, season or album is one of the
    /// ids, so an updated series names the rows of its episodes too.
    /// </summary>
    /// <param name="itemIds">The item ids.</param>
    /// <param name="includeDetached">Whether detached rows count too.</param>
    /// <returns>The ratings, each once.</returns>
    IReadOnlyList<RatingRecord> GetOn(IReadOnlyCollection<Guid> itemIds, bool includeDetached);

    /// <summary>
    /// Gets the ids of the items that have attached ratings, each once (the kit's verify sweep).
    /// </summary>
    /// <returns>The item ids.</returns>
    IReadOnlyList<Guid> GetAttachedItemIds();

    /// <summary>
    /// Gets every detached rating, oldest detach first (the kit's re-resolution; Diagnostics' counts by reason).
    /// </summary>
    /// <returns>The ratings.</returns>
    IReadOnlyList<RatingRecord> GetDetached();

    /// <summary>
    /// Gets the per-user contributions of the attached ratings of items (the summary query).
    /// </summary>
    /// <param name="itemIds">The items.</param>
    /// <returns>One row per attached rating.</returns>
    IReadOnlyList<SummaryRow> GetSummaryRows(IReadOnlyCollection<Guid> itemIds);

    /// <summary>
    /// Gets a rating's provider keys (<c>RatingKeys</c>).
    /// </summary>
    /// <param name="ratingId">The rating.</param>
    /// <returns>The keys, in ordinal order.</returns>
    IReadOnlyList<string> GetKeys(Guid ratingId);

    /// <summary>
    /// Gets the ratings that share any of the keys, attached or detached ones (relinking).
    /// </summary>
    /// <param name="keys">The keys, as the kit's <c>ItemIdentity.StrongKeys</c> writes them.</param>
    /// <param name="detached">Which ratings: the detached ones (true) or the attached ones (false), never both.</param>
    /// <returns>The ratings, each once.</returns>
    IReadOnlyList<RatingRecord> FindByKeys(IReadOnlyCollection<string> keys, bool detached);

    /// <summary>
    /// Gets the items that have attached ratings sharing any of the keys (copies): their ids only, one
    /// <c>SELECT DISTINCT ItemId</c>, never the rows and their review text.
    /// </summary>
    /// <param name="keys">The keys, as the kit's <c>ItemIdentity.StrongKeys</c> writes them.</param>
    /// <returns>The item ids, each once.</returns>
    IReadOnlyList<Guid> FindItemIdsByKeys(IReadOnlyCollection<string> keys);

    /// <summary>
    /// Inserts a rating, or replaces the row with its id, together with its provider keys and container ids, in one
    /// transaction.
    /// </summary>
    /// <param name="record">The rating; validated (growable enums, the star range, the row's shape) before anything is
    /// written.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>The rating as stored.</returns>
    /// <exception cref="ArgumentException">The record breaks a rule of the schema.</exception>
    /// <exception cref="Kit.Api.PluginException">409 when the member already has another attached rating of the item; 503
    /// while read-only.</exception>
    Task<RatingRecord> UpsertAsync(RatingRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts or replaces many ratings with their keys in one transaction (imports).
    /// </summary>
    /// <param name="records">The ratings.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>How many were written.</returns>
    Task<int> UpsertManyAsync(IReadOnlyCollection<RatingRecord> records, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a rating (its keys go with it) and writes its tombstone in the same transaction.
    /// </summary>
    /// <param name="ratingId">The rating.</param>
    /// <param name="deletedBy">Who deleted it (<see cref="RatingValues.DeletedBy"/>).</param>
    /// <param name="deletedUtc">When (UTC).</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>Whether a rating was deleted.</returns>
    Task<bool> DeleteAsync(Guid ratingId, string deletedBy, DateTime deletedUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a deleted rating's tombstone.
    /// </summary>
    /// <param name="ratingId">The rating's id.</param>
    /// <returns>The tombstone, or null.</returns>
    TombstoneRecord? GetTombstone(Guid ratingId);

    /// <summary>
    /// Appends a row to the moderation log.
    /// </summary>
    /// <param name="entry">The row (its <see cref="ModerationLogRecord.Id"/> is ignored).</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>The row's number.</returns>
    Task<long> AppendModerationLogAsync(ModerationLogRecord entry, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a page of the moderation log, newest first.
    /// </summary>
    /// <param name="startIndex">Rows to skip.</param>
    /// <param name="limit">Rows to return.</param>
    /// <returns>The rows.</returns>
    IReadOnlyList<ModerationLogRecord> GetModerationLog(int startIndex, int limit);

    /// <summary>
    /// Gets a page of the moderation log, newest first, and how many rows the filter matches
    /// (<c>Admin/ModerationLog</c>).
    /// </summary>
    /// <param name="startIndex">Rows to skip.</param>
    /// <param name="limit">Rows to return.</param>
    /// <param name="action">Only rows of this action (one of <see cref="RatingValues.ModerationAction.All"/>); all when null.</param>
    /// <returns>The rows and the total.</returns>
    (IReadOnlyList<ModerationLogRecord> Rows, int Total) GetModerationLogPage(int startIndex, int limit, string? action);

    /// <summary>
    /// Counts the moderation log's rows written before a time (what <c>ModerationLogDays</c> would delete).
    /// </summary>
    /// <param name="cutoffUtc">The time.</param>
    /// <returns>The count.</returns>
    int CountModerationLogBefore(DateTime cutoffUtc);

    /// <summary>
    /// Counts the attached ratings and reviews by their item's kind (what the Settings tab's dry run says a change to
    /// <c>RateableItemTypes</c>, <c>EnableRatings</c> or <c>EnableReviews</c> hides).
    /// </summary>
    /// <returns>The ratings and reviews of each stored kind name.</returns>
    IReadOnlyDictionary<string, (int Ratings, int Reviews)> CountAttachedByKind();

    /// <summary>
    /// Counts the attached reviews longer than a limit, in UTF-16 code units as <c>ReviewText.Validate</c> measures them
    /// (what a lower <c>MaxReviewLength</c> means).
    /// </summary>
    /// <param name="length">The limit.</param>
    /// <returns>The count.</returns>
    int CountReviewsLongerThan(int length);

    /// <summary>
    /// Gets the rows the Moderation list shows (<c>Admin/Reviews</c>), the row changed last first, filtered by
    /// what the database can filter, and the number the filters match, in one read: a page of them
    /// (for an API key's list without a search, which needs nothing else), or every one (with
    /// <paramref name="limit"/> null) for the caller to narrow further (access, search) and page.
    /// </summary>
    /// <param name="userId">Only this member's rows, or null.</param>
    /// <param name="itemId">Only the rows on this item (attached or last attached to it), or null.</param>
    /// <param name="hasText">Only rows with a review (true) or without (false), or null.</param>
    /// <param name="detached">Only detached rows (true) or attached ones (false), or null.</param>
    /// <param name="sinceUtc">Only rows changed at or after this time, or null.</param>
    /// <param name="startIndex">The page's first row (with a limit).</param>
    /// <param name="limit">The page's size, or null for every row.</param>
    /// <returns>The rows and how many the filters match.</returns>
    (IReadOnlyList<RatingRecord> Rows, int Total) GetForModeration(Guid? userId, Guid? itemId, bool? hasText, bool? detached, DateTime? sinceUtc, int startIndex, int? limit);

    /// <summary>
    /// Gets an imported source entry with the rows it produced.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="sourceKey">The entry's key.</param>
    /// <returns>The entry, or null.</returns>
    ImportLedgerRecord? GetImport(string source, string sourceKey);

    /// <summary>
    /// Records (or replaces) an imported source entry and the rows it produced.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>A task.</returns>
    Task RecordImportAsync(ImportLedgerRecord entry, CancellationToken cancellationToken);

    /// <summary>
    /// Gets one of the plugin's meta values (<c>Meta</c>).
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The value, or null.</returns>
    string? GetMeta(string key);

    /// <summary>
    /// Sets one of the plugin's meta values.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>A task.</returns>
    Task SetMetaAsync(string key, string value, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the former members (the <c>Anonymize</c> policy): the ids their rows carry, with the name shown for them. Their
    /// rows count and show as "Former member"; an id that is neither a user nor a former member is an orphan.
    /// </summary>
    /// <returns>Id → display name.</returns>
    IReadOnlyDictionary<Guid, string> GetFormerUsers();

    /// <summary>
    /// Counts the ratings of every user id, attached and detached ones, the former members' left out (what
    /// the kit's user reconciliation compares with the server's users).
    /// </summary>
    /// <returns>User id → ratings; ids without ratings are left out.</returns>
    IReadOnlyDictionary<Guid, int> CountByUser();

    /// <summary>
    /// Takes the kit's pre-operation snapshot of the database (newest 5) before the kit's users operations change
    /// ratings (a deleted user's policy, <i>Apply deleted-user policy</i>, <i>Remap by name</i>).
    /// </summary>
    /// <param name="operation">One of the kit's <c>SnapshotOperations</c>.</param>
    /// <param name="cancellationToken">Cancels waiting for another snapshot.</param>
    /// <returns>The snapshot's path, or null when none was written (read-only).</returns>
    Task<string?> PreOpSnapshotAsync(string operation, CancellationToken cancellationToken);

    /// <summary>
    /// Asks for a checkpoint of the database's write-ahead log, which cuts it to 0 bytes, and returns at once (the kit's
    /// <c>RequestCheckpoint</c>; an earlier checkpoint ran on the caller's thread and held
    /// the writer lock while it waited for readers): after the commit of a write that deleted a rating or removed or replaced
    /// a review, so no earlier version of a page that held the text stays in the <c>-wal</c> file (the deleted cells
    /// themselves are zeroed by the kit's <c>secure_delete</c>). The kit runs it in the background, a burst of requests as
    /// one, and tries again while another connection reads an older snapshot; one whose tries run out is a Warning and stays
    /// pending (Diagnostics <c>Store.Checkpoint.Pending</c>) for the next write or the daily maintenance's <c>Checkpoint</c>
    /// step. Callers use <see cref="RatingsCheckpoint.RequestAfter"/>.
    /// </summary>
    /// <returns>A task that completes with whether a checkpoint covering this request completed; false while read-only or
    /// when its tries ran out (still pending). It never fails.</returns>
    Task<bool> RequestCheckpoint();

    /// <summary>
    /// Waits for the checkpoints asked for so far without asking for one (the kit's <c>WaitForCheckpointAsync</c>).
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>Whether none is pending.</returns>
    Task<bool> WaitForCheckpointAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Counts the stored rows, for Diagnostics.
    /// </summary>
    /// <returns>The counts.</returns>
    RatingsStoreCounts Count();

    /// <summary>
    /// Runs one unit of work that commits or rolls back (the envelope import: a dry run is the import's own work, rolled
    /// back), with the kit's pre-operation snapshot taken first in the same hold of the writer lock when
    /// <paramref name="preOpOperation"/> is given, so no write commits between the snapshot and the work. With a snapshot,
    /// <paramref name="check"/> runs before it, in the same hold, rolled back: one that throws (an import whose plan changed
    /// since its dry run) writes nothing and leaves no snapshot.
    /// </summary>
    /// <typeparam name="T">The work's result.</typeparam>
    /// <param name="work">The operations.</param>
    /// <param name="commit">Whether to commit; false rolls the work back.</param>
    /// <param name="preOpOperation">The snapshot's operation (<c>SnapshotOperations</c>), or null for none.</param>
    /// <param name="check">Runs before the snapshot, always rolled back, and throws to refuse the work; null for none.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>The work's result and the snapshot's path, if one was taken.</returns>
    Task<(T Result, string? Safety)> RunWriteAsync<T>(Func<IRatingsWriter, T> work, bool commit, string? preOpOperation, Action<IRatingsWriter>? check, CancellationToken cancellationToken);

    /// <summary>
    /// Reads every table an export holds, in one read transaction.
    /// </summary>
    /// <returns>The tables.</returns>
    RatingsTables ReadTables();

    /// <summary>
    /// Reads the tables of one of this store's pre-operation snapshots, read only (a restore re-applies
    /// the deletions recorded in its own safety snapshot, the ratings as they were just before it).
    /// </summary>
    /// <param name="name">The snapshot's file name, as a restore answers it.</param>
    /// <returns>The tables.</returns>
    /// <exception cref="Kit.Api.PluginException">404 no such pre-operation snapshot; 503 it can't be read.</exception>
    RatingsTables ReadPreOpSnapshotTables(string name);

    /// <summary>
    /// Lists the database's snapshots, both classes, newest first (the kit's; the Data tab).
    /// </summary>
    /// <returns>The snapshots.</returns>
    IReadOnlyList<StoreSnapshotInfo> ListSnapshots();

    /// <summary>
    /// Restores a snapshot (the kit's: the logical restore with a <c>restore</c> safety snapshot, or its corruption restore
    /// when the database is damaged).
    /// </summary>
    /// <param name="name">The snapshot's file name.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>What was restored.</returns>
    Task<SnapshotRestoreResult> RestoreSnapshotAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Describes the database's integrity for the Data tab's banner: its status, whether it is read-only and damaged,
    /// the message, the last recovery and the quarantine folders.
    /// </summary>
    /// <returns>The description.</returns>
    IDictionary<string, object?> DescribeIntegrity();

    /// <summary>
    /// Starts the ratings empty, as only an admin can choose: while the database is damaged, the
    /// kit's corruption restore with <c>startEmpty</c>. No snapshot is restored; the damaged database goes to a quarantine
    /// folder, a new empty one takes its place, and every snapshot is kept until one of them is restored.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>Where the damaged files went.</returns>
    /// <exception cref="Kit.Api.PluginException">409 the database isn't damaged, or a readable one is in place now; 503 it
    /// couldn't be moved aside or the new one couldn't be made (what was moved is put back).</exception>
    Task<Kit.Sqlite.SqliteRecoveryResult> StartEmptyAsync(CancellationToken cancellationToken);
}
