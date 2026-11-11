using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// Request body of <c>POST /Bookmarks/BulkDelete</c>: the bookmarks selected on the My Bookmarks
/// page.
/// </summary>
public sealed class BookmarkBulkDeleteRequest
{
    /// <summary>
    /// Gets or sets the ids of the caller's bookmarks to delete: 1 to 200 (400 otherwise); an id given twice counts once.
    /// </summary>
    public IReadOnlyList<Guid>? Ids { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether their clip files go too. Without it, a bookmark with a clip file is kept as a
    /// clip-only tombstone that keeps the file, as <c>DELETE /Bookmarks/{id}</c> does.
    /// </summary>
    public bool DeleteClips { get; set; }
}
