namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A copy's video track.
/// </summary>
public sealed class LibraryMaintenanceDuplicateCompareVideoDto
{
    /// <summary>
    /// Gets or sets the width in pixels, or null.
    /// </summary>
    public int? Width { get; set; }

    /// <summary>
    /// Gets or sets the height in pixels, or null.
    /// </summary>
    public int? Height { get; set; }

    /// <summary>
    /// Gets or sets the resolution by width (2160p, 1440p, 1080p, 720p, SD), or null when the width is unknown.
    /// </summary>
    public string? ResolutionLabel { get; set; }

    /// <summary>
    /// Gets or sets the codec (<c>hevc</c>, <c>h264</c>), or null.
    /// </summary>
    public string? Codec { get; set; }

    /// <summary>
    /// Gets or sets the codec's profile (<c>Main 10</c>), or null.
    /// </summary>
    public string? Profile { get; set; }

    /// <summary>
    /// Gets or sets the bit depth, or null.
    /// </summary>
    public int? BitDepth { get; set; }

    /// <summary>
    /// Gets or sets the dynamic range: <c>SDR</c>, <c>HDR</c> or <c>Unknown</c>.
    /// </summary>
    public string VideoRange { get; set; } = "Unknown";

    /// <summary>
    /// Gets or sets its kind: <c>SDR</c>, <c>HDR10</c>, <c>HDR10Plus</c>, <c>HLG</c>, <c>DOVI</c> and the Dolby Vision
    /// combinations, or <c>Unknown</c>.
    /// </summary>
    public string VideoRangeType { get; set; } = "Unknown";

    /// <summary>
    /// Gets or sets the video track's bitrate, or null.
    /// </summary>
    public int? BitRate { get; set; }

    /// <summary>
    /// Gets or sets the frame rate (the average, else the real one), or null.
    /// </summary>
    public float? FrameRate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the video is interlaced.
    /// </summary>
    public bool IsInterlaced { get; set; }
}
