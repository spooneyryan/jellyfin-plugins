using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// One snapshot as <c>GET /&lt;Route&gt;/Admin/Snapshots</c> lists it (<c>[{Name, Utc, Size, SchemaVersion}]</c>, plus
/// its class, its operation, and whether it is kept since the data was last found missing or unreadable).
/// </summary>
/// <param name="Name">The file name, which <c>Snapshots/Restore?name=</c> takes.</param>
/// <param name="Kind">Daily or pre-operation.</param>
/// <param name="Operation">For a pre-operation snapshot, what it was taken before.</param>
/// <param name="Utc">When it was taken.</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="SchemaVersion">The schema version in the file, or <c>null</c> when it can't be read.</param>
/// <param name="Kept">Whether rotation keeps it: it was there when the data was last found missing or unreadable (the
/// <c>.keep-snapshots</c> marker records it by name), until one of those kept is restored.</param>
internal sealed record StoreSnapshotInfo(string Name, SnapshotKind Kind, string? Operation, DateTime Utc, long Size, int? SchemaVersion, bool Kept)
{
    /// <summary>
    /// Describes the snapshot with plain values, for an endpoint or Diagnostics.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Name"] = Name,
        ["Kind"] = Kind.ToString(),
        ["Operation"] = Operation,
        ["Utc"] = Utc,
        ["Size"] = Size,
        ["SchemaVersion"] = SchemaVersion,
        ["Kept"] = Kept
    };
}
