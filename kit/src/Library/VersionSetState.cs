using System;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// The broken states of a merged version set (Library Maintenance's <c>VersionState</c>), as
/// <see cref="VersionSets.Group"/> finds them. On Jellyfin 10.11.5 nothing repairs them: deleting a primary leaves its
/// secondaries pointing at nothing, and native "Group versions" leaves chains.
/// </summary>
[Flags]
internal enum VersionSetState
{
    /// <summary>A healthy set, or a standalone video.</summary>
    None = 0,

    /// <summary>The root points at a primary that doesn't exist (<see cref="VersionSet.IsOrphaned"/>).</summary>
    Orphaned = 1,

    /// <summary>The root lists links that resolve to no item (<see cref="VersionSet.StaleLinks"/>).</summary>
    StaleLinks = 2,

    /// <summary>A member points at another member instead of the root (<see cref="VersionSet.ChainedIds"/>).</summary>
    Chained = 4,

    /// <summary>A member points at the set but the root doesn't list it (<see cref="VersionSet.UnlistedIds"/>).</summary>
    Unlisted = 8,

    /// <summary>The root lists an item whose pointer doesn't lead to it (<see cref="VersionSet.ListedNotPointingIds"/>).</summary>
    ListedNotPointing = 16,

    /// <summary>The pointers go round in a circle (<see cref="VersionSet.HasCycle"/>).</summary>
    Cycle = 32
}
