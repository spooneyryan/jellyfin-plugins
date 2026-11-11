using System;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// One snapshot file of a store (<see cref="SnapshotRotation"/>).
/// </summary>
/// <param name="Name">The file name.</param>
/// <param name="Path">The full path.</param>
/// <param name="Utc">When it was taken, from its name (UTC, to the second).</param>
/// <param name="Sequence">0, or the suffix of a later snapshot taken in the same second.</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="Operation">For a pre-operation snapshot, its operation (<see cref="SnapshotOperations"/>); <c>null</c> for a daily one.</param>
internal sealed record StoreSnapshot(string Name, string Path, DateTime Utc, int Sequence, long Size, string? Operation = null);
