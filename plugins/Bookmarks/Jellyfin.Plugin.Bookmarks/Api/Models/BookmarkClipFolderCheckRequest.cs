namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// Request body of <c>POST /Bookmarks/Admin/ClipFolder/Check</c> (since Bookmarks 1.2.1): the Clip folder field's value.
/// </summary>
public sealed class BookmarkClipFolderCheckRequest
{
    /// <summary>
    /// Gets or sets the folder as typed; empty for the default folder.
    /// </summary>
    public string? Path { get; set; }
}
