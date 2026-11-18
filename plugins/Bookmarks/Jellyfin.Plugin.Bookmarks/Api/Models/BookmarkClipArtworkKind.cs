namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// A kind of clip artwork file (since 1.3.0 / 2.1.0 a clip in a Home Videos library gets its video's images beside it,
/// named <c>&lt;clip&gt;-poster</c>, <c>-fanart</c> and <c>-thumb</c>): the image a video's page shows for a clip.
/// </summary>
public enum BookmarkClipArtworkKind
{
    /// <summary>
    /// <c>&lt;clip&gt;-poster.&lt;ext&gt;</c>: the video's (or, for an episode, its series') Primary image.
    /// </summary>
    Poster,

    /// <summary>
    /// <c>&lt;clip&gt;-fanart.&lt;ext&gt;</c>: the video's Backdrop.
    /// </summary>
    Fanart,

    /// <summary>
    /// <c>&lt;clip&gt;-thumb.&lt;ext&gt;</c>: an episode's own Primary image.
    /// </summary>
    Thumb
}
