namespace Jellyfin.Plugin.Bookmarks.Model;

/// <summary>
/// Lifecycle state of a clip extraction job.
/// </summary>
/// <remarks>
/// <c>ClipStatus</c> in Bookmarks 1.0, renamed in 1.1.0 for the schema prefix.
/// Its member names and values are unchanged; only its OpenAPI schema id changed.
/// </remarks>
public enum BookmarkClipStatus
{
    /// <summary>
    /// Waiting for a free encoder slot.
    /// </summary>
    Queued,

    /// <summary>
    /// ffmpeg is currently producing the clip.
    /// </summary>
    Running,

    /// <summary>
    /// The clip file was written successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Extraction failed; see the error message.
    /// </summary>
    Failed,

    /// <summary>
    /// The user cancelled the job before it finished.
    /// </summary>
    Cancelled
}
