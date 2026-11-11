using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// Every user's clips in numbers, for the admins (<c>GET /Bookmarks/Admin/Clips/Summary</c>): per user
/// and in all, aggregates only. Not gated by <c>AdminsCanManageAllClips</c> (it names no clip); the My Clips page's owner
/// filter lists its users.
/// </summary>
public sealed class BookmarkAdminClipSummaryDto
{
    /// <summary>
    /// Gets or sets each user with at least one clip, by name (deleted users last).
    /// </summary>
    public IReadOnlyList<BookmarkAdminClipUserDto> Users { get; set; } = [];

    /// <summary>
    /// Gets or sets how many clips there are in all.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Gets or sets what every clip file takes, in bytes.
    /// </summary>
    public long TotalBytes { get; set; }
}
