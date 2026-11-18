using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// What exporting a playlist to the clip folder would make (since 1.5.0 / 2.3.0,
/// <c>GET /Bookmarks/Playlists/{playlistId}/Export</c>): shown in the export dialog before it starts.
/// </summary>
public sealed class BookmarkPlaylistExportEstimateDto
{
    /// <summary>
    /// Gets or sets the playlist's id.
    /// </summary>
    public Guid PlaylistId { get; set; }

    /// <summary>
    /// Gets or sets the playlist's name.
    /// </summary>
    public string PlaylistName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many videos are joined.
    /// </summary>
    public int VideoCount { get; set; }

    /// <summary>
    /// Gets or sets the entries left out, and why, in playlist order.
    /// </summary>
    public IReadOnlyList<BookmarkPlaylistExportSkippedDto> Skipped { get; set; } = [];

    /// <summary>
    /// Gets or sets the joined file's length, in ticks: the sum of the videos'.
    /// </summary>
    public long DurationTicks { get; set; }

    /// <summary>
    /// Gets or sets the joined file's width in pixels.
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    /// Gets or sets the joined file's height in pixels.
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// Gets or sets the joined file's frame rate.
    /// </summary>
    public double FrameRate { get; set; }

    /// <summary>
    /// Gets or sets the joined file's estimated size, in bytes.
    /// </summary>
    public long EstimatedBytes { get; set; }

    /// <summary>
    /// Gets or sets the estimated time to make it, in seconds (a rough guide).
    /// </summary>
    public double EstimatedSeconds { get; set; }

    /// <summary>
    /// Gets or sets the file name it would get (made unique again when it is written).
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether it is very large (over 3 hours, or estimated over 20 GB): the dialog warns.
    /// </summary>
    public bool IsLarge { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it can be started now.
    /// </summary>
    public bool CanExport { get; set; }

    /// <summary>
    /// Gets or sets why it can't, or <c>null</c>: no videos, too many, not enough free space, the clip folder away.
    /// </summary>
    public string? Problem { get; set; }
}
