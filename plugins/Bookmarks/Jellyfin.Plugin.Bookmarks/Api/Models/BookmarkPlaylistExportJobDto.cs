using System;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// A playlist export of the caller's (since 1.5.0 / 2.3.0): waiting, being made, or finished since the server started.
/// </summary>
public sealed class BookmarkPlaylistExportJobDto
{
    /// <summary>
    /// Gets or sets the export's id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the playlist's id.
    /// </summary>
    public Guid PlaylistId { get; set; }

    /// <summary>
    /// Gets or sets the playlist's name.
    /// </summary>
    public string PlaylistName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where it is.
    /// </summary>
    public BookmarkPlaylistExportState State { get; set; }

    /// <summary>
    /// Gets or sets how far it is, 0 to 100, while it is being made.
    /// </summary>
    public double? Progress { get; set; }

    /// <summary>
    /// Gets or sets why it failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets the file's name (never its folder): the planned one until it is made.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file's size, once made.
    /// </summary>
    public long? FileSize { get; set; }

    /// <summary>
    /// Gets or sets how many videos it joins.
    /// </summary>
    public int VideoCount { get; set; }

    /// <summary>
    /// Gets or sets its length, in ticks.
    /// </summary>
    public long DurationTicks { get; set; }

    /// <summary>
    /// Gets or sets its estimated size, in bytes.
    /// </summary>
    public long EstimatedBytes { get; set; }

    /// <summary>
    /// Gets or sets when it was asked for (UTC).
    /// </summary>
    public DateTime RequestedAt { get; set; }

    /// <summary>
    /// Gets or sets when it finished (UTC).
    /// </summary>
    public DateTime? CompletedAt { get; set; }
}
