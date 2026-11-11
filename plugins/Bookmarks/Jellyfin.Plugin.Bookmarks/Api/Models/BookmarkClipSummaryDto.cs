namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// The caller's clips in numbers, for the My Clips page's summary line ("14 clips · 1.6 GB · 1 in progress"):
/// every clip of theirs, the clips of deleted bookmarks included, counted by the same states as the list.
/// </summary>
public sealed class BookmarkClipSummaryDto
{
    /// <summary>
    /// Gets or sets how many clips the caller has.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Gets or sets what their clip files take, in bytes, as the clips recorded their sizes: every file that isn't known to
    /// be gone (a file in a clip folder that is offline is counted, since it is probably still there).
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Gets or sets how many clips are waiting or being made.
    /// </summary>
    public int InProgress { get; set; }

    /// <summary>
    /// Gets or sets how many clips failed without a file.
    /// </summary>
    public int Failed { get; set; }

    /// <summary>
    /// Gets or sets how many clip files are gone from a clip folder that is there.
    /// </summary>
    public int Missing { get; set; }

    /// <summary>
    /// Gets or sets how many clips are in a clip folder that can't be reached (offline).
    /// </summary>
    public int FolderUnavailable { get; set; }
}
