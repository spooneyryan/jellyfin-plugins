using System;
using System.Threading;
using Jellyfin.Plugin.AccessRoles.Storage;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// The <c>EffectiveRulesCache</c>, its lock part: the <see cref="LockSnapshot"/> of the store's current
/// document, rebuilt when the document, the store's status or its Degraded mode changes (the document is immutable, so a reference compare
/// tells), and built synchronously on the filter's first use, without waiting for the startup gate or the library catalog
/// (no unprotected window after a start). Lock-free; two requests racing a rebuild build the same snapshot.
/// </summary>
internal sealed class EffectiveRulesCache
{
    private readonly AccessRolesStore _store;
    private Entry? _entry;
    private int _builds;

    /// <summary>
    /// Initializes a new instance of the <see cref="EffectiveRulesCache"/> class.
    /// </summary>
    /// <param name="store">The store.</param>
    public EffectiveRulesCache(AccessRolesStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>Gets how many snapshots were built in this process.</summary>
    public int Builds => Volatile.Read(ref _builds);

    /// <summary>
    /// Gets the snapshot of the store's current document; the store loads on first use.
    /// </summary>
    /// <returns>The snapshot.</returns>
    /// <remarks>A store that can't be read throws; the filter then fails closed.</remarks>
    public LockSnapshot Current()
    {
        var document = _store.Current;
        var status = _store.Status;
        var degraded = _store.Degraded.Current;
        var entry = Volatile.Read(ref _entry);
        if (entry is not null && ReferenceEquals(entry.Document, document) && entry.Snapshot.Status == status && ReferenceEquals(entry.Snapshot.Degraded, degraded))
        {
            return entry.Snapshot;
        }

        var snapshot = LockSnapshot.Build(document, status, degraded);
        Volatile.Write(ref _entry, new Entry(document, snapshot));
        Interlocked.Increment(ref _builds);
        return snapshot;
    }

    private sealed record Entry(AccessRolesDocument Document, LockSnapshot Snapshot);
}
