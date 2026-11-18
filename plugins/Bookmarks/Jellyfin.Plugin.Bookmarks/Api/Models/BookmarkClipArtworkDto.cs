namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// One artwork file beside a clip file, served by <c>GET /Bookmarks/Clips/{id}/Artwork/{kind}</c>.
/// </summary>
public sealed class BookmarkClipArtworkDto
{
    /// <summary>
    /// Gets or sets which artwork it is.
    /// </summary>
    public BookmarkClipArtworkKind Kind { get; set; }

    /// <summary>
    /// Gets or sets a tag that changes with the file (its last write time), for the image's URL, so a browser's copy is
    /// replaced when the file is.
    /// </summary>
    public string Tag { get; set; } = string.Empty;
}
