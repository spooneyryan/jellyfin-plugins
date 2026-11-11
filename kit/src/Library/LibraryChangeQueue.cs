using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// One consumer's changes waiting for their batch, its timing and its counters. Thread-safe: the
/// event handlers offer changes while the monitor takes batches.
/// </summary>
/// <remarks>
/// <para><b>One change per item and list.</b> The waiting changes are kept by item id, so a busy library ("Refresh all
/// metadata" on 60,000 items) holds at most one entry per item, never one per event: an item added and then updated stays
/// <see cref="LibraryChangeKind.Added"/>; repeated updates combine their reasons; an item added or updated and then removed
/// is only removed; an item removed and added again (the same path gives the same id) is in both lists, so the consumer
/// detaches and then finds it again by its id; user data is kept per item and user. Each list keeps the order in which its
/// items were first seen.</para>
/// <para><b>Timing</b> (a trailing debounce): the batch is due when no change came for the quiet period, or at the latest
/// the maximum wait after its first change. A change raised after a scan's full pass while that scan still ran marks the
/// waiting changes <see cref="HasAfterFullPass"/>: the monitor sends them as soon as the scan has ended instead.</para>
/// </remarks>
internal sealed class LibraryChangeQueue
{
    /// <summary>How many batch summaries Diagnostics shows per consumer.</summary>
    public const int MaxRecent = 10;

    private readonly object _lock = new();
    private readonly Dictionary<Guid, Entry> _added = [];
    private readonly Dictionary<Guid, Entry> _updated = [];
    private readonly Dictionary<Guid, Entry> _removed = [];
    private readonly Dictionary<Guid, Entry> _removedFolders = [];
    private readonly Dictionary<(Guid ItemId, Guid UserId), Entry> _userData = [];
    private readonly Queue<IDictionary<string, object?>> _recent = new();
    private long _sequence;
    private DateTimeOffset? _first;
    private DateTimeOffset? _last;
    private bool _afterFullPass;
    private bool _deferralNoted;
    private long _batches;
    private long _fullPasses;
    private long _failures;
    private long _deferrals;
    private DateTimeOffset? _lastBatchUtc;
    private DateTimeOffset? _lastFullPassUtc;
    private string? _lastError;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryChangeQueue"/> class.
    /// </summary>
    /// <param name="consumer">The consumer; its filter is read once, here.</param>
    public LibraryChangeQueue(ILibraryChangeConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        Consumer = consumer;
        Name = string.IsNullOrWhiteSpace(consumer.Name) ? consumer.GetType().Name : consumer.Name;
        Filter = consumer.Filter ?? new LibraryChangeFilter();
        (QuietPeriod, MaxWait) = Filter.EffectiveDelays();
    }

    /// <summary>Gets the consumer.</summary>
    public ILibraryChangeConsumer Consumer { get; }

    /// <summary>Gets the consumer's name.</summary>
    public string Name { get; }

    /// <summary>Gets the consumer's filter.</summary>
    public LibraryChangeFilter Filter { get; }

    /// <summary>Gets the quiet period in use.</summary>
    public TimeSpan QuietPeriod { get; }

    /// <summary>Gets the maximum wait in use.</summary>
    public TimeSpan MaxWait { get; }

    /// <summary>Gets how many batches reached the consumer.</summary>
    public long Batches => Interlocked.Read(ref _batches);

    /// <summary>Gets how many of them were full passes.</summary>
    public long FullPasses => Interlocked.Read(ref _fullPasses);

    /// <summary>Gets how many batches the consumer failed.</summary>
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>Gets how many times a due batch waited for a library scan to end.</summary>
    public long Deferrals => Interlocked.Read(ref _deferrals);

    /// <summary>
    /// Gets a value indicating whether a waiting change was raised after a scan's full pass while that scan still ran
    /// (Jellyfin's own post-scan tasks), so the batch goes out as soon as the scan has ended.
    /// </summary>
    public bool HasAfterFullPass
    {
        get
        {
            lock (_lock)
            {
                return _afterFullPass;
            }
        }
    }

    /// <summary>Gets a value indicating whether no change waits.</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_lock)
            {
                return IsEmptyLocked();
            }
        }
    }

    /// <summary>
    /// Adds a change. A removed item goes to the removed list, to the removed folders, or both.
    /// </summary>
    /// <param name="change">The change.</param>
    /// <param name="toList">Whether it goes to its list (Added, Updated, Removed or UserData): the filter accepted it.</param>
    /// <param name="toRemovedFolders">Whether a removed folder goes to <see cref="LibraryChangeBatch.RemovedFolders"/>.</param>
    /// <param name="afterFullPass">Whether it was raised after a scan's full pass while that scan still ran.</param>
    public void Offer(ItemChange change, bool toList, bool toRemovedFolders, bool afterFullPass = false)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!toList && !toRemovedFolders)
        {
            return;
        }

        lock (_lock)
        {
            var id = change.ItemId;
            switch (change.Kind)
            {
                case LibraryChangeKind.Added:
                    _updated.Remove(id);
                    Put(_added, id, change);
                    break;
                case LibraryChangeKind.Updated:
                    if (_added.TryGetValue(id, out var added))
                    {
                        _added[id] = added with { Change = Merge(added.Change, change, LibraryChangeKind.Added) };
                    }
                    else if (_updated.TryGetValue(id, out var updated))
                    {
                        _updated[id] = updated with { Change = Merge(updated.Change, change, LibraryChangeKind.Updated) };
                    }
                    else
                    {
                        Put(_updated, id, change);
                    }

                    break;
                case LibraryChangeKind.Removed:
                    _added.Remove(id);
                    _updated.Remove(id);
                    if (toList)
                    {
                        Put(_removed, id, change);
                    }

                    if (toRemovedFolders)
                    {
                        Put(_removedFolders, id, change);
                    }

                    break;
                case LibraryChangeKind.UserData:
                    Put(_userData, (id, change.UserId ?? Guid.Empty), change);
                    break;
                default:
                    return;
            }

            if (IsEmptyLocked())
            {
                _first = null;
                _last = null;
                _afterFullPass = false;
            }
            else
            {
                _first ??= change.Utc;
                _last = change.Utc;
                _afterFullPass |= afterFullPass;
            }
        }
    }

    /// <summary>
    /// Gets when the waiting changes are due: the last change plus the quiet period, or the first plus the maximum wait,
    /// whichever comes first; null when nothing waits.
    /// </summary>
    /// <returns>The due time.</returns>
    public DateTimeOffset? DueAt()
    {
        lock (_lock)
        {
            if (_first is not { } first || _last is not { } last)
            {
                return null;
            }

            var quiet = last + QuietPeriod;
            var latest = first + MaxWait;
            return quiet < latest ? quiet : latest;
        }
    }

    /// <summary>
    /// Takes every waiting change as a batch and starts a new one. A batch that isn't a full pass is
    /// <see cref="LibraryChangeBatch.AfterFullPass"/> when a change of it was.
    /// </summary>
    /// <param name="fullPass">Whether it is the full pass of the post-scan task.</param>
    /// <param name="duringPostScan">Whether it is dispatched inside the scan.</param>
    /// <returns>The batch, empty when nothing waited.</returns>
    public LibraryChangeBatch Take(bool fullPass, bool duringPostScan)
    {
        lock (_lock)
        {
            var batch = new LibraryChangeBatch(
                Ordered(_added.Values),
                Ordered(_updated.Values),
                Ordered(_removed.Values),
                Ordered(_removedFolders.Values),
                Ordered(_userData.Values),
                fullPass,
                duringPostScan,
                afterFullPass: !fullPass && _afterFullPass);
            _added.Clear();
            _updated.Clear();
            _removed.Clear();
            _removedFolders.Clear();
            _userData.Clear();
            _first = null;
            _last = null;
            _afterFullPass = false;
            _deferralNoted = false;
            return batch;
        }
    }

    /// <summary>
    /// Notes that the due batch waits for a library scan to end; counted once per batch.
    /// </summary>
    /// <returns><c>true</c> the first time for the waiting batch.</returns>
    public bool NoteDeferral()
    {
        lock (_lock)
        {
            if (_deferralNoted)
            {
                return false;
            }

            _deferralNoted = true;
            Interlocked.Increment(ref _deferrals);
            return true;
        }
    }

    /// <summary>
    /// Records a dispatched batch for the counters and Diagnostics.
    /// </summary>
    /// <param name="batch">The batch.</param>
    /// <param name="utc">When it was dispatched.</param>
    /// <param name="elapsed">How long the consumer took.</param>
    /// <param name="scanRunning">Whether <c>IsScanRunning</c> was true when it was dispatched.</param>
    /// <param name="error">The consumer's exception, if it failed.</param>
    public void Record(LibraryChangeBatch batch, DateTimeOffset utc, TimeSpan elapsed, bool scanRunning, Exception? error)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Interlocked.Increment(ref _batches);
        if (batch.FullPass)
        {
            Interlocked.Increment(ref _fullPasses);
        }

        if (error is not null)
        {
            Interlocked.Increment(ref _failures);
        }

        lock (_lock)
        {
            _lastBatchUtc = utc;
            if (batch.FullPass)
            {
                _lastFullPassUtc = utc;
            }

            if (error is not null)
            {
                _lastError = error.GetType().Name + ": " + error.Message;
            }

            _recent.Enqueue(new Dictionary<string, object?>
            {
                ["Utc"] = utc.UtcDateTime,
                ["FullPass"] = batch.FullPass,
                ["DuringPostScan"] = batch.DuringPostScan,
                ["AfterFullPass"] = batch.AfterFullPass,
                ["ScanRunning"] = scanRunning,
                ["Added"] = batch.Added.Count,
                ["Updated"] = batch.Updated.Count,
                ["Removed"] = batch.Removed.Count,
                ["RemovedFolders"] = batch.RemovedFolders.Count,
                ["UserData"] = batch.UserData.Count,
                ["Kinds"] = batch.Kinds.Select(k => k.ToString()).Order(StringComparer.Ordinal).ToList(),
                ["Milliseconds"] = Math.Round(elapsed.TotalMilliseconds, 1),
                ["Failed"] = error is not null,
            });
            while (_recent.Count > MaxRecent)
            {
                _recent.Dequeue();
            }
        }
    }

    /// <summary>
    /// Describes the consumer for Diagnostics: its filter, what waits, the counters and the latest batches (newest first).
    /// </summary>
    /// <returns>A plain dictionary.</returns>
    public IDictionary<string, object?> Describe()
    {
        lock (_lock)
        {
            return new Dictionary<string, object?>
            {
                ["Name"] = Name,
                ["Kinds"] = Filter.Kinds?.Select(k => k.ToString()).Order(StringComparer.Ordinal).ToList(),
                ["UserData"] = Filter.IncludeUserData,
                ["QuietSeconds"] = QuietPeriod.TotalSeconds,
                ["MaxWaitSeconds"] = MaxWait.TotalSeconds,
                ["Pending"] = new Dictionary<string, object?>
                {
                    ["Added"] = _added.Count,
                    ["Updated"] = _updated.Count,
                    ["Removed"] = _removed.Count,
                    ["RemovedFolders"] = _removedFolders.Count,
                    ["UserData"] = _userData.Count,
                    ["SinceUtc"] = _first?.UtcDateTime,
                    ["AfterFullPass"] = _afterFullPass,
                },
                ["Batches"] = Batches,
                ["FullPasses"] = FullPasses,
                ["Failures"] = Failures,
                ["Deferrals"] = Deferrals,
                ["LastBatchUtc"] = _lastBatchUtc?.UtcDateTime,
                ["LastFullPassUtc"] = _lastFullPassUtc?.UtcDateTime,
                ["LastError"] = _lastError,
                ["Recent"] = _recent.Reverse().ToList(),
            };
        }
    }

    private static ItemChange Merge(ItemChange earlier, ItemChange later, LibraryChangeKind kind) => later with
    {
        Kind = kind,
        UpdateReason = earlier.UpdateReason | later.UpdateReason,
        ParentId = later.ParentId ?? earlier.ParentId,
        TopParentId = later.TopParentId ?? earlier.TopParentId,
    };

    private static List<ItemChange> Ordered(IEnumerable<Entry> entries)
        => entries.OrderBy(e => e.Sequence).Select(e => e.Change).ToList();

    private void Put<TKey>(Dictionary<TKey, Entry> list, TKey key, ItemChange change)
        where TKey : notnull
    {
        // An item keeps its place in the list; its change is the latest one.
        list[key] = list.TryGetValue(key, out var existing) ? existing with { Change = change } : new Entry(change, ++_sequence);
    }

    private bool IsEmptyLocked()
        => _added.Count == 0 && _updated.Count == 0 && _removed.Count == 0 && _removedFolders.Count == 0 && _userData.Count == 0;

    private readonly record struct Entry(ItemChange Change, long Sequence);
}
