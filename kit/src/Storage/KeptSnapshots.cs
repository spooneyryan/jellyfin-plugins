using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// The snapshots a store's <c>&lt;file&gt;.keep-snapshots</c> marker keeps (by name, in the JSON store and the SQLite
/// database): the file names of the snapshots, daily and pre-operation, there were when the data was last found
/// missing, unreadable or damaged (<see cref="Names"/>), when they were recorded and how the data was found then
/// (<see cref="SinceUtc"/>, <see cref="Incident"/>; <c>null</c> in a marker of an earlier version), and the time an
/// earlier version reads (<see cref="BeforeUtc"/>, later than every one of them). <see cref="ReleaseBeforeUtc"/> is set
/// when the names were recorded later from a marker of an earlier version, by their dates: only one of them dated
/// before the incident then releases them, since one dated after it may have been taken since, while the clock was
/// behind. Both stores share it through <see cref="KeepSnapshotsMarker"/>.
/// </summary>
/// <typeparam name="TStatus">The store's status (<c>StoreStatus</c>, <c>SqliteDatabaseStatus</c>), for <see cref="Incident"/>.</typeparam>
/// <param name="BeforeUtc">The later of the incident and the newest kept snapshot's time plus a second, never moved back.</param>
/// <param name="SinceUtc">When the snapshots were recorded (the clock's time of the incident).</param>
/// <param name="Incident">How the data was found then, or <c>null</c> when the marker didn't say.</param>
/// <param name="Names">The kept snapshots' file names, any case.</param>
/// <param name="ReleaseBeforeUtc">For names recorded from an earlier version's marker: only a kept snapshot dated before this releases them.</param>
internal sealed record KeptSnapshots<TStatus>(DateTime BeforeUtc, DateTime SinceUtc, TStatus? Incident, HashSet<string> Names, DateTime? ReleaseBeforeUtc)
    where TStatus : struct, Enum
{
    /// <summary>
    /// Tells whether a snapshot is one of those kept.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns><c>true</c> when its name was recorded.</returns>
    public bool Keeps(StoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Names.Contains(snapshot.Name);
    }

    /// <summary>
    /// Tells whether restoring a snapshot brings the data back and so ends the protection: it is one of those kept (and, for
    /// names recorded from an earlier version's marker, dated before <see cref="ReleaseBeforeUtc"/>). One taken since never
    /// does, whatever its name's date (the clock may have been behind).
    /// </summary>
    /// <param name="snapshot">The snapshot restored.</param>
    /// <returns><c>true</c> when the kept snapshots rotate normally again.</returns>
    public bool ReleasedBy(StoreSnapshot snapshot)
        => Keeps(snapshot) && (ReleaseBeforeUtc is null || SnapshotRotation.IsBefore(snapshot, ReleaseBeforeUtc.Value));
}
