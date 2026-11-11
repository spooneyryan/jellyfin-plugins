using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Grouping;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Normalization;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups;

/// <summary>
/// The entries' projections over every library (title key, year, edition, ids, bytes: <see cref="DuplicateGrouper.Project"/>)
/// for the current snapshot and title rules: normalising the titles is most
/// of a grouping's cost, and it is the same for every caller, mode and unknown-year policy, so it is done once per
/// snapshot; a caller's grouping narrows them (<see cref="GroupedEntry.Narrow"/>) and buckets. It holds one projection,
/// since the title rules come from the settings, never the request; a newer snapshot or a change of the rules replaces it.
/// </summary>
internal sealed class EntryProjectionCache
{
    private readonly object _gate = new();
    private long _version;
    private string? _hash;
    private IReadOnlyList<GroupedEntry>? _projected;
    private long _projections;
    private long _lastTicks = -1;

    /// <summary>Gets how many projections were made.</summary>
    public long Projections => Interlocked.Read(ref _projections);

    /// <summary>Gets how long the last projection took, or null before the first.</summary>
    public TimeSpan? LastProjection => Interlocked.Read(ref _lastTicks) is var ticks and >= 0 ? TimeSpan.FromTicks(ticks) : null;

    /// <summary>Gets a value indicating whether it holds a projection.</summary>
    public bool HasProjection
    {
        get
        {
            lock (_gate)
            {
                return _projected is not null;
            }
        }
    }

    /// <summary>
    /// Gets the projections of a snapshot under the title rules, projecting them (outside the lock) when it holds none.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="options">The title rules.</param>
    /// <param name="hash">The title rules' hash (<see cref="GroupingOptions.NormalizationHash"/>).</param>
    /// <param name="elapsed">How long the projection took, or null when it was kept.</param>
    /// <returns>The projections, in the snapshot's order.</returns>
    public IReadOnlyList<GroupedEntry> GetOrAdd(MovieIndexSnapshot snapshot, NormalizationOptions options, string hash, out TimeSpan? elapsed)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (_projected is { } kept && _version == snapshot.Version && string.Equals(_hash, hash, StringComparison.Ordinal))
            {
                elapsed = null;
                return kept;
            }
        }

        var watch = Stopwatch.StartNew();
        var projected = DuplicateGrouper.Project(snapshot.Entries, options);
        elapsed = watch.Elapsed;
        Interlocked.Increment(ref _projections);
        Interlocked.Exchange(ref _lastTicks, elapsed.Value.Ticks);
        lock (_gate)
        {
            // An older snapshot's projection (a build ended meanwhile) is answered, never kept.
            if (snapshot.Version >= _version)
            {
                _version = snapshot.Version;
                _hash = hash;
                _projected = projected;
            }
        }

        return projected;
    }

    /// <summary>
    /// Drops the projection when it is of a snapshot older than <paramref name="version"/>.
    /// </summary>
    /// <param name="version">The new snapshot's version.</param>
    public void Retire(long version)
    {
        lock (_gate)
        {
            if (version > _version)
            {
                _projected = null;
                _hash = null;
                _version = version;
            }
        }
    }

    /// <summary>
    /// Drops the projection (the title rules changed).
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _projected = null;
            _hash = null;
        }
    }
}
