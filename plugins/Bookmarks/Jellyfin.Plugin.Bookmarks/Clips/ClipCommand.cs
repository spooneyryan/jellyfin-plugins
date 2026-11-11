using System;
using Jellyfin.Plugin.Bookmarks.Model;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Everything needed to build the ffmpeg command line for one clip.
/// </summary>
internal sealed record ClipCommand
{
    /// <summary>
    /// Gets the source media file.
    /// </summary>
    public required string InputPath { get; init; }

    /// <summary>
    /// Gets the file ffmpeg writes to.
    /// </summary>
    public required string OutputPath { get; init; }

    /// <summary>
    /// Gets the extraction mode.
    /// </summary>
    public required BookmarkClipMode Mode { get; init; }

    /// <summary>
    /// Gets the clip start position in the source.
    /// </summary>
    public required TimeSpan Start { get; init; }

    /// <summary>
    /// Gets the clip length.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Gets the ffmpeg input index of the video stream, or <c>null</c> to let ffmpeg choose.
    /// </summary>
    public int? VideoStreamIndex { get; init; }

    /// <summary>
    /// Gets the ffmpeg input index of the audio stream, or <c>null</c> for none (or ffmpeg's choice when the video index is unknown too).
    /// </summary>
    public int? AudioStreamIndex { get; init; }

    /// <summary>
    /// Gets a value indicating whether the source video is interlaced.
    /// </summary>
    public bool IsInterlaced { get; init; }

    /// <summary>
    /// Gets the source video height in pixels, when known.
    /// </summary>
    public int? SourceHeight { get; init; }

    /// <summary>
    /// Gets the HDR to SDR conversion to apply.
    /// </summary>
    public TonemapMode Tonemap { get; init; }

    /// <summary>
    /// Gets a value indicating whether the source is Dolby Vision without an HDR10 fallback layer.
    /// </summary>
    public bool IsDolbyVision { get; init; }

    /// <summary>
    /// Gets the title written into the clip's metadata.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets the encoder settings used in <see cref="BookmarkClipMode.Accurate"/> mode.
    /// </summary>
    public ClipEncodingOptions Encoding { get; init; } = new();
}
