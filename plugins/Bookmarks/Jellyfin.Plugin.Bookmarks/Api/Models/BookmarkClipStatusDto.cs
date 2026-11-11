using System;
using Jellyfin.Plugin.Bookmarks.Model;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// The state of one clip, for the My Clips page's poll of the rows being made (<c>GET /Bookmarks/Clips/Status</c>): one
/// request for every visible row in progress, instead of one per bookmark.
/// </summary>
public sealed class BookmarkClipStatusDto
{
    /// <summary>
    /// Gets or sets the bookmark's id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the status of the most recent job.
    /// </summary>
    public BookmarkClipStatus Status { get; set; }

    /// <summary>
    /// Gets or sets what the clip is right now.
    /// </summary>
    public BookmarkClipState State { get; set; }

    /// <summary>
    /// Gets or sets the progress of a running job, from 0 to 100.
    /// </summary>
    public double? Progress { get; set; }

    /// <summary>
    /// Gets or sets the reason the most recent job failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets the name of the current clip file, which may come from an earlier job.
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// Gets or sets the size of the current clip file in bytes.
    /// </summary>
    public long? FileSize { get; set; }
}
