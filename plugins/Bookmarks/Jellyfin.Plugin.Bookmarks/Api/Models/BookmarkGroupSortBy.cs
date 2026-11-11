namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// How <c>GET /Bookmarks/Groups</c>, the grouped view of the My Bookmarks page, orders its groups, one per video.
/// Inside a group bookmarks are always ordered by position. <c>GET /Bookmarks/Query</c> has its
/// own list (<see cref="BookmarkSortBy"/>); a value of one list is a 400 on the other endpoint.
/// </summary>
public enum BookmarkGroupSortBy
{
    /// <summary>
    /// The latest change of a bookmark in the group ("Recently changed", the default); newest first unless the order says
    /// otherwise.
    /// </summary>
    LatestUpdated,

    /// <summary>
    /// The video's display name ("Title A–Z"), ignoring case.
    /// </summary>
    ItemName,

    /// <summary>
    /// How many bookmarks the group has ("Most bookmarks"); most first unless the order says otherwise.
    /// </summary>
    Count
}
