using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.UserRatings.Domain;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.UserRatings.Storage;

/// <summary>
/// The SQL of <see cref="SqliteRatingsStore"/>: every member takes a connection (and a transaction for writes), so every
/// SQLite type of the plugin's own code is in this class's signatures and in the store's <c>NoInlining</c> methods that
/// call it (the kit's isolation; the tests check it on the compiled IL). Ids are stored as <c>"N"</c> GUIDs, times as
/// ISO-8601 round-trip UTC text, provider-id maps as JSON.
/// </summary>
internal static partial class RatingsSql
{
    /// <summary>How many values one <c>IN (…)</c> carries.</summary>
    public const int InChunk = 500;

    private const int SqliteConstraint = 19;

    // SQLITE_CONSTRAINT_UNIQUE, and the columns SQLite names for UX_Ratings_User_Item.
    private const int SqliteConstraintUnique = 2067;
    private const string UserItemColumns = "Ratings.UserId, Ratings.ItemId";

    private const string Columns =
        "Id, UserId, ItemId, Stars2, ReviewText, IsSpoiler, CreatedUtc, UpdatedUtc, ReviewUpdatedUtc, EditCount, Origin, "
        + "ItemKind, ItemName, ProductionYear, ProviderIds, SeriesProviderIds, SeriesName, SeasonNumber, EpisodeNumber, ItemPath, "
        + "SeriesId, SeasonId, AlbumId, DetachedUtc, DetachReason, MirroredValue, AttachedUtc";

    private const string UpsertSql =
        "INSERT INTO Ratings (" + Columns + ") VALUES ($Id, $UserId, $ItemId, $Stars2, $ReviewText, $IsSpoiler, $CreatedUtc, $UpdatedUtc, "
        + "$ReviewUpdatedUtc, $EditCount, $Origin, $ItemKind, $ItemName, $ProductionYear, $ProviderIds, $SeriesProviderIds, $SeriesName, "
        + "$SeasonNumber, $EpisodeNumber, $ItemPath, $SeriesId, $SeasonId, $AlbumId, $DetachedUtc, $DetachReason, $MirroredValue, $AttachedUtc) "
        + "ON CONFLICT(Id) DO UPDATE SET UserId = excluded.UserId, ItemId = excluded.ItemId, Stars2 = excluded.Stars2, "
        + "ReviewText = excluded.ReviewText, IsSpoiler = excluded.IsSpoiler, CreatedUtc = excluded.CreatedUtc, UpdatedUtc = excluded.UpdatedUtc, "
        + "ReviewUpdatedUtc = excluded.ReviewUpdatedUtc, EditCount = excluded.EditCount, Origin = excluded.Origin, ItemKind = excluded.ItemKind, "
        + "ItemName = excluded.ItemName, ProductionYear = excluded.ProductionYear, ProviderIds = excluded.ProviderIds, "
        + "SeriesProviderIds = excluded.SeriesProviderIds, SeriesName = excluded.SeriesName, SeasonNumber = excluded.SeasonNumber, "
        + "EpisodeNumber = excluded.EpisodeNumber, ItemPath = excluded.ItemPath, SeriesId = excluded.SeriesId, SeasonId = excluded.SeasonId, "
        + "AlbumId = excluded.AlbumId, DetachedUtc = excluded.DetachedUtc, DetachReason = excluded.DetachReason, MirroredValue = excluded.MirroredValue, "
        + "AttachedUtc = excluded.AttachedUtc;";

    // A member's write, keyed by member and item: a new row, or on the attached row the member already
    // has (UX_Ratings_User_Item, a partial index, so the conflict target repeats its WHERE) only the columns this write
    // changes, plus the identity snapshot, refreshed on every write. A double click or two tabs is last-write-wins,
    // never a 409. RETURNING gives the row as stored (SQLite 3.35+; the server's is 3.46.1).
    private const string MemberInsert =
        "INSERT INTO Ratings (" + Columns + ") VALUES ($Id, $UserId, $ItemId, $Stars2, $ReviewText, $IsSpoiler, $Now, $Now, $ReviewUpdatedUtc, 0, 'User', "
        + "$ItemKind, $ItemName, $ProductionYear, $ProviderIds, $SeriesProviderIds, $SeriesName, $SeasonNumber, $EpisodeNumber, $ItemPath, "
        + "$SeriesId, $SeasonId, $AlbumId, NULL, NULL, NULL, NULL) "
        + "ON CONFLICT(UserId, ItemId) WHERE DetachedUtc IS NULL DO UPDATE SET ";

    private const string IdentitySet =
        "ItemKind = excluded.ItemKind, ItemName = excluded.ItemName, ProductionYear = excluded.ProductionYear, ProviderIds = excluded.ProviderIds, "
        + "SeriesProviderIds = excluded.SeriesProviderIds, SeriesName = excluded.SeriesName, SeasonNumber = excluded.SeasonNumber, "
        + "EpisodeNumber = excluded.EpisodeNumber, ItemPath = excluded.ItemPath, SeriesId = excluded.SeriesId, SeasonId = excluded.SeasonId, "
        + "AlbumId = excluded.AlbumId";

    private const string SaveStarsSql =
        MemberInsert + "Stars2 = excluded.Stars2, UpdatedUtc = excluded.UpdatedUtc, " + IdentitySet + " RETURNING " + Columns + ";";

    // EditCount counts changes of an existing text, the first text isn't one, so "(edited)" never comes from the
    // spoiler flag; ReviewUpdatedUtc moves when the text or the spoiler flag changes (the flag
    // is part of the review, so a window that read it before gets the 409 of IfReviewUpdatedUtc instead of putting the old
    // flag back with a 200). Every SET expression sees the row as it was.
    private const string SaveReviewSql =
        MemberInsert + "ReviewText = excluded.ReviewText, IsSpoiler = excluded.IsSpoiler, "
        + "EditCount = Ratings.EditCount + (Ratings.ReviewText IS NOT NULL AND Ratings.ReviewText IS NOT excluded.ReviewText), "
        + "ReviewUpdatedUtc = CASE WHEN Ratings.ReviewText IS excluded.ReviewText AND Ratings.IsSpoiler IS excluded.IsSpoiler THEN Ratings.ReviewUpdatedUtc ELSE excluded.ReviewUpdatedUtc END, "
        + "UpdatedUtc = excluded.UpdatedUtc, " + IdentitySet + " RETURNING " + Columns + ";";

    private const string ClearStarsSql =
        "UPDATE Ratings SET Stars2 = NULL, UpdatedUtc = $Now WHERE Id = $Id AND ReviewText IS NOT NULL RETURNING " + Columns + ";";

    // The review gone, the row is star-only: a later review starts again, not "(edited)".
    private const string ClearReviewSql =
        "UPDATE Ratings SET ReviewText = NULL, IsSpoiler = 0, ReviewUpdatedUtc = NULL, EditCount = 0, UpdatedUtc = $Now "
        + "WHERE Id = $Id AND Stars2 IS NOT NULL RETURNING " + Columns + ";";

    // ------------------------------------------------------------------------------------------------ writes

    /// <summary>
    /// Writes one rating and replaces its keys.
    /// </summary>
    /// <param name="connection">The writer's connection.</param>
    /// <param name="transaction">Its transaction.</param>
    /// <param name="record">The validated rating.</param>
    /// <exception cref="PluginException">409: the member already has another attached rating of the item.</exception>
    /// <exception cref="InvalidOperationException">The database refused the row for another reason (a CHECK, NOT NULL or
    /// foreign key): a rule the store's validation doesn't check, so a fault, never the member's 409.</exception>
    public static void Upsert(SqliteConnection connection, SqliteTransaction transaction, RatingRecord record)
    {
        var id = Id(record.Id);
        var identity = record.Identity;
        try
        {
            using (var command = Command(connection, transaction, UpsertSql))
            {
                command.Parameters.AddWithValue("$Id", id);
                command.Parameters.AddWithValue("$UserId", Id(record.UserId));
                command.Parameters.AddWithValue("$ItemId", Id(identity.ItemId));
                command.Parameters.AddWithValue("$Stars2", (object?)record.Stars2 ?? DBNull.Value);
                command.Parameters.AddWithValue("$ReviewText", (object?)record.ReviewText ?? DBNull.Value);
                command.Parameters.AddWithValue("$IsSpoiler", record.IsSpoiler ? 1 : 0);
                command.Parameters.AddWithValue("$CreatedUtc", Time(record.CreatedUtc));
                command.Parameters.AddWithValue("$UpdatedUtc", Time(record.UpdatedUtc));
                command.Parameters.AddWithValue("$ReviewUpdatedUtc", Time(record.ReviewUpdatedUtc));
                command.Parameters.AddWithValue("$EditCount", record.EditCount);
                command.Parameters.AddWithValue("$Origin", record.Origin);
                AddIdentity(command, identity);
                command.Parameters.AddWithValue("$DetachedUtc", Time(record.DetachedUtc));
                command.Parameters.AddWithValue("$DetachReason", (object?)record.DetachReason ?? DBNull.Value);
                command.Parameters.AddWithValue("$MirroredValue", (object?)record.MirroredValue ?? DBNull.Value);
                command.Parameters.AddWithValue("$AttachedUtc", Time(record.AttachedUtc));
                command.ExecuteNonQuery();
            }
        }
        catch (SqliteException ex) when (IsUserItemConflict(ex))
        {
            throw new PluginException(PluginErrorKind.Conflict, "This member already has a rating of this item.", ex);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint)
        {
            throw Refused(record.Id, ex);
        }

        WriteKeys(connection, transaction, record.Id, identity);
    }

    /// <summary>
    /// Sets a member's stars on an item: a keyed upsert (<see cref="SaveStarsSql"/>), then the row's keys.
    /// </summary>
    /// <param name="connection">The writer's connection.</param>
    /// <param name="transaction">Its transaction.</param>
    /// <param name="userId">The member.</param>
    /// <param name="identity">The item's identity, as captured now.</param>
    /// <param name="stars2">The stars in half-star units, 1 to 10.</param>
    /// <param name="utc">The time of the write.</param>
    /// <param name="warnings">Where a stored value read tolerantly is reported.</param>
    /// <returns>The row as stored.</returns>
    public static RatingRecord SaveStars(SqliteConnection connection, SqliteTransaction transaction, Guid userId, IdentitySnapshot identity, int stars2, DateTime utc, RatingsRowWarnings? warnings)
        => SaveForMember(connection, transaction, SaveStarsSql, userId, identity, stars2, null, false, utc, warnings);

    /// <summary>
    /// Sets a member's review on an item: a keyed upsert (<see cref="SaveReviewSql"/>, <c>EditCount</c> counted in it),
    /// then the row's keys.
    /// </summary>
    /// <param name="connection">The writer's connection.</param>
    /// <param name="transaction">Its transaction.</param>
    /// <param name="userId">The member.</param>
    /// <param name="identity">The item's identity, as captured now.</param>
    /// <param name="text">The normalised review.</param>
    /// <param name="isSpoiler">Whether it is marked as containing spoilers.</param>
    /// <param name="utc">The time of the write.</param>
    /// <param name="warnings">Where a stored value read tolerantly is reported.</param>
    /// <returns>The row as stored.</returns>
    public static RatingRecord SaveReview(SqliteConnection connection, SqliteTransaction transaction, Guid userId, IdentitySnapshot identity, string text, bool isSpoiler, DateTime utc, RatingsRowWarnings? warnings)
        => SaveForMember(connection, transaction, SaveReviewSql, userId, identity, null, text, isSpoiler, utc, warnings);

    /// <summary>
    /// Removes a row's stars; the row must keep its review.
    /// </summary>
    /// <param name="connection">The writer's connection.</param>
    /// <param name="transaction">Its transaction.</param>
    /// <param name="ratingId">The rating.</param>
    /// <param name="utc">The time of the write.</param>
    /// <param name="warnings">Where a stored value read tolerantly is reported.</param>
    /// <returns>The row as stored, or <c>null</c> when there is no such row with a review.</returns>
    public static RatingRecord? ClearStars(SqliteConnection connection, SqliteTransaction transaction, Guid ratingId, DateTime utc, RatingsRowWarnings? warnings)
        => UpdateReturning(connection, transaction, ClearStarsSql, ratingId, utc, warnings);

    /// <summary>
    /// Removes a row's review (also a moderator's "Remove text only"); the row must keep its stars.
    /// </summary>
    /// <param name="connection">The writer's connection.</param>
    /// <param name="transaction">Its transaction.</param>
    /// <param name="ratingId">The rating.</param>
    /// <param name="utc">The time of the write.</param>
    /// <param name="warnings">Where a stored value read tolerantly is reported.</param>
    /// <returns>The row as stored, or <c>null</c> when there is no such row with stars.</returns>
    public static RatingRecord? ClearReview(SqliteConnection connection, SqliteTransaction transaction, Guid ratingId, DateTime utc, RatingsRowWarnings? warnings)
        => UpdateReturning(connection, transaction, ClearReviewSql, ratingId, utc, warnings);

    /// <summary>
    /// Gets the item a rating is on, attached or detached.
    /// </summary>
    /// <param name="connection">A connection.</param>
    /// <param name="transaction">Its transaction, or <c>null</c>.</param>
    /// <param name="ratingId">The rating.</param>
    /// <returns>The item id, or <c>null</c> when there is no such rating.</returns>
    public static Guid? ItemIdOf(SqliteConnection connection, SqliteTransaction? transaction, Guid ratingId)
    {
        using var command = Command(connection, transaction, "SELECT ItemId FROM Ratings WHERE Id = $Id;");
        command.Parameters.AddWithValue("$Id", Id(ratingId));
        return command.ExecuteScalar() is string id ? Guid.Parse(id) : null;
    }

    /// <summary>
    /// Deletes a rating (its keys cascade) and writes its tombstone.
    /// </summary>
    /// <param name="connection">The writer's connection.</param>
    /// <param name="transaction">Its transaction.</param>
    /// <param name="ratingId">The rating.</param>
    /// <param name="deletedBy">Who deleted it.</param>
    /// <param name="deletedUtc">When.</param>
    /// <returns>Whether it existed.</returns>
    public static bool Delete(SqliteConnection connection, SqliteTransaction transaction, Guid ratingId, string deletedBy, DateTime deletedUtc)
        => DeleteRow(connection, transaction, ratingId, deletedBy, deletedUtc) is not null;

    /// <summary>
    /// Deletes a rating (its keys cascade) and writes its tombstone, as <see cref="Delete"/> does.
    /// </summary>
    /// <param name="connection">The writer's connection.</param>
    /// <param name="transaction">Its transaction.</param>
    /// <param name="ratingId">The rating.</param>
    /// <param name="deletedBy">Who deleted it.</param>
    /// <param name="deletedUtc">When.</param>
    /// <param name="origin">Where (<see cref="RatingValues.DeletionOrigin"/>); null for this server.</param>
    /// <returns>The item it was on, or <c>null</c> when there was no such rating.</returns>
    public static Guid? DeleteRow(SqliteConnection connection, SqliteTransaction transaction, Guid ratingId, string deletedBy, DateTime deletedUtc, string? origin = null)
    {
        string? userId = null;
        string? itemId = null;
        using (var find = Command(connection, transaction, "SELECT UserId, ItemId FROM Ratings WHERE Id = $Id;"))
        {
            find.Parameters.AddWithValue("$Id", Id(ratingId));
            using var reader = find.ExecuteReader();
            if (reader.Read())
            {
                userId = reader.GetString(0);
                itemId = reader.GetString(1);
            }
        }

        if (userId is null)
        {
            return null;
        }

        using (var delete = Command(connection, transaction, "DELETE FROM Ratings WHERE Id = $Id;"))
        {
            delete.Parameters.AddWithValue("$Id", Id(ratingId));
            delete.ExecuteNonQuery();
        }

        using var tombstone = Command(
            connection,
            transaction,
            "INSERT OR REPLACE INTO Tombstones (RatingId, UserId, ItemId, DeletedUtc, DeletedBy, Origin) VALUES ($Id, $UserId, $ItemId, $DeletedUtc, $DeletedBy, $Origin);");
        tombstone.Parameters.AddWithValue("$Id", Id(ratingId));
        tombstone.Parameters.AddWithValue("$UserId", userId);
        tombstone.Parameters.AddWithValue("$ItemId", itemId!);
        tombstone.Parameters.AddWithValue("$DeletedUtc", Time(deletedUtc));
        tombstone.Parameters.AddWithValue("$DeletedBy", deletedBy);
        tombstone.Parameters.AddWithValue("$Origin", origin ?? RatingValues.DeletionOrigin.Local);
        tombstone.ExecuteNonQuery();
        return Guid.Parse(itemId!);
    }

    /// <summary>
    /// Writes the tombstone of a rating the live table doesn't hold (an archived one).
    /// </summary>
    /// <param name="connection">The writer's connection.</param>
    /// <param name="transaction">Its transaction.</param>
    /// <param name="ratingId">The rating.</param>
    /// <param name="userId">Its member.</param>
    /// <param name="itemId">The item it was on.</param>
    /// <param name="deletedBy">Who deleted it.</param>
    /// <param name="deletedUtc">When.</param>
    /// <param name="origin">Where (<see cref="RatingValues.DeletionOrigin"/>); null for this server.</param>
    public static void AddTombstone(SqliteConnection connection, SqliteTransaction transaction, Guid ratingId, Guid userId, Guid itemId, string deletedBy, DateTime deletedUtc, string? origin = null)
    {
        using var tombstone = Command(
            connection,
            transaction,
            "INSERT OR REPLACE INTO Tombstones (RatingId, UserId, ItemId, DeletedUtc, DeletedBy, Origin) VALUES ($Id, $UserId, $ItemId, $DeletedUtc, $DeletedBy, $Origin);");
        tombstone.Parameters.AddWithValue("$Id", Id(ratingId));
        tombstone.Parameters.AddWithValue("$UserId", Id(userId));
        tombstone.Parameters.AddWithValue("$ItemId", Id(itemId));
        tombstone.Parameters.AddWithValue("$DeletedUtc", Time(deletedUtc));
        tombstone.Parameters.AddWithValue("$DeletedBy", deletedBy);
        tombstone.Parameters.AddWithValue("$Origin", origin ?? RatingValues.DeletionOrigin.Local);
        tombstone.ExecuteNonQuery();
    }

    /// <summary>
    /// Whether a moderation log row about a user has an action.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">Its transaction, if any.</param>
    /// <param name="targetUserId">The user.</param>
    /// <param name="action">The action.</param>
    /// <returns>Whether one has.</returns>
    public static bool HasModerationAction(SqliteConnection connection, SqliteTransaction? transaction, Guid targetUserId, string action)
    {
        using var command = Command(connection, transaction, "SELECT EXISTS (SELECT 1 FROM ModerationLog WHERE TargetUserId = $UserId AND Action = $Action);");
        command.Parameters.AddWithValue("$UserId", Id(targetUserId));
        command.Parameters.AddWithValue("$Action", action);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>
    /// Whether a rating id has a tombstone.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">Its transaction, if any.</param>
    /// <param name="ratingId">The rating.</param>
    /// <returns>Whether it has one.</returns>
    public static bool HasTombstone(SqliteConnection connection, SqliteTransaction? transaction, Guid ratingId)
    {
        using var command = Command(connection, transaction, "SELECT EXISTS (SELECT 1 FROM Tombstones WHERE RatingId = $Id);");
        command.Parameters.AddWithValue("$Id", Id(ratingId));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>
    /// Removes a detached rating (its keys cascade) without a tombstone: the kit's archive holds it now.
    /// </summary>
    /// <param name="connection">The writer's connection.</param>
    /// <param name="transaction">Its transaction.</param>
    /// <param name="ratingId">The rating.</param>
    /// <returns>The item it was on, or <c>null</c> when there was no such detached rating.</returns>
    public static Guid? RemoveDetachedRow(SqliteConnection connection, SqliteTransaction transaction, Guid ratingId)
    {
        using var delete = Command(connection, transaction, "DELETE FROM Ratings WHERE Id = $Id AND DetachedUtc IS NOT NULL RETURNING ItemId;");
        delete.Parameters.AddWithValue("$Id", Id(ratingId));
        return delete.ExecuteScalar() is string itemId ? Guid.Parse(itemId) : null;
    }

    /// <summary>
    /// Appends a moderation log row.
    /// </summary>
    /// <param name="connection">The writer's connection.</param>
    /// <param name="transaction">Its transaction.</param>
    /// <param name="entry">The row.</param>
    /// <returns>Its number.</returns>
    public static long AppendModerationLog(SqliteConnection connection, SqliteTransaction transaction, ModerationLogRecord entry)
    {
        using var command = Command(
            connection,
            transaction,
            "INSERT INTO ModerationLog (AtUtc, ActorUserId, Action, RatingId, TargetUserId, ItemId, Reason, TextSnapshot, SnapshotExpiresUtc, Details) "
            + "VALUES ($AtUtc, $ActorUserId, $Action, $RatingId, $TargetUserId, $ItemId, $Reason, $TextSnapshot, $SnapshotExpiresUtc, $Details); "
            + "SELECT last_insert_rowid();");
        command.Parameters.AddWithValue("$AtUtc", Time(entry.AtUtc));
        command.Parameters.AddWithValue("$ActorUserId", Id(entry.ActorUserId));
        command.Parameters.AddWithValue("$Action", entry.Action);
        command.Parameters.AddWithValue("$RatingId", Id(entry.RatingId));
        command.Parameters.AddWithValue("$TargetUserId", Id(entry.TargetUserId));
        command.Parameters.AddWithValue("$ItemId", Id(entry.ItemId));
        command.Parameters.AddWithValue("$Reason", (object?)entry.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$TextSnapshot", (object?)entry.TextSnapshot ?? DBNull.Value);
        command.Parameters.AddWithValue("$SnapshotExpiresUtc", Time(entry.SnapshotExpiresUtc));
        command.Parameters.AddWithValue("$Details", (object?)entry.Details ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Deletes the moderation log's rows written before a time (<c>ModerationLogDays</c>): the daily
    /// maintenance's retention. The writable connection has <c>secure_delete</c>, so their bytes are overwritten.
    /// </summary>
    /// <param name="connection">The writer's connection.</param>
    /// <param name="transaction">Its transaction.</param>
    /// <param name="cutoffUtc">Rows whose <c>AtUtc</c> is before this go; one at it stays.</param>
    /// <param name="removalsCutoffUtc">A row a restore reads (<c>RemoveText</c>, <c>RemoveEntry</c>, <c>ImportEnvelope</c>,
    /// <c>StartEmpty</c>) goes only before this one too; null for <paramref name="cutoffUtc"/>.</param>
    /// <returns>How many rows went.</returns>
    public static int DeleteModerationLogBefore(SqliteConnection connection, SqliteTransaction transaction, DateTime cutoffUtc, DateTime? removalsCutoffUtc = null)
    {
        // AtUtc is always Time()'s fixed-width ISO-8601 UTC text, so the text order is the time order. A row a restore reads
        // stays while a kept snapshot is older than it: a moderator's removal (the restore removes the review again from it),
        // an envelope import's (a Replace says the log before it is the file's, and an import of this server's own export
        // that its ledger rows are this server's) and a Start empty (the restore says deletions may be back). Before
        // 0.3.22, only the removals stayed.
        var removals = removalsCutoffUtc is { } r && r < cutoffUtc ? r : cutoffUtc;
        using var command = Command(
            connection,
            transaction,
            "DELETE FROM ModerationLog WHERE AtUtc < $Cutoff AND (Action NOT IN ($RemoveText, $RemoveEntry, $ImportEnvelope, $StartEmpty) OR AtUtc < $Removals);");
        command.Parameters.AddWithValue("$Cutoff", Time(cutoffUtc));
        command.Parameters.AddWithValue("$Removals", Time(removals));
        command.Parameters.AddWithValue("$RemoveText", RatingValues.ModerationAction.RemoveText);
        command.Parameters.AddWithValue("$RemoveEntry", RatingValues.ModerationAction.RemoveEntry);
        command.Parameters.AddWithValue("$ImportEnvelope", RatingValues.ModerationAction.ImportEnvelope);
        command.Parameters.AddWithValue("$StartEmpty", RatingValues.ModerationAction.StartEmpty);
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// Records an imported source entry and replaces its rows.
    /// </summary>
    /// <param name="connection">The writer's connection.</param>
    /// <param name="transaction">Its transaction.</param>
    /// <param name="entry">The entry.</param>
    public static void RecordImport(SqliteConnection connection, SqliteTransaction transaction, ImportLedgerRecord entry)
    {
        using (var ledger = Command(
            connection,
            transaction,
            "INSERT OR REPLACE INTO ImportLedger (Source, SourceKey, SourceUpdatedUtc, ImportedUtc) VALUES ($Source, $SourceKey, $SourceUpdatedUtc, $ImportedUtc); "
            + "DELETE FROM ImportLedgerRows WHERE Source = $Source AND SourceKey = $SourceKey;"))
        {
            ledger.Parameters.AddWithValue("$Source", entry.Source);
            ledger.Parameters.AddWithValue("$SourceKey", entry.SourceKey);
            ledger.Parameters.AddWithValue("$SourceUpdatedUtc", Time(entry.SourceUpdatedUtc));
            ledger.Parameters.AddWithValue("$ImportedUtc", Time(entry.ImportedUtc));
            ledger.ExecuteNonQuery();
        }

        using var rows = Command(connection, transaction, "INSERT OR IGNORE INTO ImportLedgerRows (Source, SourceKey, RatingId) VALUES ($Source, $SourceKey, $RatingId);");
        rows.Parameters.AddWithValue("$Source", entry.Source);
        rows.Parameters.AddWithValue("$SourceKey", entry.SourceKey);
        var rating = rows.Parameters.Add("$RatingId", SqliteType.Text);
        foreach (var id in entry.RatingIds.Distinct())
        {
            rating.Value = Id(id);
            rows.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Sets a meta value.
    /// </summary>
    /// <param name="connection">The writer's connection.</param>
    /// <param name="transaction">Its transaction.</param>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    public static void SetMeta(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        using var command = Command(connection, transaction, "INSERT INTO Meta (Key, Value) VALUES ($Key, $Value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;");
        command.Parameters.AddWithValue("$Key", key);
        command.Parameters.AddWithValue("$Value", value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds a former member (the <c>Anonymize</c> policy).
    /// </summary>
    /// <param name="connection">The writer's connection.</param>
    /// <param name="transaction">Its transaction.</param>
    /// <param name="id">The id the user's rows carry from now on.</param>
    /// <param name="originalUserId">The deleted user's id.</param>
    /// <param name="displayName">The name shown ("Former member", "Former member 2", …).</param>
    /// <param name="deletedUtc">When the user was deleted.</param>
    public static void AddFormerUser(SqliteConnection connection, SqliteTransaction transaction, Guid id, Guid originalUserId, string displayName, DateTime deletedUtc)
    {
        using var command = Command(connection, transaction, "INSERT INTO FormerUsers (Id, OriginalUserId, DisplayName, DeletedUtc) VALUES ($Id, $OriginalUserId, $DisplayName, $DeletedUtc);");
        command.Parameters.AddWithValue("$Id", Id(id));
        command.Parameters.AddWithValue("$OriginalUserId", Id(originalUserId));
        command.Parameters.AddWithValue("$DisplayName", displayName);
        command.Parameters.AddWithValue("$DeletedUtc", Time(deletedUtc));
        command.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------------------------------------ reads

    /// <summary>Reads ratings by a condition.</summary>
    /// <param name="connection">A reader's connection.</param>
    /// <param name="transaction">The transaction the read is part of (a writer's), or <c>null</c>.</param>
    /// <param name="warnings">Where a stored value read tolerantly is reported.</param>
    /// <param name="where">The condition (and order), with <c>$</c> parameters.</param>
    /// <param name="parameters">The parameters' values.</param>
    /// <returns>The ratings.</returns>
    public static List<RatingRecord> Ratings(SqliteConnection connection, SqliteTransaction? transaction, RatingsRowWarnings? warnings, string where, params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, "SELECT " + Columns + " FROM Ratings " + where);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = new List<RatingRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadRating(reader, warnings));
        }

        return result;
    }

    /// <summary>Reads the summary rows of items, in chunks.</summary>
    /// <param name="connection">A reader's connection.</param>
    /// <param name="itemIds">The items.</param>
    /// <returns>The rows.</returns>
    public static List<SummaryRow> SummaryRows(SqliteConnection connection, IReadOnlyCollection<Guid> itemIds)
    {
        var result = new List<SummaryRow>();
        foreach (var chunk in itemIds.Distinct().Chunk(InChunk))
        {
            using var command = Command(connection, null, "SELECT ItemId, UserId, Stars2, (ReviewText IS NOT NULL) FROM Ratings WHERE DetachedUtc IS NULL AND ItemId IN (" + Placeholders(chunk.Length) + ");");
            AddList(command, chunk.Select(Id));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new SummaryRow(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetInt64(3) != 0));
            }
        }

        return result;
    }

    /// <summary>Reads a rating's keys.</summary>
    /// <param name="connection">A reader's connection.</param>
    /// <param name="ratingId">The rating.</param>
    /// <returns>The keys.</returns>
    public static List<string> Keys(SqliteConnection connection, Guid ratingId)
    {
        using var command = Command(connection, null, "SELECT Key FROM RatingKeys WHERE RatingId = $Id ORDER BY Key;");
        command.Parameters.AddWithValue("$Id", Id(ratingId));
        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>Reads the ratings sharing any of the keys.</summary>
    /// <param name="connection">A reader's connection.</param>
    /// <param name="warnings">Where a stored value read tolerantly is reported.</param>
    /// <param name="keys">The keys.</param>
    /// <param name="detached">Detached (true) or attached (false) ratings.</param>
    /// <returns>The ratings, each once.</returns>
    public static List<RatingRecord> ByKeys(SqliteConnection connection, RatingsRowWarnings? warnings, IReadOnlyCollection<string> keys, bool detached)
    {
        var seen = new HashSet<Guid>();
        var result = new List<RatingRecord>();
        foreach (var chunk in keys.Distinct(StringComparer.Ordinal).Chunk(InChunk))
        {
            using var command = Command(
                connection,
                null,
                "SELECT " + Columns + " FROM Ratings WHERE Id IN (SELECT RatingId FROM RatingKeys WHERE Key IN (" + Placeholders(chunk.Length) + ")) AND "
                + (detached ? "DetachedUtc IS NOT NULL" : "DetachedUtc IS NULL") + " ORDER BY Id;");
            AddList(command, chunk);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var record = ReadRating(reader, warnings);
                if (seen.Add(record.Id))
                {
                    result.Add(record);
                }
            }
        }

        return result;
    }

    /// <summary>Reads the ids of the items whose attached ratings share any of the keys: ids only, never a row's text.</summary>
    /// <param name="connection">A reader's connection.</param>
    /// <param name="keys">The keys.</param>
    /// <returns>The item ids, each once, in id order.</returns>
    public static List<Guid> AttachedItemIdsByKeys(SqliteConnection connection, IReadOnlyCollection<string> keys)
    {
        var result = new SortedSet<Guid>();
        foreach (var chunk in keys.Distinct(StringComparer.Ordinal).Chunk(InChunk))
        {
            using var command = Command(
                connection,
                null,
                "SELECT DISTINCT ItemId FROM Ratings WHERE Id IN (SELECT RatingId FROM RatingKeys WHERE Key IN (" + Placeholders(chunk.Length) + ")) AND DetachedUtc IS NULL;");
            AddList(command, chunk);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        return [.. result];
    }

    /// <summary>
    /// Reads the ratings on items or inside them: the rows whose <c>ItemId</c>, <c>SeriesId</c>,
    /// <c>SeasonId</c> or <c>AlbumId</c> is one of the ids (the indexes <c>IX_Ratings_Item</c>, <c>_Series</c>,
    /// <c>_Season</c>, <c>_Album</c>), so a removed series or album names the rows of its episodes or tracks too.
    /// </summary>
    /// <param name="connection">A connection.</param>
    /// <param name="transaction">The unit of work's transaction, or null for a reader's.</param>
    /// <param name="warnings">Where a stored value read tolerantly is reported.</param>
    /// <param name="itemIds">The item ids.</param>
    /// <param name="includeDetached">Whether detached rows count too (the move journal moves them).</param>
    /// <returns>The ratings, each once, in id order per chunk.</returns>
    public static List<RatingRecord> On(SqliteConnection connection, SqliteTransaction? transaction, RatingsRowWarnings? warnings, IReadOnlyCollection<Guid> itemIds, bool includeDetached)
    {
        var seen = new HashSet<Guid>();
        var result = new List<RatingRecord>();
        foreach (var chunk in itemIds.Where(id => id != Guid.Empty).Distinct().Select(Id).Chunk(InChunk))
        {
            var list = Placeholders(chunk.Length);
            using var command = Command(
                connection,
                transaction,
                "SELECT " + Columns + " FROM Ratings WHERE (ItemId IN (" + list + ") OR SeriesId IN (" + list + ") OR SeasonId IN (" + list
                + ") OR AlbumId IN (" + list + "))" + (includeDetached ? string.Empty : " AND DetachedUtc IS NULL") + " ORDER BY Id;");
            AddList(command, chunk);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var record = ReadRating(reader, warnings);
                if (seen.Add(record.Id))
                {
                    result.Add(record);
                }
            }
        }

        return result;
    }

    /// <summary>Reads the ids of the items with attached ratings, each once (the verify sweep's list).</summary>
    /// <param name="connection">A reader's connection.</param>
    /// <returns>The item ids, in id order.</returns>
    public static List<Guid> AttachedItemIds(SqliteConnection connection)
    {
        using var command = Command(connection, null, "SELECT DISTINCT ItemId FROM Ratings WHERE DetachedUtc IS NULL ORDER BY ItemId;");
        var result = new List<Guid>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(Guid.Parse(reader.GetString(0)));
        }

        return result;
    }

    /// <summary>Reads a tombstone.</summary>
    /// <param name="connection">A reader's connection.</param>
    /// <param name="ratingId">The rating's id.</param>
    /// <returns>The tombstone, or null.</returns>
    public static TombstoneRecord? Tombstone(SqliteConnection connection, Guid ratingId) => Tombstone(connection, null, ratingId);

    /// <summary>Reads a user's tombstones on an item, oldest first (the JE import's guard).</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">Its transaction, if any.</param>
    /// <param name="userId">The user.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>The tombstones.</returns>
    public static IReadOnlyList<TombstoneRecord> TombstonesOf(SqliteConnection connection, SqliteTransaction? transaction, Guid userId, Guid itemId)
    {
        using var command = Command(connection, transaction, "SELECT RatingId, UserId, ItemId, DeletedUtc, DeletedBy, Origin FROM Tombstones WHERE UserId = $User AND ItemId = $Item ORDER BY DeletedUtc, RatingId;");
        command.Parameters.AddWithValue("$User", Id(userId));
        command.Parameters.AddWithValue("$Item", Id(itemId));
        using var reader = command.ExecuteReader();
        var found = new List<TombstoneRecord>();
        while (reader.Read())
        {
            found.Add(new TombstoneRecord(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), ParseTime(reader.GetString(3)), reader.GetString(4)) { Origin = reader.GetString(5) });
        }

        return found;
    }

    /// <summary>Reads a tombstone, inside a transaction (the writer's).</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">Its transaction, if any.</param>
    /// <param name="ratingId">The rating's id.</param>
    /// <returns>The tombstone, or null.</returns>
    public static TombstoneRecord? Tombstone(SqliteConnection connection, SqliteTransaction? transaction, Guid ratingId)
    {
        using var command = Command(connection, transaction, "SELECT RatingId, UserId, ItemId, DeletedUtc, DeletedBy, Origin FROM Tombstones WHERE RatingId = $Id;");
        command.Parameters.AddWithValue("$Id", Id(ratingId));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new TombstoneRecord(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), ParseTime(reader.GetString(3)), reader.GetString(4)) { Origin = reader.GetString(5) }
            : null;
    }

    /// <summary>Reads every moderation log row's id, time, action and rating, oldest first (a restore's comparison).</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">Its transaction, if any.</param>
    /// <returns>The rows, with only those four fields.</returns>
    public static List<ModerationLogRecord> ModerationLogKeys(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var rows = new List<ModerationLogRecord>();
        using var command = Command(connection, transaction, "SELECT Id, AtUtc, Action, RatingId FROM ModerationLog ORDER BY Id;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ModerationLogRecord { Id = reader.GetInt64(0), AtUtc = ParseTime(reader.GetString(1)), Action = reader.GetString(2), RatingId = ParseId(reader, 3) });
        }

        return rows;
    }

    /// <summary>Reads a page of the moderation log, newest first.</summary>
    /// <param name="connection">A reader's connection.</param>
    /// <param name="startIndex">Rows to skip.</param>
    /// <param name="limit">Rows to return.</param>
    /// <param name="action">Only rows of this action, or null for all.</param>
    /// <returns>The rows.</returns>
    public static List<ModerationLogRecord> ModerationLog(SqliteConnection connection, int startIndex, int limit, string? action = null)
    {
        using var command = Command(
            connection,
            null,
            "SELECT Id, AtUtc, ActorUserId, Action, RatingId, TargetUserId, ItemId, Reason, TextSnapshot, SnapshotExpiresUtc, Details FROM ModerationLog "
            + (action is null ? string.Empty : "WHERE Action = $Action ")
            + "ORDER BY Id DESC LIMIT $Limit OFFSET $Offset;");
        command.Parameters.AddWithValue("$Limit", limit);
        command.Parameters.AddWithValue("$Offset", startIndex);
        if (action is not null)
        {
            command.Parameters.AddWithValue("$Action", action);
        }

        var result = new List<ModerationLogRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ModerationLogRecord
            {
                Id = reader.GetInt64(0),
                AtUtc = ParseTime(reader.GetString(1)),
                ActorUserId = ParseId(reader, 2),
                Action = reader.GetString(3),
                RatingId = ParseId(reader, 4),
                TargetUserId = ParseId(reader, 5),
                ItemId = ParseId(reader, 6),
                Reason = reader.IsDBNull(7) ? null : reader.GetString(7),
                TextSnapshot = reader.IsDBNull(8) ? null : reader.GetString(8),
                SnapshotExpiresUtc = reader.IsDBNull(9) ? null : ParseTime(reader.GetString(9)),
                Details = reader.IsDBNull(10) ? null : reader.GetString(10)
            });
        }

        return result;
    }

    /// <summary>Counts the moderation log's rows.</summary>
    /// <param name="connection">A reader's connection.</param>
    /// <param name="action">Only rows of this action, or null for all.</param>
    /// <returns>The count.</returns>
    public static int CountModerationLog(SqliteConnection connection, string? action)
    {
        using var command = Command(connection, null, "SELECT COUNT(*) FROM ModerationLog" + (action is null ? ";" : " WHERE Action = $Action;"));
        if (action is not null)
        {
            command.Parameters.AddWithValue("$Action", action);
        }

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Counts the moderation log's rows written before a time (the Settings tab's dry run of
    /// <c>ModerationLogDays</c>).</summary>
    /// <param name="connection">A reader's connection.</param>
    /// <param name="cutoffUtc">The time.</param>
    /// <returns>The count.</returns>
    public static int CountModerationLogBefore(SqliteConnection connection, DateTime cutoffUtc)
    {
        using var command = Command(connection, null, "SELECT COUNT(*) FROM ModerationLog WHERE AtUtc < $Cutoff;");
        command.Parameters.AddWithValue("$Cutoff", Time(cutoffUtc));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Counts the attached ratings and reviews by their item's kind.</summary>
    /// <param name="connection">A reader's connection.</param>
    /// <returns>The ratings and reviews of each stored kind name.</returns>
    public static IReadOnlyDictionary<string, (int Ratings, int Reviews)> CountAttachedByKind(SqliteConnection connection)
    {
        using var command = Command(connection, null, "SELECT ItemKind, COUNT(*), COUNT(ReviewText) FROM Ratings WHERE DetachedUtc IS NULL GROUP BY ItemKind;");
        var result = new Dictionary<string, (int Ratings, int Reviews)>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt32(2));
        }

        return result;
    }

    /// <summary>Counts the attached reviews longer than a limit in UTF-16 code units, as <c>ReviewText.Validate</c> measures
    /// them.</summary>
    /// <param name="connection">A reader's connection.</param>
    /// <param name="length">The limit.</param>
    /// <returns>The count.</returns>
    public static int CountReviewsLongerThan(SqliteConnection connection, int length)
    {
        // SQLite's length() counts code points, and a text's UTF-16 length is from that to twice that: only the texts of
        // more than half the limit are read, and measured here.
        using var command = Command(connection, null, "SELECT ReviewText FROM Ratings WHERE DetachedUtc IS NULL AND ReviewText IS NOT NULL AND length(ReviewText) * 2 > $Length;");
        command.Parameters.AddWithValue("$Length", length);
        var count = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(0).Length > length)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Reads the rows of the Moderation list by the filters the database applies, the row changed last first.</summary>
    /// <param name="connection">A reader's connection.</param>
    /// <param name="warnings">Where a stored value read tolerantly is reported.</param>
    /// <param name="userId">Only this member's rows, or null.</param>
    /// <param name="itemId">Only the rows on this item, or null.</param>
    /// <param name="hasText">Only rows with (true) or without (false) a review, or null.</param>
    /// <param name="detached">Only detached (true) or attached (false) rows, or null.</param>
    /// <param name="sinceUtc">Only rows changed at or after this time, or null.</param>
    /// <param name="startIndex">The page's first row (with a limit).</param>
    /// <param name="limit">The page's size, or null for every row.</param>
    /// <returns>The rows and how many the filters match.</returns>
    public static (List<RatingRecord> Rows, int Total) ForModeration(SqliteConnection connection, RatingsRowWarnings? warnings, Guid? userId, Guid? itemId, bool? hasText, bool? detached, DateTime? sinceUtc, int startIndex, int? limit)
    {
        var (clause, parameters) = ModerationWhere(userId, itemId, hasText, detached, sinceUtc);
        if (limit is not { } take)
        {
            var all = Ratings(connection, null, warnings, clause + "ORDER BY UpdatedUtc DESC, Id;", parameters);
            return (all, all.Count);
        }

        var page = Ratings(
            connection,
            null,
            warnings,
            clause + string.Create(CultureInfo.InvariantCulture, $"ORDER BY UpdatedUtc DESC, Id LIMIT {take} OFFSET {startIndex};"),
            parameters);
        using var count = Command(connection, null, "SELECT COUNT(*) FROM Ratings " + clause + ";");
        foreach (var (name, value) in parameters)
        {
            count.Parameters.AddWithValue(name, value);
        }

        return (page, Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    // The Moderation list's filters the database applies (UpdatedUtc is stored in the round-trip format, so text order is
    // time order).
    private static (string Clause, (string Name, object Value)[] Parameters) ModerationWhere(Guid? userId, Guid? itemId, bool? hasText, bool? detached, DateTime? sinceUtc)
    {
        var where = new List<string>();
        var parameters = new List<(string Name, object Value)>();
        if (userId is { } user)
        {
            where.Add("UserId = $UserId");
            parameters.Add(("$UserId", Id(user)));
        }

        if (itemId is { } item)
        {
            where.Add("ItemId = $ItemId");
            parameters.Add(("$ItemId", Id(item)));
        }

        if (hasText is { } text)
        {
            where.Add(text ? "ReviewText IS NOT NULL" : "ReviewText IS NULL");
        }

        if (detached is { } isDetached)
        {
            where.Add(isDetached ? "DetachedUtc IS NOT NULL" : "DetachedUtc IS NULL");
        }

        if (sinceUtc is { } since)
        {
            where.Add("UpdatedUtc >= $Since");
            parameters.Add(("$Since", Time(since)));
        }

        var clause = where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where) + " ";
        return (clause, [.. parameters]);
    }

    /// <summary>Reads an imported source entry with its rows.</summary>
    /// <param name="connection">A reader's connection.</param>
    /// <param name="source">The source.</param>
    /// <param name="sourceKey">The entry's key.</param>
    /// <param name="transaction">The unit of work's transaction, or null for a reader's (the import reads it in its write).</param>
    /// <returns>The entry, or null.</returns>
    public static ImportLedgerRecord? Import(SqliteConnection connection, string source, string sourceKey, SqliteTransaction? transaction = null)
    {
        DateTime updated;
        DateTime imported;
        using (var command = Command(connection, transaction, "SELECT SourceUpdatedUtc, ImportedUtc FROM ImportLedger WHERE Source = $Source AND SourceKey = $SourceKey;"))
        {
            command.Parameters.AddWithValue("$Source", source);
            command.Parameters.AddWithValue("$SourceKey", sourceKey);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            updated = ParseTime(reader.GetString(0));
            imported = ParseTime(reader.GetString(1));
        }

        using var rows = Command(connection, transaction, "SELECT RatingId FROM ImportLedgerRows WHERE Source = $Source AND SourceKey = $SourceKey ORDER BY RatingId;");
        rows.Parameters.AddWithValue("$Source", source);
        rows.Parameters.AddWithValue("$SourceKey", sourceKey);
        var ids = new List<Guid>();
        using (var reader = rows.ExecuteReader())
        {
            while (reader.Read())
            {
                ids.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        return new ImportLedgerRecord(source, sourceKey, updated, imported, ids);
    }

    /// <summary>Reads a meta value.</summary>
    /// <param name="connection">A connection.</param>
    /// <param name="key">The key.</param>
    /// <param name="transaction">The unit of work's transaction, or null for a reader's.</param>
    /// <returns>The value, or null.</returns>
    public static string? Meta(SqliteConnection connection, string key, SqliteTransaction? transaction = null)
    {
        using var command = Command(connection, transaction, "SELECT Value FROM Meta WHERE Key = $Key;");
        command.Parameters.AddWithValue("$Key", key);
        return command.ExecuteScalar() as string;
    }

    /// <summary>Reads the former members (the <c>Anonymize</c> policy): the ids their rows carry now, with the names shown.</summary>
    /// <param name="connection">A connection.</param>
    /// <param name="transaction">The unit of work's transaction, or null for a reader's.</param>
    /// <returns>Id → display name.</returns>
    public static Dictionary<Guid, string> FormerUsers(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = Command(connection, transaction, "SELECT Id, DisplayName FROM FormerUsers;");
        var result = new Dictionary<Guid, string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[Guid.Parse(reader.GetString(0))] = reader.GetString(1);
        }

        return result;
    }

    /// <summary>
    /// Reads every table of a snapshot file (a restore's safety snapshot), on a connection of its own.
    /// </summary>
    /// <param name="path">The snapshot's path.</param>
    /// <param name="warnings">Where a stored value read tolerantly is reported.</param>
    /// <returns>The tables.</returns>
    public static RatingsTables SnapshotTables(string path, RatingsRowWarnings? warnings)
    {
        // Read only, unpooled: the file is closed when this returns, so the snapshot's rotation can delete it.
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return Tables(connection, warnings);
    }

    /// <summary>
    /// Reads every table an export holds in one read transaction, so the tables agree with each other.
    /// </summary>
    /// <param name="connection">A reader's connection.</param>
    /// <param name="warnings">Where a stored value read tolerantly is reported.</param>
    /// <returns>The tables.</returns>
    public static RatingsTables Tables(SqliteConnection connection, RatingsRowWarnings? warnings)
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        var ratings = Ratings(connection, transaction, warnings, "ORDER BY Id;");
        var former = new List<FormerUserRecord>();
        using (var command = Command(connection, transaction, "SELECT Id, OriginalUserId, DisplayName, DeletedUtc, Origin FROM FormerUsers ORDER BY Id;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                former.Add(new FormerUserRecord(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), ParseTime(reader.GetString(3))) { Origin = reader.GetString(4) });
            }
        }

        var log = new List<ModerationLogRecord>();
        using (var command = Command(connection, transaction, "SELECT Id, AtUtc, ActorUserId, Action, RatingId, TargetUserId, ItemId, Reason, Details FROM ModerationLog ORDER BY Id;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                log.Add(new ModerationLogRecord
                {
                    Id = reader.GetInt64(0),
                    AtUtc = ParseTime(reader.GetString(1)),
                    ActorUserId = ParseId(reader, 2),
                    Action = reader.GetString(3),
                    RatingId = ParseId(reader, 4),
                    TargetUserId = ParseId(reader, 5),
                    ItemId = ParseId(reader, 6),
                    Reason = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Details = reader.IsDBNull(8) ? null : reader.GetString(8)
                });
            }
        }

        var rowsByKey = new Dictionary<(string Source, string Key), List<Guid>>();
        using (var command = Command(connection, transaction, "SELECT Source, SourceKey, RatingId FROM ImportLedgerRows ORDER BY Source, SourceKey, RatingId;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!rowsByKey.TryGetValue(key, out var ids))
                {
                    rowsByKey[key] = ids = [];
                }

                ids.Add(Guid.Parse(reader.GetString(2)));
            }
        }

        var ledger = new List<ImportLedgerRecord>();
        using (var command = Command(connection, transaction, "SELECT Source, SourceKey, SourceUpdatedUtc, ImportedUtc FROM ImportLedger ORDER BY Source, SourceKey;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var key = (reader.GetString(0), reader.GetString(1));
                ledger.Add(new ImportLedgerRecord(key.Item1, key.Item2, ParseTime(reader.GetString(2)), ParseTime(reader.GetString(3)), rowsByKey.GetValueOrDefault(key) ?? []));
            }
        }

        var tombstones = new List<TombstoneRecord>();
        using (var command = Command(connection, transaction, "SELECT RatingId, UserId, ItemId, DeletedUtc, DeletedBy, Origin FROM Tombstones ORDER BY RatingId;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                tombstones.Add(new TombstoneRecord(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), ParseTime(reader.GetString(3)), reader.GetString(4)) { Origin = reader.GetString(5) });
            }
        }

        return new RatingsTables(ratings, former, log, ledger, tombstones);
    }

    /// <summary>
    /// Empties the tables an envelope Replace replaces: every rating and its keys, the moderation log and
    /// the import ledger. The tombstones and the former members stay (a deleted rating never comes back through an import),
    /// and so does Meta, and so do the envelope ledger's rows that point at a deleted rating (they
    /// are how the next import of the same file finds that the rating was deleted here). <c>secure_delete</c> zeroes
    /// what goes.
    /// </summary>
    /// <param name="connection">The writer's connection.</param>
    /// <param name="transaction">Its transaction.</param>
    /// <returns>The ratings deleted and the items they were on.</returns>
    public static (int Ratings, List<Guid> ItemIds) WipeForReplace(SqliteConnection connection, SqliteTransaction transaction)
    {
        var items = new List<Guid>();
        using (var command = Command(connection, transaction, "SELECT DISTINCT ItemId FROM Ratings;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                items.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        using (var keys = Command(connection, transaction, "DELETE FROM RatingKeys;"))
        {
            keys.ExecuteNonQuery();
        }

        int ratings;
        using (var command = Command(connection, transaction, "DELETE FROM Ratings;"))
        {
            ratings = command.ExecuteNonQuery();
        }

        using (var command = Command(
            connection,
            transaction,
            "DELETE FROM ModerationLog; "
            + "DELETE FROM ImportLedgerRows WHERE RatingId NOT IN (SELECT RatingId FROM Tombstones); "
            + "DELETE FROM ImportLedger WHERE NOT EXISTS (SELECT 1 FROM ImportLedgerRows r WHERE r.Source = ImportLedger.Source AND r.SourceKey = ImportLedger.SourceKey);"))
        {
            command.ExecuteNonQuery();
        }

        return (ratings, items);
    }

    /// <summary>Adds a former member's row unless one with its id is there (the envelope import).</summary>
    /// <param name="connection">The writer's connection.</param>
    /// <param name="transaction">Its transaction.</param>
    /// <param name="row">The row.</param>
    /// <returns>Whether it was added.</returns>
    public static bool AddFormerUserIfAbsent(SqliteConnection connection, SqliteTransaction transaction, FormerUserRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        using var command = Command(connection, transaction, "INSERT OR IGNORE INTO FormerUsers (Id, OriginalUserId, DisplayName, DeletedUtc, Origin) VALUES ($Id, $OriginalUserId, $DisplayName, $DeletedUtc, $Origin);");
        command.Parameters.AddWithValue("$Id", Id(row.Id));
        command.Parameters.AddWithValue("$OriginalUserId", Id(row.OriginalUserId));
        command.Parameters.AddWithValue("$DisplayName", row.DisplayName);
        command.Parameters.AddWithValue("$DeletedUtc", Time(row.DeletedUtc));
        command.Parameters.AddWithValue("$Origin", string.IsNullOrWhiteSpace(row.Origin) ? RatingValues.DeletionOrigin.Local : row.Origin);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Counts the ratings of every user id, attached and detached ones, leaving out the former members' (the
    /// reconciliation excludes <c>FormerUsers</c>). One pass over <c>IX_Ratings_User</c>.
    /// </summary>
    /// <param name="connection">A reader's connection.</param>
    /// <returns>User id → ratings.</returns>
    public static Dictionary<Guid, int> CountByUser(SqliteConnection connection)
    {
        using var command = Command(connection, null, "SELECT UserId, COUNT(*) FROM Ratings WHERE UserId NOT IN (SELECT Id FROM FormerUsers) GROUP BY UserId;");
        var result = new Dictionary<Guid, int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (Guid.TryParse(reader.GetString(0), out var id) && id != Guid.Empty)
            {
                result[id] = reader.GetInt32(1);
            }
        }

        return result;
    }

    /// <summary>Counts the stored rows.</summary>
    /// <param name="connection">A reader's connection.</param>
    /// <returns>The counts.</returns>
    public static RatingsStoreCounts Count(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        long Scalar(string sql)
        {
            using var command = Command(connection, transaction, sql);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        return new RatingsStoreCounts(
            Scalar("SELECT COUNT(*) FROM Ratings WHERE DetachedUtc IS NULL;"),
            Scalar("SELECT COUNT(*) FROM Ratings WHERE DetachedUtc IS NOT NULL;"),
            Scalar("SELECT COUNT(*) FROM Ratings WHERE DetachedUtc IS NULL AND ReviewText IS NOT NULL;"),
            Scalar("SELECT COUNT(DISTINCT UserId) FROM Ratings WHERE DetachedUtc IS NULL;"),
            Scalar("SELECT COUNT(*) FROM Tombstones;"),
            Scalar("SELECT COUNT(*) FROM ModerationLog;"),
            Scalar("SELECT COUNT(*) FROM ImportLedger;"),
            DetachedJeWithKeySuffix(connection, transaction));
    }

    /// <summary>
    /// Counts the detached Jellyfin Enhanced ratings whose stored TMDb id still carries a season or episode suffix
    /// (<c>1399:s1</c>, <c>1399:s1:e2</c>): 0.3.21 kept JE 12.14's season and episode reviews detached with JE's whole TMDb
    /// key as the series' id, so they never attached; since 0.3.22 the id is the series' alone. 0 is what a database
    /// never imported by 0.3.21 holds.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The read's transaction.</param>
    /// <returns>The count.</returns>
    private static long DetachedJeWithKeySuffix(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction, "SELECT ProviderIds, SeriesProviderIds FROM Ratings WHERE DetachedUtc IS NOT NULL AND Origin = $Origin;");
        command.Parameters.AddWithValue("$Origin", RatingValues.Origin.JellyfinEnhanced);
        using var reader = command.ExecuteReader();
        long count = 0;
        while (reader.Read())
        {
            if (HasKeySuffix(reader, 0) || HasKeySuffix(reader, 1))
            {
                count++;
            }
        }

        return count;
    }

    private static bool HasKeySuffix(SqliteDataReader reader, int ordinal)
        => !reader.IsDBNull(ordinal)
            && ProviderIdsJson.Read(reader.GetString(ordinal), out _)
                .Any(p => string.Equals(p.Key, "Tmdb", StringComparison.OrdinalIgnoreCase) && JeKeySuffix().IsMatch(p.Value));

    // JE's TMDb key's season and episode parts at the end of a stored id (JellyfinEnhancedImport.ParseTmdbKey's, any case).
    [GeneratedRegex(@":s[0-9]+(:e[0-9]+)?\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex JeKeySuffix();

    // ------------------------------------------------------------------------------------------------ values

    /// <summary>Formats an id as the database stores it.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The <c>"N"</c> text.</returns>
    public static string Id(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Formats a time as the database stores it.</summary>
    /// <param name="utc">The time; a time of unspecified kind counts as UTC.</param>
    /// <returns>The ISO-8601 round-trip UTC text.</returns>
    public static string Time(DateTime utc)
        => (utc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc.ToUniversalTime()).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Parses a stored time.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The UTC time.</returns>
    public static DateTime ParseTime(string text)
    {
        var time = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return time.Kind switch
        {
            DateTimeKind.Utc => time,
            DateTimeKind.Local => time.ToUniversalTime(),
            _ => DateTime.SpecifyKind(time, DateTimeKind.Utc)
        };
    }

    private static object Id(Guid? id) => id is { } value ? Id(value) : DBNull.Value;

    private static object Time(DateTime? utc) => utc is { } value ? Time(value) : DBNull.Value;

    private static RatingRecord SaveForMember(SqliteConnection connection, SqliteTransaction transaction, string sql, Guid userId, IdentitySnapshot identity, int? stars2, string? text, bool isSpoiler, DateTime utc, RatingsRowWarnings? warnings)
    {
        var newId = Guid.NewGuid();
        RatingRecord row;
        try
        {
            using var command = Command(connection, transaction, sql);
            command.Parameters.AddWithValue("$Id", Id(newId));
            command.Parameters.AddWithValue("$UserId", Id(userId));
            command.Parameters.AddWithValue("$ItemId", Id(identity.ItemId));
            command.Parameters.AddWithValue("$Stars2", (object?)stars2 ?? DBNull.Value);
            command.Parameters.AddWithValue("$ReviewText", (object?)text ?? DBNull.Value);
            command.Parameters.AddWithValue("$IsSpoiler", isSpoiler && text is not null ? 1 : 0);
            command.Parameters.AddWithValue("$Now", Time(utc));
            command.Parameters.AddWithValue("$ReviewUpdatedUtc", text is null ? DBNull.Value : Time(utc));
            AddIdentity(command, identity);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("The database returned no row for a member's write.");
            }

            row = ReadRating(reader, warnings);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint)
        {
            throw Refused(newId, ex);
        }

        WriteKeys(connection, transaction, row.Id, identity);
        return row;
    }

    private static RatingRecord? UpdateReturning(SqliteConnection connection, SqliteTransaction transaction, string sql, Guid ratingId, DateTime utc, RatingsRowWarnings? warnings)
    {
        try
        {
            using var command = Command(connection, transaction, sql);
            command.Parameters.AddWithValue("$Id", Id(ratingId));
            command.Parameters.AddWithValue("$Now", Time(utc));
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadRating(reader, warnings) : null;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint)
        {
            throw Refused(ratingId, ex);
        }
    }

    // The identity snapshot's columns; the kit snapshot's RunTimeTicks and Extra have none.
    private static void AddIdentity(SqliteCommand command, IdentitySnapshot identity)
    {
        command.Parameters.AddWithValue("$ItemKind", identity.Kind);
        command.Parameters.AddWithValue("$ItemName", string.IsNullOrEmpty(identity.Name) ? DBNull.Value : identity.Name);
        command.Parameters.AddWithValue("$ProductionYear", (object?)identity.Year ?? DBNull.Value);
        command.Parameters.AddWithValue("$ProviderIds", (object?)ProviderIdsJson.Write(identity.ProviderIds) ?? DBNull.Value);
        command.Parameters.AddWithValue("$SeriesProviderIds", (object?)ProviderIdsJson.Write(identity.SeriesProviderIds) ?? DBNull.Value);
        command.Parameters.AddWithValue("$SeriesName", (object?)identity.SeriesName ?? DBNull.Value);
        command.Parameters.AddWithValue("$SeasonNumber", (object?)identity.Season ?? DBNull.Value);
        command.Parameters.AddWithValue("$EpisodeNumber", (object?)identity.Episode ?? DBNull.Value);
        command.Parameters.AddWithValue("$ItemPath", (object?)identity.Path ?? DBNull.Value);
        command.Parameters.AddWithValue("$SeriesId", Id(identity.SeriesId));
        command.Parameters.AddWithValue("$SeasonId", Id(identity.SeasonId));
        command.Parameters.AddWithValue("$AlbumId", Id(identity.AlbumId));
    }

    // A rating's keys, replaced: the kit's ItemIdentity.StrongKeys of its identity (RatingKeys).
    private static void WriteKeys(SqliteConnection connection, SqliteTransaction transaction, Guid ratingId, IdentitySnapshot identity)
    {
        var id = Id(ratingId);
        using (var delete = Command(connection, transaction, "DELETE FROM RatingKeys WHERE RatingId = $Id;"))
        {
            delete.Parameters.AddWithValue("$Id", id);
            delete.ExecuteNonQuery();
        }

        using var insert = Command(connection, transaction, "INSERT OR IGNORE INTO RatingKeys (RatingId, Key) VALUES ($Id, $Key);");
        var key = insert.Parameters.Add("$Key", SqliteType.Text);
        insert.Parameters.AddWithValue("$Id", id);
        foreach (var value in ItemIdentity.StrongKeys(identity))
        {
            key.Value = value;
            insert.ExecuteNonQuery();
        }
    }

    // UX_Ratings_User_Item, the one constraint a member's write can meet (a second attached rating of the same item): SQLite
    // names a unique index on plain columns by its columns.
    private static bool IsUserItemConflict(SqliteException ex)
        => ex.SqliteExtendedErrorCode == SqliteConstraintUnique && ex.Message.Contains(UserItemColumns, StringComparison.Ordinal);

    // Any other constraint: the store's validation and the schema disagree. A fault (500), never the member's 409; the
    // message names the constraint and the row's id, never a value.
    private static InvalidOperationException Refused(Guid ratingId, SqliteException ex)
        => new(string.Create(CultureInfo.InvariantCulture, $"The database refused the rating {ratingId:N} although it passed the store's validation: {ex.Message} (SQLite error {ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode})."), ex);

    // A provider-id map, read tolerantly (ProviderIdsJson), one Warning per row and column.
    private static Dictionary<string, string>? ReadIds(SqliteDataReader reader, int column, Guid ratingId, string name, RatingsRowWarnings? warnings)
    {
        if (reader.IsDBNull(column))
        {
            return null;
        }

        var ids = ProviderIdsJson.Read(reader.GetString(column), out var problem);
        if (problem is not null)
        {
            warnings?.ProviderIds(ratingId, name, problem, ids.Count);
        }

        return ids;
    }

    private static Guid? ParseId(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : Guid.Parse(reader.GetString(column));

    private static int? Int(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetInt32(column);

    private static string? Text(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);

    private static RatingRecord ReadRating(SqliteDataReader reader, RatingsRowWarnings? warnings)
    {
        var id = Guid.Parse(reader.GetString(0));
        return new()
        {
            Id = id,
            UserId = Guid.Parse(reader.GetString(1)),
            Stars2 = Int(reader, 3),
            ReviewText = Text(reader, 4),
            IsSpoiler = reader.GetInt64(5) != 0,
            CreatedUtc = ParseTime(reader.GetString(6)),
            UpdatedUtc = ParseTime(reader.GetString(7)),
            ReviewUpdatedUtc = reader.IsDBNull(8) ? null : ParseTime(reader.GetString(8)),
            EditCount = reader.GetInt32(9),
            Origin = reader.GetString(10),
            Identity = new IdentitySnapshot
            {
                ItemId = Guid.Parse(reader.GetString(2)),
                Kind = reader.GetString(11),
                Name = Text(reader, 12) ?? string.Empty,
                Year = Int(reader, 13),
                ProviderIds = ReadIds(reader, 14, id, "ProviderIds", warnings) ?? new Dictionary<string, string>(),
                SeriesProviderIds = ReadIds(reader, 15, id, "SeriesProviderIds", warnings),
                SeriesName = Text(reader, 16),
                Season = Int(reader, 17),
                Episode = Int(reader, 18),
                Path = Text(reader, 19),
                SeriesId = ParseId(reader, 20),
                SeasonId = ParseId(reader, 21),
                AlbumId = ParseId(reader, 22)
            },
            DetachedUtc = reader.IsDBNull(23) ? null : ParseTime(reader.GetString(23)),
            DetachReason = Text(reader, 24),
            MirroredValue = reader.IsDBNull(25) ? null : reader.GetDouble(25),
            AttachedUtc = reader.IsDBNull(26) ? null : ParseTime(reader.GetString(26))
        };
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every statement is built from this class's constants and numbered placeholders; values are always parameters.")]
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static string Placeholders(int count)
        => string.Join(", ", Enumerable.Range(0, count).Select(i => string.Create(CultureInfo.InvariantCulture, $"$p{i}")));

    private static void AddList(SqliteCommand command, IEnumerable<string> values)
    {
        var i = 0;
        foreach (var value in values)
        {
            command.Parameters.AddWithValue(string.Create(CultureInfo.InvariantCulture, $"$p{i++}"), value);
        }
    }
}
