namespace Jellyfin.Plugin.Bookmarks.Model;

/// <summary>
/// How a clip is produced from the source media.
/// </summary>
/// <remarks>
/// <c>ClipMode</c> in Bookmarks 1.0, renamed in 1.1.0 for the schema prefix.
/// Its member names and values are unchanged; only its OpenAPI schema id changed.
/// </remarks>
public enum BookmarkClipMode
{
    /// <summary>
    /// Re-encode to H.264/AAC in an MP4 container. Frame accurate and plays almost anywhere, but costs CPU time.
    /// </summary>
    Accurate,

    /// <summary>
    /// Copy the original streams into a Matroska container. Nearly instant, but the clip starts on the
    /// keyframe at or before the requested start, so it can begin a few seconds early.
    /// </summary>
    Fast
}
