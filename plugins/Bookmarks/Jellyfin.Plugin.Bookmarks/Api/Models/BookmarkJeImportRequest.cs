using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// Optional request body of <c>POST /Bookmarks/Admin/Import/JellyfinEnhanced</c>: which Jellyfin
/// Enhanced users' bookmarks to import. Without a body, or without <see cref="UserIds"/>, every user whose Jellyfin
/// Enhanced folder holds a <c>bookmark.json</c>.
/// </summary>
public sealed class BookmarkJeImportRequest
{
    /// <summary>
    /// Gets or sets the users (Jellyfin Enhanced names each user's folder after their Jellyfin user id): at most 1000, none
    /// empty; <c>null</c> or empty for every user.
    /// </summary>
    public IReadOnlyList<Guid>? UserIds { get; set; }
}
