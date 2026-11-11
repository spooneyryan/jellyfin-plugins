using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// One merged version set, or one standalone video, as <see cref="VersionSets.Group"/> builds it. Each video of the
/// input is a member of exactly one set, so a broken set neither vanishes nor counts twice.
/// </summary>
internal sealed record VersionSet
{
    /// <summary>
    /// Gets the set's representative: the root, found by following <c>PrimaryVersionId</c> from any member until it is
    /// empty, names an item that doesn't exist, or repeats (then the smallest id of the circle).
    /// </summary>
    public required Guid RootId { get; init; }

    /// <summary>
    /// Gets every member, the root first: every item whose pointer chain reaches the root, plus every live item the root
    /// lists that no other set holds by its pointers (a standalone video the root lists). Items found only through the
    /// lookup (a primary in another library) are members too; <see cref="ExternalIds"/> names them.
    /// </summary>
    public required IReadOnlyList<Guid> MemberIds { get; init; }

    /// <summary>
    /// Gets the members that weren't in the input (found through the lookup: a root or a chained item outside it).
    /// </summary>
    public IReadOnlyList<Guid> ExternalIds { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the root points at a primary that doesn't exist: an orphaned secondary, which is
    /// its own representative (nothing clears <c>PrimaryVersionId</c> when a primary is deleted on 10.11.5).
    /// </summary>
    public bool IsOrphaned { get; init; }

    /// <summary>
    /// Gets the <c>PrimaryVersionId</c> text an orphaned root points at, or null.
    /// </summary>
    public string? MissingPrimaryVersionId { get; init; }

    /// <summary>
    /// Gets the root's links that resolve to no item (neither the cached id nor the path finds one).
    /// </summary>
    public IReadOnlyList<VersionLink> StaleLinks { get; init; } = [];

    /// <summary>
    /// Gets the cached ids of <see cref="StaleLinks"/> (those that have one other than the empty GUID).
    /// </summary>
    public IReadOnlyList<Guid> StaleLinkIds { get; init; } = [];

    /// <summary>
    /// Gets the members whose <c>PrimaryVersionId</c> names another member instead of the root: native "Group versions"
    /// leaves the members it folds in pointing at their old primary (<c>VideosController.cs:220-247</c>).
    /// </summary>
    public IReadOnlyList<Guid> ChainedIds { get; init; } = [];

    /// <summary>
    /// Gets the members whose pointer chain reaches the root while the root doesn't list them: hidden or unplayable as a
    /// version, since the sources come from the primary's list (<c>Video.cs:533-563</c>).
    /// </summary>
    public IReadOnlyList<Guid> UnlistedIds { get; init; } = [];

    /// <summary>
    /// Gets the live items the root lists whose pointer chain doesn't reach it (empty, or pointing elsewhere). A standalone
    /// one is a member of this set; one that belongs to another set by its pointers stays there and is named here too
    /// (claimed by two sets).
    /// </summary>
    public IReadOnlyList<Guid> ListedNotPointingIds { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the pointers go round in a circle.
    /// </summary>
    public bool HasCycle { get; init; }

    /// <summary>
    /// Gets the set's states as flags.
    /// </summary>
    public VersionSetState State
        => (IsOrphaned ? VersionSetState.Orphaned : VersionSetState.None)
            | (StaleLinks.Count > 0 ? VersionSetState.StaleLinks : VersionSetState.None)
            | (ChainedIds.Count > 0 ? VersionSetState.Chained : VersionSetState.None)
            | (UnlistedIds.Count > 0 ? VersionSetState.Unlisted : VersionSetState.None)
            | (ListedNotPointingIds.Count > 0 ? VersionSetState.ListedNotPointing : VersionSetState.None)
            | (HasCycle ? VersionSetState.Cycle : VersionSetState.None);

    /// <summary>
    /// Gets a value indicating whether the set has more than one member.
    /// </summary>
    public bool IsMerged => MemberIds.Count > 1;
}
