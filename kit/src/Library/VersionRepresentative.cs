using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// One row of <see cref="VersionSets.Collapse"/>: the item that stands for a version set some of whose members matched.
/// </summary>
internal sealed record VersionRepresentative
{
    /// <summary>
    /// Gets the representative: the set's root when it is eligible, otherwise the first eligible matching member.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets the version set.
    /// </summary>
    public required VersionSet Set { get; init; }

    /// <summary>
    /// Gets the members that matched, in the caller's order.
    /// </summary>
    public required IReadOnlyList<Guid> MatchedIds { get; init; }

    /// <summary>
    /// Gets a value indicating whether the representative is the set's root (primary).
    /// </summary>
    public bool IsRoot => Id == Set.RootId;
}
