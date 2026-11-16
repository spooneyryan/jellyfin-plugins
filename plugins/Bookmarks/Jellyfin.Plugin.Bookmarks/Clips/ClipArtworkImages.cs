namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The local image files a clip's artwork is copied from (<see cref="IClipArtworkSource.Find"/>); each may be missing.
/// </summary>
/// <param name="Poster">The image for <c>-poster</c> (Primary).</param>
/// <param name="Fanart">The image for <c>-fanart</c> (Backdrop).</param>
/// <param name="Thumb">The image for <c>-thumb</c> (Thumb): an episode's own Primary image.</param>
internal sealed record ClipArtworkImages(string? Poster, string? Fanart, string? Thumb)
{
    /// <summary>
    /// Gets the image for a kind (<see cref="ClipArtworkNames.Kinds"/>).
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>Its image's path, or <c>null</c>.</returns>
    public string? For(string kind) => kind switch
    {
        ClipArtworkNames.Poster => Poster,
        ClipArtworkNames.Fanart => Fanart,
        ClipArtworkNames.Thumb => Thumb,
        _ => null
    };
}
