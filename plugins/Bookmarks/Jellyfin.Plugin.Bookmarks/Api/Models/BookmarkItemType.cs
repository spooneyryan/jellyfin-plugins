namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// The <c>itemTypes</c> filter of <c>GET /Bookmarks/Query</c> and <c>/Groups</c>, by the kind the
/// bookmark's identity snapshot stores (<c>BookmarkDto.ItemType</c>).
/// </summary>
public enum BookmarkItemType
{
    /// <summary>
    /// Bookmarks on movies.
    /// </summary>
    Movie,

    /// <summary>
    /// Bookmarks on episodes.
    /// </summary>
    Episode,

    /// <summary>
    /// Bookmarks on any other video: home videos, music videos, trailers, and videos of an unknown kind.
    /// </summary>
    Other
}
