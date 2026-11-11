using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.UserRatings.Domain;

namespace Jellyfin.Plugin.UserRatings.Storage;

/// <summary>
/// The operations of one unit of work (<see cref="IRatingsStore.WriteAsync{T}"/>): every one runs on
/// the open transaction of the kit's single writer, so a read, the checks that depend on it and the writes are one
/// transaction (a member's write with its <c>IfUpdatedUtc</c> check; a moderator's delete with its log row,
/// a deleted-user policy run, a JE apply, a relink's detach and attach). Nothing here names a SQLite type, so
/// services build units of work without touching Microsoft.Data.Sqlite (the kit's isolation).
/// </summary>
/// <remarks>
/// A unit of work is synchronous and must not keep the writer: it runs under the writer lock. When it throws, nothing it
/// did is committed. The items it touched are passed to <see cref="IRatingsStore.Changed"/> after it ends, which is how the
/// summary cache drops their entries (<c>SummaryCache</c>).
/// </remarks>
internal interface IRatingsWriter
{
    /// <summary>
    /// Gets the generation the store will have once this unit of work commits (<see cref="IRatingsStore.Generation"/>):
    /// what <see cref="IRatingsStore.WriteAsync{T}"/>'s caller gets back as the write's generation.
    /// </summary>
    long Generation { get; }

    /// <summary>
    /// Gets a rating by its id, attached or detached, as this transaction sees it.
    /// </summary>
    /// <param name="ratingId">The rating.</param>
    /// <returns>The rating, or null.</returns>
    RatingRecord? Get(Guid ratingId);

    /// <summary>
    /// Gets a member's attached rating of an item, as this transaction sees it.
    /// </summary>
    /// <param name="userId">The member.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>The rating, or null.</returns>
    RatingRecord? GetAttached(Guid userId, Guid itemId);

    /// <summary>
    /// Gets the ratings on items or inside them, as this transaction sees them: the rows whose item, series,
    /// season or album is one of the ids.
    /// </summary>
    /// <param name="itemIds">The item ids.</param>
    /// <param name="includeDetached">Whether detached rows count too.</param>
    /// <returns>The ratings, each once.</returns>
    IReadOnlyList<RatingRecord> GetOn(IReadOnlyCollection<Guid> itemIds, bool includeDetached);

    /// <summary>
    /// Gets every rating of a user id, attached and detached ones, as this transaction sees them (a deleted user's
    /// policy, <i>Remap by name</i>).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The ratings, in item order.</returns>
    IReadOnlyList<RatingRecord> GetByUser(Guid userId);

    /// <summary>
    /// Gets the former members, as this transaction sees them (the <c>Anonymize</c> policy).
    /// </summary>
    /// <returns>Id → display name.</returns>
    IReadOnlyDictionary<Guid, string> GetFormerUsers();

    /// <summary>
    /// Gets one of the plugin's meta values, as this transaction sees it (the move journal's stored position).
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The value, or null.</returns>
    string? GetMeta(string key);

    /// <summary>
    /// Sets a member's stars on an item: a new row, or the member's attached row with its stars replaced, keyed by member and
    /// item (<c>ON CONFLICT(UserId, ItemId) WHERE DetachedUtc IS NULL DO UPDATE</c>), so two writes of the same member are
    /// last-write-wins, never a 409. The identity snapshot and the keys are refreshed.
    /// </summary>
    /// <param name="userId">The member.</param>
    /// <param name="identity">The item's identity, captured now.</param>
    /// <param name="stars2">The stars in half-star units, 1 to 10.</param>
    /// <param name="utc">The time of the write (UTC).</param>
    /// <returns>The row as stored.</returns>
    RatingRecord SaveStars(Guid userId, IdentitySnapshot identity, int stars2, DateTime utc);

    /// <summary>
    /// Sets a member's review on an item, keyed as <see cref="SaveStars"/>: <c>EditCount</c> rises when an existing text
    /// changes and <c>ReviewUpdatedUtc</c> moves with the text or the spoiler flag, both in the same
    /// statement.
    /// </summary>
    /// <param name="userId">The member.</param>
    /// <param name="identity">The item's identity, captured now.</param>
    /// <param name="text">The normalised review, 1 to 20000 characters, no NUL.</param>
    /// <param name="isSpoiler">Whether it is marked as containing spoilers.</param>
    /// <param name="utc">The time of the write (UTC).</param>
    /// <returns>The row as stored.</returns>
    RatingRecord SaveReview(Guid userId, IdentitySnapshot identity, string text, bool isSpoiler, DateTime utc);

    /// <summary>
    /// Removes a row's stars, keeping its review ("Clear stars on a row with text"). A row without a review is
    /// deleted instead (<see cref="Delete"/>, with a tombstone).
    /// </summary>
    /// <param name="ratingId">The rating; it must have a review.</param>
    /// <param name="utc">The time of the write (UTC).</param>
    /// <returns>The row as stored.</returns>
    RatingRecord ClearStars(Guid ratingId, DateTime utc);

    /// <summary>
    /// Removes a row's review, keeping its stars (a member's <c>DELETE Mine/Review</c>; a moderator's "Remove text only").
    /// A row without stars is deleted instead.
    /// </summary>
    /// <param name="ratingId">The rating; it must have stars.</param>
    /// <param name="utc">The time of the write (UTC).</param>
    /// <returns>The row as stored.</returns>
    RatingRecord ClearReview(Guid ratingId, DateTime utc);

    /// <summary>
    /// Inserts a rating, or replaces the row with its id, with its keys (imports, relinks, detach and attach).
    /// A second attached row of the same member and item is a 409.
    /// </summary>
    /// <param name="record">The rating; validated as <see cref="IRatingsStore.UpsertAsync"/> validates it.</param>
    void Upsert(RatingRecord record);

    /// <summary>
    /// Deletes a rating and writes its tombstone.
    /// </summary>
    /// <param name="ratingId">The rating.</param>
    /// <param name="deletedBy">Who deleted it (<see cref="RatingValues.DeletedBy"/>).</param>
    /// <param name="deletedUtc">When (UTC).</param>
    /// <param name="origin">Where the deletion was made (<see cref="RatingValues.DeletionOrigin"/>); null for this server.</param>
    /// <returns>Whether a rating was deleted.</returns>
    bool Delete(Guid ratingId, string deletedBy, DateTime deletedUtc, string? origin = null);

    /// <summary>
    /// Removes a detached rating from the live table because the kit's archive now holds it: its row and
    /// keys go, and no tombstone is written, since it isn't deleted (it is exported and still reattaches from the archive).
    /// </summary>
    /// <param name="ratingId">The rating; it must be detached.</param>
    /// <returns>Whether a detached rating was removed.</returns>
    bool RemoveArchived(Guid ratingId);

    /// <summary>
    /// Appends a row to the moderation log.
    /// </summary>
    /// <param name="entry">The row.</param>
    /// <returns>Its number.</returns>
    long AppendModerationLog(ModerationLogRecord entry);

    /// <summary>
    /// Reads every moderation log row's id, time, action and rating, oldest first, without their texts (a
    /// restore tells the rows the restored snapshot holds from those written after it).
    /// </summary>
    /// <returns>The rows, with only those four fields.</returns>
    IReadOnlyList<ModerationLogRecord> GetModerationLogKeys();

    /// <summary>
    /// Deletes the moderation log's rows written before a time (<c>ModerationLogDays</c>).
    /// </summary>
    /// <param name="cutoffUtc">Rows before it go; a row at it stays.</param>
    /// <param name="removalsCutoffUtc">The rows a restore reads go only before this one too (the oldest kept snapshot's
    /// time): a moderator's <c>RemoveText</c> and <c>RemoveEntry</c> (since 0.3.21), and since
    /// 0.3.22 every <c>ImportEnvelope</c> and <c>StartEmpty</c>; null for
    /// <paramref name="cutoffUtc"/>.</param>
    /// <returns>How many went.</returns>
    int DeleteModerationLogBefore(DateTime cutoffUtc, DateTime? removalsCutoffUtc = null);

    /// <summary>
    /// Tells whether a moderation log row about a user has an action (<c>KeepAsOrphan</c>
    /// applied again writes no second <c>UserOrphaned</c> row, whatever a moderator logged about the user in between).
    /// </summary>
    /// <param name="targetUserId">The user (<c>TargetUserId</c>).</param>
    /// <param name="action">The action.</param>
    /// <returns>Whether any row about the user has it.</returns>
    bool HasModerationAction(Guid targetUserId, string action);

    /// <summary>
    /// Tells whether a rating id has a tombstone (a reattach from the archive never brings back a
    /// deleted rating, even one whose archive line is still there).
    /// </summary>
    /// <param name="ratingId">The rating.</param>
    /// <returns>Whether it has one.</returns>
    bool HasTombstone(Guid ratingId);

    /// <summary>
    /// Reads a rating id's tombstone, for who deleted it (an import skips a rating the ledger maps to a
    /// deleted one, as <c>PreviouslyPurged</c> when an admin's Purge deleted it).
    /// </summary>
    /// <param name="ratingId">The rating.</param>
    /// <returns>The tombstone, or null.</returns>
    TombstoneRecord? GetTombstone(Guid ratingId);

    /// <summary>
    /// Reads a user's tombstones on an item: the JE import's guard for an entry without a ledger row,
    /// so a rating deleted here since JE's copy was written never comes back.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>The tombstones, oldest first.</returns>
    IReadOnlyList<TombstoneRecord> GetTombstonesOf(Guid userId, Guid itemId);

    /// <summary>
    /// Writes the tombstone of a rating the live table doesn't hold (an archived rating of a user
    /// deleted under the <c>Delete</c> policy, whose line leaves the kit's archive), so an import never brings it back.
    /// </summary>
    /// <param name="ratingId">The rating.</param>
    /// <param name="userId">Its member.</param>
    /// <param name="itemId">The item it was on.</param>
    /// <param name="deletedBy">Who deleted it (<see cref="RatingValues.DeletedBy"/>).</param>
    /// <param name="deletedUtc">When (UTC).</param>
    /// <param name="origin">Where the deletion was made (<see cref="RatingValues.DeletionOrigin"/>); null for this server.</param>
    void AddTombstone(Guid ratingId, Guid userId, Guid itemId, string deletedBy, DateTime deletedUtc, string? origin = null);

    /// <summary>
    /// Records (or replaces) an imported source entry and the rows it produced.
    /// </summary>
    /// <param name="entry">The entry.</param>
    void RecordImport(ImportLedgerRecord entry);

    /// <summary>
    /// Adds a former member (the <c>Anonymize</c> policy): the id a deleted user's rows carry from now on, with the name
    /// they show.
    /// </summary>
    /// <param name="id">The new id; not a user's.</param>
    /// <param name="originalUserId">The deleted user's id.</param>
    /// <param name="displayName">"Former member", "Former member 2", ….</param>
    /// <param name="deletedUtc">When the user was deleted (UTC).</param>
    void AddFormerUser(Guid id, Guid originalUserId, string displayName, DateTime deletedUtc);

    /// <summary>
    /// Sets one of the plugin's meta values.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    void SetMeta(string key, string value);

    /// <summary>
    /// Reads an import ledger entry in this unit of work (the envelope import).
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="sourceKey">The key in the source.</param>
    /// <returns>The entry, or null.</returns>
    ImportLedgerRecord? GetImport(string source, string sourceKey);

    /// <summary>
    /// Empties what an envelope Replace replaces: every rating, the moderation log and the import ledger; the
    /// tombstones, the former members and Meta stay, and so do the envelope ledger's rows that point at a deleted rating.
    /// </summary>
    /// <returns>The ratings deleted.</returns>
    int WipeForReplace();

    /// <summary>
    /// Adds a former member's row unless one with its id is there (the envelope import).
    /// </summary>
    /// <param name="row">The row.</param>
    /// <returns>Whether it was added.</returns>
    bool AddFormerUserIfAbsent(FormerUserRecord row);
}
