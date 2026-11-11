using Jellyfin.Plugin.Bookmarks.Model;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// Optional request body for starting a clip extraction.
/// </summary>
/// <remarks>
/// <c>CreateClipRequest</c> in Bookmarks 1.0, renamed in 1.1.0 for the schema prefix.
/// Its JSON is unchanged; only its OpenAPI schema id changed.
/// </remarks>
public sealed class BookmarkClipRequest
{
    /// <summary>
    /// Gets or sets the extraction mode, or <c>null</c> for the server default.
    /// </summary>
    public BookmarkClipMode? Mode { get; set; }
}
