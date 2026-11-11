using System;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// One user's clips in numbers, for the admins (<c>GET /Bookmarks/Admin/Clips/Summary</c>): aggregates
/// only, no clip's name, note or file.
/// </summary>
public sealed class BookmarkAdminClipUserDto
{
    /// <summary>
    /// Gets or sets the user's id.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the user's name on this server now, or <c>"Deleted user (&lt;id&gt;)"</c> for an id no user of this server has.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether no user of this server has the id.
    /// </summary>
    public bool UserDeleted { get; set; }

    /// <summary>
    /// Gets or sets how many clips the user has, those of deleted bookmarks included.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Gets or sets what the user's clip files take, in bytes, as the clips recorded their sizes (every file not known to be
    /// gone, as the user's own summary counts them).
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Gets or sets when the user's oldest clip was made (its job's end, else when it was asked for; UTC), or <c>null</c>.
    /// </summary>
    public DateTime? OldestUtc { get; set; }

    /// <summary>
    /// Gets or sets when the user's newest clip was made (UTC), or <c>null</c>.
    /// </summary>
    public DateTime? NewestUtc { get; set; }
}
