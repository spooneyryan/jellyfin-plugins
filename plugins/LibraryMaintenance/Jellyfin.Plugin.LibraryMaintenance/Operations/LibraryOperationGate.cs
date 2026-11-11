using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.LibraryMaintenance.Operations;

/// <summary>
/// The one gate Move and Duplicates share for file and version changes (locks and busy
/// state): an operation lock (one such operation at a time, refused while a library scan runs, except for work inside
/// the post-scan task), and a per-item busy set (an item, its version partners and its predicted ids while a move job or a
/// Duplicates operation holds them). Merge and delete refuse an item a move holds ("This item is being moved"); Move
/// refuses an item a Duplicates operation holds (<c>move.item-busy</c>).
/// </summary>
/// <remarks>
/// Move 1.1.0.N only reads the busy set (plan time: eligibility and the preflight); the move jobs will mark it. Duplicates 1.1
///  marks it around a merge or delete. The set is in memory: a restart clears it, and recovery
/// re-registers unfinished jobs.
/// </remarks>
internal sealed class LibraryOperationGate : IDisposable
{
    /// <summary>The message Duplicates shows for an item a move holds.</summary>
    public const string BeingMovedMessage = "This item is being moved.";

    /// <summary>The message Move shows for an item Duplicates holds.</summary>
    public const string BeingChangedMessage = "This item is being changed on the Duplicates tab.";

    private readonly ILibraryManager _library;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly Dictionary<Guid, BusyItem> _busy = [];
    private readonly Lock _gate = new();
    private string? _current;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryOperationGate"/> class.
    /// </summary>
    /// <param name="library">The library manager (<c>IsScanRunning</c>).</param>
    public LibraryOperationGate(ILibraryManager library)
    {
        ArgumentNullException.ThrowIfNull(library);
        _library = library;
    }

    /// <summary>Gets what holds the operation lock now, or null.</summary>
    public string? CurrentOperation => Volatile.Read(ref _current);

    /// <summary>Gets a value indicating whether a library scan runs, which refuses the operation lock (outside the post-scan task).</summary>
    public bool IsScanRunning => _library.IsScanRunning;

    /// <summary>
    /// Gets the message for an item another operation holds.
    /// </summary>
    /// <param name="owner">The feature holding it.</param>
    /// <returns>The message.</returns>
    public static string BusyMessage(OperationOwner owner) => owner == OperationOwner.Move ? BeingMovedMessage : BeingChangedMessage;

    /// <summary>
    /// Takes the operation lock if it is free and no scan runs.
    /// </summary>
    /// <param name="operation">What takes it (a job id and phase).</param>
    /// <param name="duringPostScan">True only for work inside the post-scan task, which runs while the scan still counts as
    /// running.</param>
    /// <returns>The lease, or null when another operation holds the lock or a scan runs.</returns>
    public OperationLease? TryEnter(string operation, bool duringPostScan = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);
        if ((!duringPostScan && _library.IsScanRunning) || !_operation.Wait(0))
        {
            return null;
        }

        return Lease(operation);
    }

    /// <summary>
    /// Waits for the operation lock (the worker's path; a scan is waited out by the caller).
    /// </summary>
    /// <param name="operation">What takes it.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The lease, or null after the timeout.</returns>
    public async Task<OperationLease?> EnterAsync(string operation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);
        return await _operation.WaitAsync(timeout, cancellationToken).ConfigureAwait(false) ? Lease(operation) : null;
    }

    /// <summary>
    /// Marks items busy for an operation, all or none: none is marked when any is held by another operation.
    /// </summary>
    /// <param name="owner">The feature.</param>
    /// <param name="operationId">The job or operation.</param>
    /// <param name="itemIds">The items (the empty id is ignored).</param>
    /// <param name="conflict">The first item another operation holds, when refused.</param>
    /// <returns>True when marked.</returns>
    public bool TryMarkBusy(OperationOwner owner, string operationId, IEnumerable<Guid> itemIds, out BusyItem? conflict)
    {
        ArgumentException.ThrowIfNullOrEmpty(operationId);
        ArgumentNullException.ThrowIfNull(itemIds);
        var ids = itemIds.Where(id => id != Guid.Empty).Distinct().ToList();
        lock (_gate)
        {
            conflict = ids.Select(id => _busy.GetValueOrDefault(id)).FirstOrDefault(b => b is not null && b.OperationId != operationId);
            if (conflict is not null)
            {
                return false;
            }

            foreach (var id in ids)
            {
                _busy[id] = new BusyItem(id, owner, operationId);
            }

            return true;
        }
    }

    /// <summary>
    /// Releases every item an operation holds (at its final status).
    /// </summary>
    /// <param name="operationId">The job or operation.</param>
    /// <returns>How many items were released.</returns>
    public int Release(string operationId)
    {
        lock (_gate)
        {
            var mine = _busy.Values.Where(b => b.OperationId == operationId).Select(b => b.ItemId).ToList();
            foreach (var id in mine)
            {
                _busy.Remove(id);
            }

            return mine.Count;
        }
    }

    /// <summary>
    /// Finds the first of some items that an operation holds.
    /// </summary>
    /// <param name="itemIds">The items.</param>
    /// <param name="exceptOperationId">An operation whose own items don't count (a job re-checking itself), or null.</param>
    /// <returns>The busy item, or null.</returns>
    public BusyItem? FindBusy(IEnumerable<Guid> itemIds, string? exceptOperationId = null)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        lock (_gate)
        {
            return itemIds.Select(id => _busy.GetValueOrDefault(id)).FirstOrDefault(b => b is not null && b.OperationId != exceptOperationId);
        }
    }

    /// <summary>
    /// Gets a snapshot of the busy set (Diagnostics).
    /// </summary>
    /// <returns>The busy items.</returns>
    public IReadOnlyList<BusyItem> Busy()
    {
        lock (_gate)
        {
            return _busy.Values.ToList();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _operation.Dispose();

    private OperationLease Lease(string operation)
    {
        Volatile.Write(ref _current, operation);
        return new OperationLease(operation, () =>
        {
            Volatile.Write(ref _current, null);
            _operation.Release();
        });
    }
}
