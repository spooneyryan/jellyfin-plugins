using System;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// Request body for renaming a clip's file to the name Bookmarks gives the clip now, <c>POST /Bookmarks/Clips/{id}/RenameFile</c>
/// (the My Clips page's Rename file): optional, so a request without a body still works.
/// </summary>
public sealed class BookmarkClipRenameFileRequest
{
    /// <summary>
    /// Gets or sets the clip's <see cref="BookmarkClipItemDto.UpdatedAt"/> as the client last read it: when it is set and the
    /// clip was renamed (or changed otherwise) since, in another tab say, nothing is renamed and the answer is 409, rather than
    /// a file name other than the one the client showed.
    /// </summary>
    public DateTime? IfUpdatedAt { get; set; }
}
