using System;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// Request body for naming or renaming a bookmark, <c>POST /Bookmarks/{id}/Name</c> (new in 1.1):
/// only the name changes. Every rename is a change, even to the same name: it clears the "Relinked, check position"
/// badge (the web client's "Looks right" renames to the same name).
/// </summary>
public sealed class BookmarkRenameRequest
{
    /// <summary>
    /// Gets or sets the new name, or <c>null</c> (or white space) to leave the bookmark unnamed. The rules of
    /// <see cref="BookmarkCreateRequest.Name"/> apply.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the bookmark's <see cref="BookmarkDto.UpdatedAt"/> as the client last read it: when it is set and the
    /// bookmark was changed since (in another tab, say), nothing is written and the answer is 409.
    /// </summary>
    public DateTime? IfUpdatedAt { get; set; }
}
