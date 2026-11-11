using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Sqlite;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.UserRatings.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.UserRatings.Storage;

/// <summary>
/// <see cref="IRatingsStore"/> on the kit's <see cref="SqliteDatabase"/>: <c>userratings.db</c>,
/// schema <see cref="RatingsSchema"/>. Every write is one unit of work (<see cref="WriteAsync{T}"/>): one transaction of the
/// kit's single writer, after which <see cref="Changed"/> names the items it touched; the store's <see cref="Generation"/>
/// is the database's.
/// </summary>
/// <remarks>
/// <para><b>Isolation</b> (kit README "SQLite"). The constructor, the fields and every public member name
/// no Microsoft.Data.Sqlite type. Each operation calls a <see cref="MethodImplOptions.NoInlining"/> core method, whose
/// body hands <see cref="RatingsSql"/> (or, for a write, a <see cref="SqliteRatingsWriter"/>) to the database, inside a
/// guard: on a server whose Microsoft.Data.Sqlite this build can't bind, the JIT fails on that core method, and the guard
/// answers the database's 503 instead.</para>
/// <para><b>Validation.</b> Before anything is written the record is checked against the schema's rules: the growable enums
/// (<see cref="RatingValues"/>), the star range, the text length, at least one of stars and text, the detached pair, and
/// non-empty ids and kind. The database's CHECK constraints guard the fixed rules a second time.</para>
/// </remarks>
internal sealed class SqliteRatingsStore : IRatingsStore
{
    /// <summary>The longest review the database takes (the schema's CHECK; the setting's 2000–20000 range).</summary>
    public const int MaxReviewLength = 20000;

    private readonly SqliteDatabase _database;
    private readonly ILogger _logger;
    private readonly RatingsRowWarnings _warnings;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteRatingsStore"/> class. Nothing is opened.
    /// </summary>
    /// <param name="database">The plugin's database (<c>AddKitSqliteDatabase</c>).</param>
    /// <param name="logger">The logger; none by default.</param>
    public SqliteRatingsStore(SqliteDatabase database, ILogger<SqliteRatingsStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _warnings = new RatingsRowWarnings(_logger);
    }

    /// <inheritdoc />
    public event EventHandler<RatingsChangedEventArgs>? Changed;

    /// <inheritdoc />
    public long Generation => _database.Generation;

    /// <inheritdoc />
    public long RestoreCount => _database.RestoreCount;

    /// <inheritdoc />
    public bool IsReadOnly => _database.IsReadOnly;

    /// <inheritdoc />
    public string? StatusMessage => _database.StatusMessage;

    /// <inheritdoc />
    public string DataDirectory => System.IO.Path.GetDirectoryName(_database.FilePath)!;

    /// <summary>
    /// Checks a record against the schema's rules that the store enforces.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <exception cref="ArgumentException">A rule is broken.</exception>
    public static void Validate(RatingRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Id == Guid.Empty || record.UserId == Guid.Empty || record.Identity.ItemId == Guid.Empty)
        {
            throw new ArgumentException("A rating needs its own id, a user id and an item id.", nameof(record));
        }

        if (string.IsNullOrWhiteSpace(record.Identity.Kind))
        {
            throw new ArgumentException("A rating needs its item's kind.", nameof(record));
        }

        if (record.Stars2 is < 1 or > 10)
        {
            throw new ArgumentException("Stars are 1 to 10 half stars (0.5 to 5).", nameof(record));
        }

        if (record.ReviewText is { Length: 0 or > MaxReviewLength })
        {
            throw new ArgumentException("A review is 1 to 20000 characters; an empty one is null.", nameof(record));
        }

        // SQLite's length() stops at the first NUL, so the CHECK on ReviewText would count "a\0b" as 1 character and refuse
        // "\0a" as empty; ReviewText's normalisation removes control characters before this.
        if (record.ReviewText is { } text && text.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A review can't hold a NUL character.", nameof(record));
        }

        if (record.Stars2 is null && record.ReviewText is null)
        {
            throw new ArgumentException("A rating has stars, a review or both.", nameof(record));
        }

        if (record.EditCount < 0)
        {
            throw new ArgumentException("The edit count can't be negative.", nameof(record));
        }

        RatingValues.Require(RatingValues.Origin.All, record.Origin, "Origin");
        if ((record.DetachedUtc is null) != (record.DetachReason is null))
        {
            throw new ArgumentException("A detached rating has both its time and its reason; an attached one neither.", nameof(record));
        }

        if (record.DetachReason is not null)
        {
            RatingValues.Require(RatingValues.DetachReason.All, record.DetachReason, "DetachReason");
        }
    }

    /// <inheritdoc />
    public RatingRecord? Get(Guid ratingId) => Guard(() => GetCore(ratingId));

    /// <inheritdoc />
    public RatingRecord? GetAttached(Guid userId, Guid itemId) => Guard(() => GetAttachedCore(userId, itemId));

    /// <inheritdoc />
    public IReadOnlyList<RatingRecord> GetAttachedForItem(Guid itemId) => Guard(() => GetAttachedForItemCore(itemId));

    /// <inheritdoc />
    public IReadOnlyList<RatingRecord> GetAttachedForUser(Guid userId) => Guard(() => GetAttachedForUserCore(userId));

    /// <inheritdoc />
    public IReadOnlyList<RatingRecord> GetOn(IReadOnlyCollection<Guid> itemIds, bool includeDetached)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        return itemIds.Count == 0 ? [] : Guard(() => GetOnCore(itemIds, includeDetached));
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> GetAttachedItemIds() => Guard(GetAttachedItemIdsCore);

    /// <inheritdoc />
    public IReadOnlyList<RatingRecord> GetDetached() => Guard(GetDetachedCore);

    /// <inheritdoc />
    public IReadOnlyList<SummaryRow> GetSummaryRows(IReadOnlyCollection<Guid> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        return itemIds.Count == 0 ? [] : Guard(() => GetSummaryRowsCore(itemIds));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetKeys(Guid ratingId) => Guard(() => GetKeysCore(ratingId));

    /// <inheritdoc />
    public IReadOnlyList<RatingRecord> FindByKeys(IReadOnlyCollection<string> keys, bool detached)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return keys.Count == 0 ? [] : Guard(() => FindByKeysCore(keys, detached));
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> FindItemIdsByKeys(IReadOnlyCollection<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return keys.Count == 0 ? [] : Guard(() => FindItemIdsByKeysCore(keys));
    }

    /// <inheritdoc />
    public void ThrowIfReadOnly() => _database.ThrowIfReadOnly();

    /// <inheritdoc />
    public async Task<T> WriteAsync<T>(Func<IRatingsWriter, T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        var touched = new HashSet<Guid>();
        var committed = false;
        try
        {
            var result = await GuardAsync(() => WriteCore(work, touched, cancellationToken)).ConfigureAwait(false);
            committed = true;
            return result;
        }
        finally
        {
            // After the kit's commit and its generation bump (or the rollback): the summary cache drops their entries.
            if (touched.Count > 0)
            {
                RaiseChanged(touched, committed);
            }
        }
    }

    /// <inheritdoc />
    public async Task<(T Result, string? Safety)> RunWriteAsync<T>(Func<IRatingsWriter, T> work, bool commit, string? preOpOperation, Action<IRatingsWriter>? check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        var touched = new HashSet<Guid>();
        var committed = false;
        try
        {
            var result = await GuardAsync(() => WriteCore(work, touched, commit, preOpOperation, check, cancellationToken)).ConfigureAwait(false);
            committed = commit;
            return result;
        }
        finally
        {
            if (touched.Count > 0)
            {
                RaiseChanged(touched, committed);
            }
        }
    }

    /// <inheritdoc />
    public RatingsTables ReadTables() => Guard(ReadTablesCore);

    /// <inheritdoc />
    public IReadOnlyList<StoreSnapshotInfo> ListSnapshots() => _database.ListSnapshots();

    /// <inheritdoc />
    public RatingsTables ReadPreOpSnapshotTables(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var info = _database.ListSnapshots().FirstOrDefault(s => s.Kind == SnapshotKind.PreOp && string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw PluginException.NotFound("No pre-operation snapshot has that name.");
        return Guard(() => ReadSnapshotTablesCore(System.IO.Path.Combine(SnapshotRotation.PreOpDirectory(_database.SnapshotDirectory), info.Name)));
    }

    /// <inheritdoc />
    public Task<SnapshotRestoreResult> RestoreSnapshotAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _database.RestoreSnapshotAsync(name, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SqliteRecoveryResult> StartEmptyAsync(CancellationToken cancellationToken)
        => _database.RecoverAsync(null, startEmpty: true, cancellationToken);

    /// <inheritdoc />
    public IDictionary<string, object?> DescribeIntegrity()
    {
        var status = _database.Status;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Status"] = status.ToString(),
            ["ReadOnly"] = _database.IsReadOnly,
            ["Damaged"] = status == SqliteDatabaseStatus.ReadOnlyCorrupt,
            ["Message"] = _database.StatusMessage,
            ["LastRecovery"] = _database.LastRecovery?.ToDictionary(),
            ["Quarantines"] = _database.ListQuarantines().Select(q => q.ToDictionary()).ToList()
        };
    }

    /// <inheritdoc />
    public async Task<RatingRecord> UpsertAsync(RatingRecord record, CancellationToken cancellationToken)
    {
        Validate(record);
        await WriteAsync(
            w =>
            {
                w.Upsert(record);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <inheritdoc />
    public Task<int> UpsertManyAsync(IReadOnlyCollection<RatingRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var record in records)
        {
            Validate(record);
        }

        return records.Count == 0
            ? Task.FromResult(0)
            : WriteAsync(
                w =>
                {
                    foreach (var record in records)
                    {
                        w.Upsert(record);
                    }

                    return records.Count;
                },
                cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(Guid ratingId, string deletedBy, DateTime deletedUtc, CancellationToken cancellationToken)
    {
        RatingValues.Require(RatingValues.DeletedBy.All, deletedBy, "DeletedBy");
        return WriteAsync(w => w.Delete(ratingId, deletedBy, deletedUtc), cancellationToken);
    }

    /// <inheritdoc />
    public TombstoneRecord? GetTombstone(Guid ratingId) => Guard(() => GetTombstoneCore(ratingId));

    /// <inheritdoc />
    public Task<long> AppendModerationLogAsync(ModerationLogRecord entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        RatingValues.Require(RatingValues.ModerationAction.All, entry.Action, "Action");
        return WriteAsync(w => w.AppendModerationLog(entry), cancellationToken);
    }

    /// <inheritdoc />
    public IReadOnlyList<ModerationLogRecord> GetModerationLog(int startIndex, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return Guard(() => GetModerationLogCore(startIndex, limit));
    }

    /// <inheritdoc />
    public (IReadOnlyList<ModerationLogRecord> Rows, int Total) GetModerationLogPage(int startIndex, int limit, string? action)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (action is not null)
        {
            RatingValues.Require(RatingValues.ModerationAction.All, action, "Action");
        }

        return Guard(() => GetModerationLogPageCore(startIndex, limit, action));
    }

    /// <inheritdoc />
    public int CountModerationLogBefore(DateTime cutoffUtc) => Guard(() => CountModerationLogBeforeCore(cutoffUtc));

    /// <inheritdoc />
    public IReadOnlyDictionary<string, (int Ratings, int Reviews)> CountAttachedByKind() => Guard(CountAttachedByKindCore);

    /// <inheritdoc />
    public int CountReviewsLongerThan(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return Guard(() => CountReviewsLongerThanCore(length));
    }

    /// <inheritdoc />
    public (IReadOnlyList<RatingRecord> Rows, int Total) GetForModeration(Guid? userId, Guid? itemId, bool? hasText, bool? detached, DateTime? sinceUtc, int startIndex, int? limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        if (limit is { } take)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        }

        return Guard(() => GetForModerationCore(userId, itemId, hasText, detached, sinceUtc, startIndex, limit));
    }

    /// <inheritdoc />
    public ImportLedgerRecord? GetImport(string source, string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrEmpty(sourceKey);
        return Guard(() => GetImportCore(source, sourceKey));
    }

    /// <inheritdoc />
    public Task RecordImportAsync(ImportLedgerRecord entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Source);
        ArgumentException.ThrowIfNullOrEmpty(entry.SourceKey);
        ArgumentNullException.ThrowIfNull(entry.RatingIds);
        return WriteAsync(
            w =>
            {
                w.RecordImport(entry);
                return 0;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public string? GetMeta(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Guard(() => GetMetaCore(key));
    }

    /// <inheritdoc />
    public Task SetMetaAsync(string key, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        return WriteAsync(
            w =>
            {
                w.SetMeta(key, value);
                return 0;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, string> GetFormerUsers() => Guard(GetFormerUsersCore);

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, int> CountByUser() => Guard(CountByUserCore);

    /// <inheritdoc />
    public Task<string?> PreOpSnapshotAsync(string operation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return GuardAsync(() => _database.PreOpSnapshotAsync(operation, cancellationToken));
    }

    /// <inheritdoc />
    public Task<bool> RequestCheckpoint() => _database.RequestCheckpoint();

    /// <inheritdoc />
    public Task<bool> WaitForCheckpointAsync(CancellationToken cancellationToken) => _database.WaitForCheckpointAsync(cancellationToken);

    /// <inheritdoc />
    public RatingsStoreCounts Count() => Guard(CountCore);

    // A server whose Microsoft.Data.Sqlite this build can't bind fails when the JIT compiles a core method below: the
    // database's 503, never an exception the caller doesn't expect (kit README "SQLite", Isolation).
    private static T Guard<T>(Func<T> work)
    {
        try
        {
            return work();
        }
        catch (Exception ex) when (SqliteDatabase.IsBindingFailure(ex))
        {
            throw new PluginException(PluginErrorKind.Unavailable, SqliteMessages.Unavailable, ex);
        }
    }

    private async Task<T> GuardAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (SqliteDatabase.IsBindingFailure(ex))
        {
            throw new PluginException(PluginErrorKind.Unavailable, SqliteMessages.Unavailable, ex);
        }
        catch (InvalidOperationException ex)
        {
            // A row the database refused although it passed validation (RatingsSql's refusal), or another fault: a 500,
            // logged here with the store's context (no values), never the member's 409.
            _logger.LogError(ex, "A write to the ratings database failed and was rolled back");
            throw;
        }
    }

    // ------------------------------------------------------------------------------------------------ the SQLite work

    [MethodImpl(MethodImplOptions.NoInlining)]
    private RatingRecord? GetCore(Guid ratingId)
        => _database.Read(c => RatingsSql.Ratings(c, null, _warnings, "WHERE Id = $Id;", ("$Id", RatingsSql.Id(ratingId))).FirstOrDefault());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private RatingRecord? GetAttachedCore(Guid userId, Guid itemId)
        => _database.Read(c => RatingsSql.Ratings(c, null, _warnings, "WHERE UserId = $UserId AND ItemId = $ItemId AND DetachedUtc IS NULL;", ("$UserId", RatingsSql.Id(userId)), ("$ItemId", RatingsSql.Id(itemId))).FirstOrDefault());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<RatingRecord> GetAttachedForItemCore(Guid itemId)
        => _database.Read(c => RatingsSql.Ratings(c, null, _warnings, "WHERE ItemId = $ItemId AND DetachedUtc IS NULL ORDER BY UpdatedUtc DESC, Id;", ("$ItemId", RatingsSql.Id(itemId))));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<RatingRecord> GetAttachedForUserCore(Guid userId)
        => _database.Read(c => RatingsSql.Ratings(c, null, _warnings, "WHERE UserId = $UserId AND DetachedUtc IS NULL ORDER BY UpdatedUtc DESC, Id;", ("$UserId", RatingsSql.Id(userId))));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<RatingRecord> GetOnCore(IReadOnlyCollection<Guid> itemIds, bool includeDetached)
        => _database.Read(c => RatingsSql.On(c, null, _warnings, itemIds, includeDetached));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<Guid> GetAttachedItemIdsCore() => _database.Read(RatingsSql.AttachedItemIds);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<RatingRecord> GetDetachedCore()
        => _database.Read(c => RatingsSql.Ratings(c, null, _warnings, "WHERE DetachedUtc IS NOT NULL ORDER BY DetachedUtc, Id;"));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<SummaryRow> GetSummaryRowsCore(IReadOnlyCollection<Guid> itemIds)
        => _database.Read(c => RatingsSql.SummaryRows(c, itemIds));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<string> GetKeysCore(Guid ratingId) => _database.Read(c => RatingsSql.Keys(c, ratingId));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<RatingRecord> FindByKeysCore(IReadOnlyCollection<string> keys, bool detached)
        => _database.Read(c => RatingsSql.ByKeys(c, _warnings, keys, detached));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<Guid> FindItemIdsByKeysCore(IReadOnlyCollection<string> keys)
        => _database.Read(c => RatingsSql.AttachedItemIdsByKeys(c, keys));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private TombstoneRecord? GetTombstoneCore(Guid ratingId) => _database.Read(c => RatingsSql.Tombstone(c, ratingId));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<ModerationLogRecord> GetModerationLogCore(int startIndex, int limit)
        => _database.Read(c => RatingsSql.ModerationLog(c, startIndex, limit));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int CountModerationLogBeforeCore(DateTime cutoffUtc) => _database.Read(c => RatingsSql.CountModerationLogBefore(c, cutoffUtc));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private IReadOnlyDictionary<string, (int Ratings, int Reviews)> CountAttachedByKindCore() => _database.Read(RatingsSql.CountAttachedByKind);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int CountReviewsLongerThanCore(int length) => _database.Read(c => RatingsSql.CountReviewsLongerThan(c, length));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private (IReadOnlyList<ModerationLogRecord> Rows, int Total) GetModerationLogPageCore(int startIndex, int limit, string? action)
        => _database.Read(c => ((IReadOnlyList<ModerationLogRecord>)RatingsSql.ModerationLog(c, startIndex, limit, action), RatingsSql.CountModerationLog(c, action)));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private (IReadOnlyList<RatingRecord> Rows, int Total) GetForModerationCore(Guid? userId, Guid? itemId, bool? hasText, bool? detached, DateTime? sinceUtc, int startIndex, int? limit)
        => _database.Read(c =>
        {
            var (rows, total) = RatingsSql.ForModeration(c, _warnings, userId, itemId, hasText, detached, sinceUtc, startIndex, limit);
            return ((IReadOnlyList<RatingRecord>)rows, total);
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ImportLedgerRecord? GetImportCore(string source, string sourceKey) => _database.Read(c => RatingsSql.Import(c, source, sourceKey));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private string? GetMetaCore(string key) => _database.Read(c => RatingsSql.Meta(c, key));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Dictionary<Guid, string> GetFormerUsersCore() => _database.Read(c => RatingsSql.FormerUsers(c));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Dictionary<Guid, int> CountByUserCore() => _database.Read(RatingsSql.CountByUser);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private RatingsStoreCounts CountCore() => _database.Read(RatingsSql.Count);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private RatingsTables ReadTablesCore() => _database.Read(c => RatingsSql.Tables(c, _warnings));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private RatingsTables ReadSnapshotTablesCore(string path)
    {
        try
        {
            return RatingsSql.SnapshotTables(path, _warnings);
        }
        catch (Exception ex) when (ex is not PluginException && !SqliteDatabase.IsBindingFailure(ex))
        {
            throw new PluginException(PluginErrorKind.Unavailable, "The snapshot " + System.IO.Path.GetFileName(path) + " can't be read: " + ex.Message, ex);
        }
    }

    // One unit of work: the writer made inside the kit's write, on its connection and transaction, with the generation the
    // commit gives (the kit raises it under the writer lock, after the commit, and only there or in a restore, which takes
    // the same lock).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Task<T> WriteCore<T>(Func<IRatingsWriter, T> work, HashSet<Guid> touched, CancellationToken cancellationToken)
        => _database.WriteAsync((c, t) => work(new SqliteRatingsWriter(c, t, _database.Generation + 1, _warnings, touched)), cancellationToken);

    // The same, committed or rolled back, with the kit's pre-operation snapshot in the same hold of the writer lock,
    // and the check before it (the rows it touches are its own, never raised: it is rolled back).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Task<(T Result, string? Safety)> WriteCore<T>(Func<IRatingsWriter, T> work, HashSet<Guid> touched, bool commit, string? preOpOperation, Action<IRatingsWriter>? check, CancellationToken cancellationToken)
        => _database.RunWriteAsync(
            (c, t) => work(new SqliteRatingsWriter(c, t, _database.Generation + 1, _warnings, touched)),
            commit,
            preOpOperation,
            check is null ? null : (c, t) => check(new SqliteRatingsWriter(c, t, _database.Generation + 1, _warnings, new HashSet<Guid>())),
            cancellationToken);

    private void RaiseChanged(HashSet<Guid> touched, bool committed)
    {
        try
        {
            Changed?.Invoke(this, new RatingsChangedEventArgs(touched, committed));
        }
        catch (Exception ex)
        {
            // A cache's handler never fails the write that already committed.
            _logger.LogError(ex, "A handler of the ratings store's Changed event failed");
        }
    }
}
