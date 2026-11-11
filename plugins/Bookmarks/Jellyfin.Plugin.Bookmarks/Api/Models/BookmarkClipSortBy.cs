namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// How <c>GET /Bookmarks/Clips</c> orders the clips. Each has its own direction, as the page's
/// menu names it. Whatever the sort, the clips being made come first: the one running, then those
/// waiting in the order they were asked for.
/// </summary>
public enum BookmarkClipSortBy
{
    /// <summary>
    /// When the clip file was made, newest first (the default): its job's completion time, else the file's last write time
    /// (a kept clip whose time was lost), else when the clip was last asked for.
    /// </summary>
    Newest,

    /// <summary>
    /// The same time, oldest first.
    /// </summary>
    Oldest,

    /// <summary>
    /// The clip's title A–Z, ignoring case: the bookmark's name, else its note's first line, else the clip file's name;
    /// clips with none of them last.
    /// </summary>
    Name,

    /// <summary>
    /// The video's display name A–Z ("Title A–Z"), ignoring case, then where the clip starts in it.
    /// </summary>
    ItemName,

    /// <summary>
    /// The clip file's size, largest first; clips without a file last.
    /// </summary>
    Size,

    /// <summary>
    /// The clip's length, longest first.
    /// </summary>
    Duration
}
