using System;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>
/// A match as the Residual path keeps it once its batch is dropped: its id, sort name, presentation key,
/// its version-set links when it is in a set, and its captured sort keys (<see cref="MatchOrder"/>). A few hundred bytes,
/// so memory is one batch of items plus these rows, whatever the candidate count.
/// </summary>
/// <param name="Id">The item's id.</param>
/// <param name="SortName">Its sort name (the tiebreak).</param>
/// <param name="PresentationUniqueKey">Its presentation key, which core's <c>/Items</c> groups by for a user.</param>
/// <param name="Version">Its version-set member (<see cref="VersionMember.From"/>), or <c>null</c> for an item in no set.</param>
/// <param name="Keys">The captured sort keys, one per key of the order.</param>
internal sealed record MatchRow(Guid Id, string? SortName, string? PresentationUniqueKey, VersionMember? Version, object?[] Keys)
{
    /// <summary>Gets the member <c>VersionSets.Collapse</c> reads: the captured one, or the item alone.</summary>
    public VersionMember Member => Version ?? new VersionMember { Id = Id, SortName = SortName };
}
