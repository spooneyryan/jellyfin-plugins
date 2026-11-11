using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// The outcome of <c>JsonDocumentStore.RestoreSnapshotAsync</c> (<c>{RestoredFrom, SafetySnapshot}</c>).
/// </summary>
/// <param name="RestoredFrom">The snapshot's file name.</param>
/// <param name="SafetySnapshot">The pre-operation snapshot of the state it replaced, or <c>null</c>.</param>
/// <param name="SchemaVersion">The snapshot's schema version.</param>
/// <param name="MigratedFrom">The version it was upgraded from, or <c>null</c> when it was current.</param>
/// <param name="KeepSnapshotsBeforeUtc">
/// The time recorded with the snapshots kept since the data was last found missing or unreadable, if they are still kept
/// after this restore (the snapshot isn't one of them, so the data isn't back yet); <c>null</c> when none are kept.
/// </param>
internal sealed record SnapshotRestoreResult(string RestoredFrom, string? SafetySnapshot, int SchemaVersion, int? MigratedFrom, DateTime? KeepSnapshotsBeforeUtc = null)
{
    /// <summary>
    /// Describes the outcome with plain values.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["RestoredFrom"] = RestoredFrom,
        ["SafetySnapshot"] = SafetySnapshot,
        ["SchemaVersion"] = SchemaVersion,
        ["MigratedFrom"] = MigratedFrom,
        ["KeepSnapshotsBeforeUtc"] = KeepSnapshotsBeforeUtc
    };
}
