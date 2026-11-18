namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// A playlist entry an export leaves out (since 1.5.0 / 2.3.0).
/// </summary>
public sealed class BookmarkPlaylistExportSkippedDto
{
    /// <summary>
    /// Gets or sets the item's name, or <c>null</c> for one the caller may not see or one no longer in the library.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets why it is left out, in a few words.
    /// </summary>
    public string Reason { get; set; } = string.Empty;
}
