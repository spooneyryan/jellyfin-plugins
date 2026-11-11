using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.UserRatings.Storage;

namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// Per item, the raw per-user contributions of its attached rows: what
/// <see cref="VisibilityPolicy.Summarize"/> builds an average, a count and a histogram from at read time, so hiding a user
/// changes the next summary without any call here. An item without ratings is cached too (an empty list), so a details
/// view of an unrated item costs no query either.
/// </summary>
/// <remarks>
/// <para><b>Consistency</b>. (1) A fill reads the store's <see cref="IRatingsStore.Generation"/>
/// before its query and inserts only while the generation is unchanged, the check and the insert under the cache's lock.
/// (2) Every write drops the entries of the items it touched (the store's <see cref="IRatingsStore.Changed"/>, raised after
/// the kit's commit and its generation bump), under the same lock. So a fill whose query read the state before a write either
/// finds the generation changed (discarded) or inserted before the drop (removed by it); one that inserts after the drop
/// read the generation after the bump, so its query saw the write. No fill leaves a value older than a write. The next read
/// refills the item with one indexed query. (3) A restore (an admin's, a corruption restore, the restore at open) raises the
/// store's <see cref="IRatingsStore.RestoreCount"/>, which every read compares first: when it moved, the cache starts over
/// (a restore names no item, so rule 2 can't drop what it changed), and a fill whose query began
/// before it is discarded like one that crossed a write. (4) A bulk operation and anything else that changes rows outside
/// the store's units of work calls <see cref="Clear"/>. A setting changes nothing here: the entries hold raw rows, and the
/// settings apply at read time.</para>
/// <para>At most <see cref="MaxEntries"/> items are kept; past that a fill starts over with an empty cache.</para>
/// </remarks>
internal sealed class SummaryCache : IDisposable
{
    /// <summary>The most items kept.</summary>
    public const int MaxEntries = 20000;

    private readonly IRatingsStore _store;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, IReadOnlyList<SummaryRow>> _entries = [];
    private long _fills;
    private long _discarded;
    private long _restores;

    /// <summary>
    /// Initializes a new instance of the <see cref="SummaryCache"/> class, which follows the store's writes from now on.
    /// </summary>
    /// <param name="store">The store.</param>
    public SummaryCache(IRatingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _restores = store.RestoreCount;
        _store.Changed += OnChanged;
    }

    /// <summary>Gets how many items are cached.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Gets how many fills were discarded because the generation moved during their query.</summary>
    public long Discarded => Interlocked.Read(ref _discarded);

    /// <summary>Gets how many fills ran a query.</summary>
    public long Fills => Interlocked.Read(ref _fills);

    /// <summary>
    /// Gets or sets a step run between a fill's query and its check-and-insert: the deterministic interleaving hook of
    /// the consistency tests. Never set outside tests.
    /// </summary>
    internal Action<IReadOnlyCollection<Guid>>? AfterQuery { get; set; }

    /// <summary>
    /// Gets an item's contributions.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>Its attached rows' contributions; empty when it has none.</returns>
    public IReadOnlyList<SummaryRow> Get(Guid itemId) => Get([itemId])[itemId];

    /// <summary>
    /// Gets items' contributions, filling the missing ones with one query (in chunks of the store's).
    /// </summary>
    /// <param name="itemIds">The items.</param>
    /// <returns>Each item's contributions; empty lists for items without ratings.</returns>
    public IReadOnlyDictionary<Guid, IReadOnlyList<SummaryRow>> Get(IReadOnlyCollection<Guid> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var result = new Dictionary<Guid, IReadOnlyList<SummaryRow>>();
        var missing = new List<Guid>();

        // Rule 3: a restore since the last read replaced the data under every entry.
        var restores = _store.RestoreCount;
        lock (_lock)
        {
            if (restores != _restores)
            {
                _entries.Clear();
                _restores = restores;
            }

            foreach (var id in itemIds.Distinct())
            {
                if (_entries.TryGetValue(id, out var rows))
                {
                    result[id] = rows;
                }
                else
                {
                    missing.Add(id);
                }
            }
        }

        if (missing.Count == 0)
        {
            return result;
        }

        // Rule 1: the generation before the query.
        var generation = _store.Generation;
        Interlocked.Increment(ref _fills);
        var fetched = missing.ToDictionary(id => id, _ => (IReadOnlyList<SummaryRow>)new List<SummaryRow>());
        foreach (var group in _store.GetSummaryRows(missing).GroupBy(r => r.ItemId))
        {
            fetched[group.Key] = group.ToList();
        }

        AfterQuery?.Invoke(missing);
        lock (_lock)
        {
            if (_store.Generation == generation && _store.RestoreCount == restores)
            {
                if (_entries.Count + fetched.Count > MaxEntries)
                {
                    _entries.Clear();
                }

                foreach (var (id, rows) in fetched)
                {
                    _entries[id] = rows;
                }
            }
            else
            {
                Interlocked.Increment(ref _discarded);
            }
        }

        // This read gets what its query saw, a state no older than the read's own start.
        foreach (var (id, rows) in fetched)
        {
            result[id] = rows;
        }

        return result;
    }

    /// <summary>
    /// Drops items' entries (rule 2; the store's <see cref="IRatingsStore.Changed"/> does it after every write).
    /// </summary>
    /// <param name="itemIds">The items.</param>
    public void Invalidate(IEnumerable<Guid> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        lock (_lock)
        {
            foreach (var id in itemIds)
            {
                _entries.Remove(id);
            }
        }
    }

    /// <summary>
    /// Drops every entry (rule 4): after a bulk operation, or any other change to the rows outside the store's units of
    /// work.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _store.Changed -= OnChanged;

    private void OnChanged(object? sender, RatingsChangedEventArgs e) => Invalidate(e.ItemIds);
}
