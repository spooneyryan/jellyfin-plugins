using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.UserRatings.Domain;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.UserRatings.Storage;

/// <summary>
/// <see cref="IRatingsWriter"/> on the kit writer's open connection and transaction: every operation is
/// <see cref="RatingsSql"/> given that transaction. Made only by <see cref="SqliteRatingsStore"/>'s <c>NoInlining</c> write
/// core, inside the kit's write; its methods are <c>NoInlining</c> too, so a build that can't bind the server's
/// Microsoft.Data.Sqlite fails there, inside the store's guard (the kit's isolation; the IL test lists it with
/// <see cref="RatingsSql"/> and the store as the plugin's only SQLite owners).
/// </summary>
internal sealed class SqliteRatingsWriter : IRatingsWriter
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly RatingsRowWarnings? _warnings;
    private readonly ISet<Guid> _touched;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteRatingsWriter"/> class.
    /// </summary>
    /// <param name="connection">The writer's connection.</param>
    /// <param name="transaction">Its open transaction.</param>
    /// <param name="generation">The generation the store will have once this commits.</param>
    /// <param name="warnings">Where a stored value read tolerantly is reported.</param>
    /// <param name="touched">Collects the items the operations wrote.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqliteRatingsWriter(SqliteConnection connection, SqliteTransaction transaction, long generation, RatingsRowWarnings? warnings, ISet<Guid> touched)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(touched);
        _connection = connection;
        _transaction = transaction;
        Generation = generation;
        _warnings = warnings;
        _touched = touched;
    }

    /// <inheritdoc />
    public long Generation { get; }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public RatingRecord? Get(Guid ratingId)
        => RatingsSql.Ratings(_connection, _transaction, _warnings, "WHERE Id = $Id;", ("$Id", RatingsSql.Id(ratingId))).FirstOrDefault();

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public RatingRecord? GetAttached(Guid userId, Guid itemId)
        => RatingsSql.Ratings(_connection, _transaction, _warnings, "WHERE UserId = $UserId AND ItemId = $ItemId AND DetachedUtc IS NULL;", ("$UserId", RatingsSql.Id(userId)), ("$ItemId", RatingsSql.Id(itemId))).FirstOrDefault();

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IReadOnlyList<RatingRecord> GetOn(IReadOnlyCollection<Guid> itemIds, bool includeDetached)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        return itemIds.Count == 0 ? [] : RatingsSql.On(_connection, _transaction, _warnings, itemIds, includeDetached);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IReadOnlyList<RatingRecord> GetByUser(Guid userId)
        => RatingsSql.Ratings(_connection, _transaction, _warnings, "WHERE UserId = $UserId ORDER BY ItemId, Id;", ("$UserId", RatingsSql.Id(userId)));

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IReadOnlyDictionary<Guid, string> GetFormerUsers() => RatingsSql.FormerUsers(_connection, _transaction);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public string? GetMeta(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return RatingsSql.Meta(_connection, key, _transaction);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public RatingRecord SaveStars(Guid userId, IdentitySnapshot identity, int stars2, DateTime utc)
    {
        RequireMember(userId, identity);
        ArgumentOutOfRangeException.ThrowIfLessThan(stars2, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stars2, 10);
        _touched.Add(identity.ItemId);
        return RatingsSql.SaveStars(_connection, _transaction, userId, identity, stars2, utc, _warnings);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public RatingRecord SaveReview(Guid userId, IdentitySnapshot identity, string text, bool isSpoiler, DateTime utc)
    {
        RequireMember(userId, identity);
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length is 0 or > SqliteRatingsStore.MaxReviewLength || text.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A review is 1 to 20000 characters with no NUL; normalise it first (ReviewText).", nameof(text));
        }

        _touched.Add(identity.ItemId);
        return RatingsSql.SaveReview(_connection, _transaction, userId, identity, text, isSpoiler, utc, _warnings);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public RatingRecord ClearStars(Guid ratingId, DateTime utc)
        => Touch(RatingsSql.ClearStars(_connection, _transaction, ratingId, utc, _warnings)
            ?? throw new InvalidOperationException("Only the stars of a rating that keeps its review can be cleared; delete the rating instead."));

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public RatingRecord ClearReview(Guid ratingId, DateTime utc)
        => Touch(RatingsSql.ClearReview(_connection, _transaction, ratingId, utc, _warnings)
            ?? throw new InvalidOperationException("Only the review of a rating that keeps its stars can be cleared; delete the rating instead."));

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Upsert(RatingRecord record)
    {
        SqliteRatingsStore.Validate(record);

        // A row that moves (a relink, a merged version) leaves its old item too.
        if (RatingsSql.ItemIdOf(_connection, _transaction, record.Id) is { } before)
        {
            _touched.Add(before);
        }

        _touched.Add(record.ItemId);
        RatingsSql.Upsert(_connection, _transaction, record);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool Delete(Guid ratingId, string deletedBy, DateTime deletedUtc, string? origin = null)
    {
        RatingValues.Require(RatingValues.DeletedBy.All, deletedBy, "DeletedBy");
        if (RatingsSql.DeleteRow(_connection, _transaction, ratingId, deletedBy, deletedUtc, origin) is not { } itemId)
        {
            return false;
        }

        _touched.Add(itemId);
        return true;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool RemoveArchived(Guid ratingId)
    {
        if (RatingsSql.RemoveDetachedRow(_connection, _transaction, ratingId) is not { } itemId)
        {
            return false;
        }

        _touched.Add(itemId);
        return true;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public long AppendModerationLog(ModerationLogRecord entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        RatingValues.Require(RatingValues.ModerationAction.All, entry.Action, "Action");
        return RatingsSql.AppendModerationLog(_connection, _transaction, entry);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int DeleteModerationLogBefore(DateTime cutoffUtc, DateTime? removalsCutoffUtc = null) => RatingsSql.DeleteModerationLogBefore(_connection, _transaction, cutoffUtc, removalsCutoffUtc);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool HasModerationAction(Guid targetUserId, string action) => RatingsSql.HasModerationAction(_connection, _transaction, targetUserId, action);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool HasTombstone(Guid ratingId) => RatingsSql.HasTombstone(_connection, _transaction, ratingId);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public TombstoneRecord? GetTombstone(Guid ratingId) => RatingsSql.Tombstone(_connection, _transaction, ratingId);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IReadOnlyList<TombstoneRecord> GetTombstonesOf(Guid userId, Guid itemId) => RatingsSql.TombstonesOf(_connection, _transaction, userId, itemId);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void AddTombstone(Guid ratingId, Guid userId, Guid itemId, string deletedBy, DateTime deletedUtc, string? origin = null)
    {
        RatingValues.Require(RatingValues.DeletedBy.All, deletedBy, "DeletedBy");
        RatingsSql.AddTombstone(_connection, _transaction, ratingId, userId, itemId, deletedBy, deletedUtc, origin);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IReadOnlyList<ModerationLogRecord> GetModerationLogKeys() => RatingsSql.ModerationLogKeys(_connection, _transaction);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void RecordImport(ImportLedgerRecord entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Source);
        ArgumentException.ThrowIfNullOrEmpty(entry.SourceKey);
        ArgumentNullException.ThrowIfNull(entry.RatingIds);
        RatingsSql.RecordImport(_connection, _transaction, entry);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void AddFormerUser(Guid id, Guid originalUserId, string displayName, DateTime deletedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (id == Guid.Empty || originalUserId == Guid.Empty || id == originalUserId)
        {
            throw new ArgumentException("A former member needs a new id of its own and the deleted user's id.", nameof(id));
        }

        RatingsSql.AddFormerUser(_connection, _transaction, id, originalUserId, displayName, deletedUtc);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void SetMeta(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        RatingsSql.SetMeta(_connection, _transaction, key, value);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public ImportLedgerRecord? GetImport(string source, string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        return RatingsSql.Import(_connection, source, sourceKey, _transaction);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int WipeForReplace()
    {
        var (ratings, items) = RatingsSql.WipeForReplace(_connection, _transaction);
        foreach (var item in items)
        {
            _touched.Add(item);
        }

        return ratings;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool AddFormerUserIfAbsent(FormerUserRecord row) => RatingsSql.AddFormerUserIfAbsent(_connection, _transaction, row);

    private static void RequireMember(Guid userId, IdentitySnapshot identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (userId == Guid.Empty || identity.ItemId == Guid.Empty || string.IsNullOrWhiteSpace(identity.Kind))
        {
            throw new ArgumentException("A member's rating needs a user id, an item id and the item's kind.", nameof(identity));
        }
    }

    private RatingRecord Touch(RatingRecord row)
    {
        _touched.Add(row.ItemId);
        return row;
    }
}
