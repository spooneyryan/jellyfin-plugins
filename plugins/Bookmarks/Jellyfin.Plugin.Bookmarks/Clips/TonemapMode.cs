namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// How HDR video is converted to SDR when re-encoding a clip.
/// </summary>
internal enum TonemapMode
{
    /// <summary>
    /// No tone mapping (SDR source, or no suitable filter available).
    /// </summary>
    None,

    /// <summary>
    /// jellyfin-ffmpeg's <c>tonemapx</c> filter, which also handles Dolby Vision.
    /// </summary>
    TonemapX,

    /// <summary>
    /// The stock ffmpeg <c>zscale</c> + <c>tonemap</c> chain.
    /// </summary>
    ZScale
}
