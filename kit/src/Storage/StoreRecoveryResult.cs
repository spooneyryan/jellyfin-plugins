using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// The outcome of <c>JsonDocumentStore.RecoverAsync</c>: an admin's way out of
/// <see cref="StoreStatus.ReadOnlyDataMissing"/> or <see cref="StoreStatus.ReadOnlyDataUnreadable"/>, a chosen snapshot
/// restored or the data started empty (the SQLite database's <c>RecoverAsync(snapshot | null, startEmpty)</c>).
/// </summary>
/// <param name="RestoredFrom">The snapshot's file name, or <c>null</c> when the admin chose to start empty.</param>
/// <param name="SnapshotUtc">When that snapshot was taken.</param>
/// <param name="SchemaVersion">The snapshot's schema version, or <c>null</c> when the data started empty.</param>
/// <param name="MigratedFrom">The version the snapshot was upgraded from, or <c>null</c> when it was current.</param>
/// <param name="MovedAside">The names the files found in place of the data file and its <c>.bak</c> were moved to
/// (<c>&lt;file&gt;.corrupt-&lt;ts&gt;</c>), kept until an admin deletes them.</param>
/// <param name="KeepSnapshotsBeforeUtc">The time recorded with the kept snapshots from now on (the <c>.keep-snapshots</c>
/// marker, which names every snapshot there is), or <c>null</c> when there is none to keep.</param>
internal sealed record StoreRecoveryResult(
    string? RestoredFrom,
    DateTime? SnapshotUtc,
    int? SchemaVersion,
    int? MigratedFrom,
    IReadOnlyList<string> MovedAside,
    DateTime? KeepSnapshotsBeforeUtc)
{
    /// <summary>
    /// Gets a value indicating whether the admin chose to start empty.
    /// </summary>
    public bool StartedEmpty => RestoredFrom is null;

    /// <summary>
    /// Describes the outcome with plain values, for an endpoint.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["RestoredFrom"] = RestoredFrom,
        ["SnapshotUtc"] = SnapshotUtc,
        ["StartedEmpty"] = StartedEmpty,
        ["SchemaVersion"] = SchemaVersion,
        ["MigratedFrom"] = MigratedFrom,
        ["MovedAside"] = MovedAside,
        ["KeepSnapshotsBeforeUtc"] = KeepSnapshotsBeforeUtc
    };
}
