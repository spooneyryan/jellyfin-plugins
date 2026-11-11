namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// The <c>availability</c> filter of <c>GET /Bookmarks/Query</c> and <c>/Groups</c>. It
/// works on the stored state only: whether the bookmark is detached because its video left the library, which
/// the identity service keeps current. It never asks the library, so filtering and counting cost no lookups; whether the
/// viewer may still open the video is <c>BookmarkDto.ItemAvailable</c> instead, which is no filter.
/// </summary>
public enum BookmarkAvailability
{
    /// <summary>
    /// Every bookmark (the default).
    /// </summary>
    All,

    /// <summary>
    /// Bookmarks whose video is in the library (not detached).
    /// </summary>
    Available,

    /// <summary>
    /// Bookmarks whose video left the library ("Not in library": detached).
    /// </summary>
    Missing
}
