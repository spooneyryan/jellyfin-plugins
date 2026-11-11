using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Grouping;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups;

/// <summary>
/// The grouper's results for the last snapshot: an LRU of <see cref="Capacity"/> results keyed
/// by the snapshot's version, the options' hash and the effective library ids in order, so paging, filtering and
/// re-sorting don't group again. Two callers share a result only when their effective libraries are the same, so one
/// admin's groups never reach an admin who can see other libraries. A newer snapshot clears it, when it is published
/// (<see cref="Retire"/>) or read, whichever comes first.
/// </summary>
internal sealed class GroupResultCache
{
    /// <summary>How many results it keeps.</summary>
    public const int Capacity = 8;

    private readonly object _gate = new();
    private readonly LinkedList<KeyValuePair<string, GroupingResult>> _results = new();
    private long _version;
    private long _hits;
    private long _misses;

    /// <summary>Gets how many results it holds.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _results.Count;
            }
        }
    }

    /// <summary>Gets how many reads it answered.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Gets how many reads grouped.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>
    /// Gets the cache's key for a result: the options' hash, the library ids, sorted, and the digest of the movies the
    /// caller can't see, when any (the snapshot's version is the cache's own).
    /// </summary>
    /// <param name="optionsHash">The options' hash.</param>
    /// <param name="libraryIds">The effective libraries.</param>
    /// <param name="hidden">The hidden movies' digest (<see cref="HiddenItems.Key"/>), or null for none.</param>
    /// <returns>The key.</returns>
    public static string Key(string optionsHash, IEnumerable<Guid> libraryIds, string? hidden = null)
        => optionsHash + "|" + string.Join(',', libraryIds.Distinct().Select(id => id.ToString("N", CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal))
            + (string.IsNullOrEmpty(hidden) ? string.Empty : "|hidden:" + hidden);

    /// <summary>
    /// Gets the result for a snapshot, the options and the libraries, grouping (outside the lock) when it holds none.
    /// </summary>
    /// <param name="version">The snapshot's version.</param>
    /// <param name="optionsHash">The options' hash (<see cref="GroupingOptions.Hash"/>).</param>
    /// <param name="libraryIds">The effective libraries.</param>
    /// <param name="group">Groups the snapshot.</param>
    /// <param name="hidden">The hidden movies' digest, or null for none.</param>
    /// <returns>The result.</returns>
    public GroupingResult GetOrAdd(long version, string optionsHash, IEnumerable<Guid> libraryIds, Func<GroupingResult> group, string? hidden = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        var key = Key(optionsHash, libraryIds, hidden);
        lock (_gate)
        {
            Advance(version);
            if (version == _version && Find(key) is { } node)
            {
                _results.Remove(node);
                _results.AddFirst(node);
                Interlocked.Increment(ref _hits);
                return node.Value.Value;
            }
        }

        Interlocked.Increment(ref _misses);
        var result = group();
        lock (_gate)
        {
            Advance(version);

            // A result for an older snapshot (a build ended meanwhile) is answered, never kept.
            if (version == _version && Find(key) is null)
            {
                _results.AddFirst(new KeyValuePair<string, GroupingResult>(key, result));
                while (_results.Count > Capacity)
                {
                    _results.RemoveLast();
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Drops every result of a snapshot older than <paramref name="version"/> (the index published it: nothing
    /// keeps an old snapshot alive until the next read).
    /// </summary>
    /// <param name="version">The new snapshot's version.</param>
    public void Retire(long version)
    {
        lock (_gate)
        {
            Advance(version);
        }
    }

    /// <summary>
    /// Drops every result (the title rules changed: their results can't be asked for any more).
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _results.Clear();
        }
    }

    // A newer snapshot makes every result old.
    private void Advance(long version)
    {
        if (version > _version)
        {
            _results.Clear();
            _version = version;
        }
    }

    private LinkedListNode<KeyValuePair<string, GroupingResult>>? Find(string key)
    {
        for (var node = _results.First; node is not null; node = node.Next)
        {
            if (string.Equals(node.Value.Key, key, StringComparison.Ordinal))
            {
                return node;
            }
        }

        return null;
    }
}
