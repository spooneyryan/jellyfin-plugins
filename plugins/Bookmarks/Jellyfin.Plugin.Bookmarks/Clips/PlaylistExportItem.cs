using System;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// One video of a playlist export (since 1.5.0 / 2.3.0): its file, the streams taken from it, its length and what the
/// joined file's format is chosen from (<see cref="PlaylistExportFormat"/>).
/// </summary>
internal sealed record PlaylistExportItem
{
    /// <summary>Gets the library item's id.</summary>
    public required Guid ItemId { get; init; }

    /// <summary>Gets the item's name, as the export dialog lists it.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the video file's path.</summary>
    public required string InputPath { get; init; }

    /// <summary>Gets how much of the file is used: all of it (its runtime).</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Gets the video stream's index among the file's streams (ffmpeg's <c>i:N</c>).</summary>
    public required int VideoStreamIndex { get; init; }

    /// <summary>Gets the audio stream's index among the file's streams, or <c>null</c> for a video without audio (silence
    /// then).</summary>
    public int? AudioStreamIndex { get; init; }

    /// <summary>Gets the video's width in pixels, when known.</summary>
    public int? Width { get; init; }

    /// <summary>Gets the video's height in pixels, when known.</summary>
    public int? Height { get; init; }

    /// <summary>Gets the video's frame rate, when known.</summary>
    public double? FrameRate { get; init; }

    /// <summary>Gets a value indicating whether the video is interlaced (deinterlaced as clips are).</summary>
    public bool IsInterlaced { get; init; }

    /// <summary>Gets the HDR tone mapping, as clips choose it.</summary>
    public TonemapMode Tonemap { get; init; }

    /// <summary>Gets a value indicating whether the video is Dolby Vision (the tone mapping's peak).</summary>
    public bool IsDolbyVision { get; init; }

    /// <summary>Gets the video stream's bitrate, or the file's, in bits per second, when known (the size estimate's cap).</summary>
    public double? SourceBitrate { get; init; }
}
