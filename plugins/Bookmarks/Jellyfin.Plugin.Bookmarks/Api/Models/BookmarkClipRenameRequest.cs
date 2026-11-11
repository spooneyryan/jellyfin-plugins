using System;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// Request body for renaming a clip on the My Clips page, <c>POST /Bookmarks/Clips/{id}/Name</c>: the name of
/// its bookmark (or of a deleted bookmark's kept clip), and with <see cref="RenameFile"/> its clip file too, in one change:
/// either both are renamed or nothing is.
/// </summary>
public sealed class BookmarkClipRenameRequest
{
    /// <summary>
    /// Gets or sets the new name, or <c>null</c> (or white space) to leave it unnamed (its title is then the note's first line,
    /// else the file's name). The rules of <see cref="BookmarkCreateRequest.Name"/> apply: at most 100 characters.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the clip file is renamed to match: the name Bookmarks gives the clip now (the
    /// video's name, the new name, else the note, and the file's range), in the same folder, numbered when the name is taken.
    /// </summary>
    public bool RenameFile { get; set; }

    /// <summary>
    /// Gets or sets the clip's <see cref="BookmarkClipItemDto.UpdatedAt"/> as the client last read it: when it is set and the
    /// record was changed since (in another tab, say), nothing is renamed and the answer is 409.
    /// </summary>
    public DateTime? IfUpdatedAt { get; set; }
}
