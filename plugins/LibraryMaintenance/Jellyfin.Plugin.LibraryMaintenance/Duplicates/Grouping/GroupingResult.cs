using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Grouping;

/// <summary>
/// What a grouping pass gives: the groups, and the entries and movies it looked at.
/// </summary>
/// <param name="Groups">The groups, by key, then id.</param>
/// <param name="Entries">The entries in scope with a title.</param>
/// <param name="Movies">Their members in scope.</param>
/// <param name="NoTitle">The entries in scope left out because their name gives an empty key.</param>
internal sealed record GroupingResult(IReadOnlyList<DuplicateGroup> Groups, int Entries, int Movies, int NoTitle)
{
    /// <summary>Gets the number of groups the other mode makes of the same buckets (<c>AlternateModeGroupCount</c>).</summary>
    public int AlternateModeGroupCount { get; init; }
}
