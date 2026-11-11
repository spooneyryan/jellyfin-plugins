namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// How <c>GET /Bookmarks/Query</c>, the flat list of the My Bookmarks page, orders bookmarks.
/// <c>GET /Bookmarks/Groups</c> has its own list (<see cref="BookmarkGroupSortBy"/>); a value of one list is a
/// 400 on the other endpoint.
/// </summary>
public enum BookmarkSortBy
{
    /// <summary>
    /// When the bookmark was last changed ("Recently changed", the default); newest first unless the order says otherwise.
    /// </summary>
    UpdatedAt,

    /// <summary>
    /// When the bookmark was made ("Recently created"); newest first unless the order says otherwise.
    /// </summary>
    CreatedAt,

    /// <summary>
    /// The bookmark's name ("Name A–Z"), ignoring case; unnamed bookmarks come after the named ones in either order.
    /// </summary>
    Name,

    /// <summary>
    /// The video's display name ("Title A–Z"), ignoring case, then the position in the video.
    /// </summary>
    ItemName,

    /// <summary>
    /// The position in the video ("Position in title"): the start, then the end of a range; most useful with <c>itemId</c>.
    /// </summary>
    Position
}
