using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// Request body of <c>POST /Bookmarks/Clips/BulkDelete</c>: the clips selected
/// on the My Clips page.
/// </summary>
public sealed class BookmarkClipBulkDeleteRequest
{
    /// <summary>
    /// Gets or sets the ids of the caller's clips (their bookmarks' ids, deleted bookmarks' too) whose clips to delete: 1 to
    /// 200 (400 otherwise); an id given twice counts once.
    /// </summary>
    public IReadOnlyList<Guid>? Ids { get; set; }
}
