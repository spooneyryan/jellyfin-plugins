using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// The items the plugin itself is adding to or removing from a collection through the server's collection calls, which
/// fire the same events as a person's edit: the edit listener ignores the events of those items in that collection while
/// they are in here, and records any other item's.
/// </summary>
internal sealed class CollectionWriteSuppression
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Dictionary<Guid, int>> _writing = [];

    /// <summary>Marks items of a collection as being written until the returned handle is disposed.</summary>
    /// <param name="collectionId">The collection's id.</param>
    /// <param name="itemIds">The items the write adds or removes.</param>
    /// <returns>The handle.</returns>
    public IDisposable Begin(Guid collectionId, IReadOnlyCollection<Guid> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var ids = itemIds.Distinct().ToList();
        lock (_lock)
        {
            if (ids.Count > 0 && !_writing.TryGetValue(collectionId, out _))
            {
                _writing[collectionId] = [];
            }

            foreach (var id in ids)
            {
                var items = _writing[collectionId];
                items[id] = items.GetValueOrDefault(id) + 1;
            }
        }

        return new Handle(this, collectionId, ids);
    }

    /// <summary>Tells whether the plugin is writing items of a collection.</summary>
    /// <param name="collectionId">The collection's id.</param>
    /// <returns>Whether some of its events are the plugin's own.</returns>
    public bool IsWriting(Guid collectionId)
    {
        lock (_lock)
        {
            return _writing.ContainsKey(collectionId);
        }
    }

    /// <summary>The items of an event that the plugin isn't writing: a person's edits.</summary>
    /// <param name="collectionId">The collection's id.</param>
    /// <param name="itemIds">The event's items.</param>
    /// <returns>Those the plugin isn't writing.</returns>
    public IReadOnlyCollection<Guid> NotWriting(Guid collectionId, IReadOnlyCollection<Guid> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        lock (_lock)
        {
            return _writing.TryGetValue(collectionId, out var items) ? itemIds.Where(id => !items.ContainsKey(id)).ToList() : itemIds;
        }
    }

    private void End(Guid collectionId, List<Guid> ids)
    {
        lock (_lock)
        {
            if (!_writing.TryGetValue(collectionId, out var items))
            {
                return;
            }

            foreach (var id in ids)
            {
                if (items.TryGetValue(id, out var count))
                {
                    if (count <= 1)
                    {
                        items.Remove(id);
                    }
                    else
                    {
                        items[id] = count - 1;
                    }
                }
            }

            if (items.Count == 0)
            {
                _writing.Remove(collectionId);
            }
        }
    }

    private sealed class Handle(CollectionWriteSuppression owner, Guid collectionId, List<Guid> ids) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.End(collectionId, ids);
            }
        }
    }
}
