using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;

/// <summary>
/// One version set of the index, the unit Duplicates compares: a merged set is one entry, never its own
/// duplicate. A caller sees only the members in the libraries they can open (<see cref="EntryScope"/>).
/// </summary>
/// <param name="Representative">The set's root, or, when the root isn't indexed (a primary in a library the settings
/// leave out, or an excluded folder), its indexed member with the lowest id.</param>
/// <param name="Members">The set's indexed members, the representative first, then by id.</param>
/// <param name="SourceCount">The versions Jellyfin plays from the set: every live member (indexed or not) plus the
/// root's local alternates. Stale links don't count, unlike the native <c>MediaSourceCount</c>.</param>
/// <param name="State">The set's broken states; any other than <c>None</c> flags the entry "Broken merge".</param>
/// <param name="StaleLinkIds">The cached ids of the root's links that resolve to nothing.</param>
/// <param name="ProblemMemberIds">The chained, unlisted and listed-not-pointing members.</param>
/// <param name="LibraryIds">The libraries of the indexed members.</param>
/// <param name="AllIds">Every member id, indexed or not.</param>
/// <param name="RootId">The set's root.</param>
/// <param name="ExternalIds">The members that aren't indexed.</param>
internal sealed record VersionEntry(
    MovieRecord Representative,
    IReadOnlyList<MovieRecord> Members,
    int SourceCount,
    VersionSetState State,
    IReadOnlyList<Guid> StaleLinkIds,
    IReadOnlyList<Guid> ProblemMemberIds,
    IReadOnlyList<Guid> LibraryIds,
    IReadOnlySet<Guid> AllIds,
    Guid RootId,
    IReadOnlyList<Guid> ExternalIds)
{
    /// <summary>Gets the members that point at another member instead of the root (<see cref="VersionSetState.Chained"/>).</summary>
    public IReadOnlyList<Guid> ChainedIds { get; init; } = [];

    /// <summary>Gets the members the root doesn't list (<see cref="VersionSetState.Unlisted"/>).</summary>
    public IReadOnlyList<Guid> UnlistedIds { get; init; } = [];

    /// <summary>Gets the items the root lists that don't point back (<see cref="VersionSetState.ListedNotPointing"/>).</summary>
    public IReadOnlyList<Guid> ListedNotPointingIds { get; init; } = [];
}
