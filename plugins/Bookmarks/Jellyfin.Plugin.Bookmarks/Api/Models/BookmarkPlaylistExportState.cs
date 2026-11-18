namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// Where a playlist export is (since 1.5.0 / 2.3.0).
/// </summary>
public enum BookmarkPlaylistExportState
{
    /// <summary>
    /// Waiting for the export before it to finish (one is made at a time).
    /// </summary>
    Queued,

    /// <summary>
    /// Being made.
    /// </summary>
    Running,

    /// <summary>
    /// Made: the file is in the clip folder.
    /// </summary>
    Completed,

    /// <summary>
    /// Not made; <see cref="BookmarkPlaylistExportJobDto.Error"/> says why. Nothing was left in the clip folder.
    /// </summary>
    Failed,

    /// <summary>
    /// Cancelled; its unfinished file was deleted.
    /// </summary>
    Cancelled
}
