using System;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The library's side of <see cref="ClipArtwork"/>: the local image files of a clip's video, and the refresh of a clip
/// that is a library item itself. <see cref="LibraryClipArtworkSource"/> is the real one; tests give their own.
/// </summary>
internal interface IClipArtworkSource
{
    /// <summary>
    /// Finds the artwork of a video: for a movie (or another video) its Primary image and its first Backdrop; for an
    /// episode its series' Primary image (the episode's own when the series has none), its own Primary image as the Thumb,
    /// and its first Backdrop, or else the series'. Only images stored as local files count, never a remote URL.
    /// </summary>
    /// <param name="itemId">The video's library id.</param>
    /// <returns>The images' paths, or <c>null</c> when the video isn't in the library.</returns>
    ClipArtworkImages? Find(Guid itemId);

    /// <summary>
    /// Asks Jellyfin to look at a clip's images again when the clip file is a library item (a clip folder that is a Home
    /// Videos library), so the new artwork shows without a scan; nothing when it isn't one yet (the next scan finds the
    /// files).
    /// </summary>
    /// <param name="clipPath">The clip file's full path.</param>
    /// <returns>Whether a refresh was queued.</returns>
    bool RefreshClip(string clipPath);
}
