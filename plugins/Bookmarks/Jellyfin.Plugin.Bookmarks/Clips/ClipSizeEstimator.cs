using System;
using Jellyfin.Plugin.Bookmarks.Model;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The free-space guard's size estimate of a clip. It only has to reject jobs that are obviously doomed;
/// the watchdog is the real protection.
/// </summary>
/// <remarks>
/// <para><b>Fast</b> (stream copy): the source's bitrate × the clip's length / 8 × 1.05. The bitrate is the media source's
/// stated one, else its file size × 8 / its runtime; with neither known there is no estimate (<c>null</c>, which the guard
/// counts as nothing: the reserve and the watchdog still apply).</para>
/// <para><b>Accurate</b> (x264 CRF): a video budget by the output height (up to 576 lines 3 Mbit/s, up to 720 5, up to 1080
/// 10, above 25), capped at 1.2 × the source's video bitrate (its video stream's, else the whole source's), plus the audio
/// bitrate of the settings when the clip has audio, × the clip's length / 8 × 1.1. The output height is the source's, or
/// the settings' maximum height when the source is taller (as <see cref="FfmpegClipCommandBuilder"/> scales it); a source
/// of unknown height counts as the maximum height, else as 1080 lines. A source without a video stream has no video
/// budget.</para>
/// </remarks>
internal static class ClipSizeEstimator
{
    /// <summary>The factor on a Fast clip's size: container overhead and the keyframe before the start.</summary>
    public const double FastOverhead = 1.05;

    /// <summary>The factor on an Accurate clip's size: CRF bitrates vary around the budget.</summary>
    public const double AccurateOverhead = 1.1;

    /// <summary>The factor on the source's video bitrate that caps an Accurate clip's video budget.</summary>
    public const double SourceBitrateCap = 1.2;

    /// <summary>The height an Accurate clip of a source of unknown height is estimated at, without a maximum height.</summary>
    public const int UnknownHeight = 1080;

    /// <summary>
    /// Estimates a clip's size.
    /// </summary>
    /// <param name="command">The clip's command: mode, length, streams, source height and encoding settings.</param>
    /// <param name="source">The media source it is cut from.</param>
    /// <param name="video">The source's video stream, if it has one.</param>
    /// <returns>The estimate in bytes, or <c>null</c> for a Fast clip of a source whose bitrate isn't known.</returns>
    public static long? Estimate(ClipCommand command, MediaSourceInfo source, MediaStream? video)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(source);

        var seconds = command.Duration.TotalSeconds;
        if (seconds <= 0)
        {
            return 0;
        }

        if (command.Mode == BookmarkClipMode.Fast)
        {
            return SourceBitrate(source) is double bitrate ? Bytes(bitrate, seconds, FastOverhead) : null;
        }

        double videoBudget = 0;
        if (video is not null)
        {
            videoBudget = VideoBudget(OutputHeight(command.SourceHeight, command.Encoding.MaxHeight));
            var sourceVideo = video.BitRate is int streamRate && streamRate > 0 ? streamRate : SourceBitrate(source);
            if (sourceVideo is double cap)
            {
                videoBudget = Math.Min(videoBudget, SourceBitrateCap * cap);
            }
        }

        // With an explicit video map and no audio map the clip has no audio (FfmpegClipCommandBuilder's rule).
        var hasAudio = command.AudioStreamIndex is not null || command.VideoStreamIndex is null;
        var audio = hasAudio ? command.Encoding.AudioBitrateKbps * 1000.0 : 0;
        return Bytes(videoBudget + audio, seconds, AccurateOverhead);
    }

    /// <summary>
    /// Gets an Accurate clip's video budget for an output height, in bits per second.
    /// </summary>
    /// <param name="height">The output height in lines.</param>
    /// <returns>The budget.</returns>
    public static double VideoBudget(int height) => height switch
    {
        <= 576 => 3_000_000,
        <= 720 => 5_000_000,
        <= 1080 => 10_000_000,
        _ => 25_000_000
    };

    /// <summary>
    /// Gets an Accurate clip's output height: the source's, or the maximum height when the source is taller.
    /// </summary>
    /// <param name="sourceHeight">The source's height, if known.</param>
    /// <param name="maxHeight">The settings' maximum height; 0 keeps the source's.</param>
    /// <returns>The height in lines.</returns>
    public static int OutputHeight(int? sourceHeight, int maxHeight)
    {
        if (sourceHeight is not int height || height <= 0)
        {
            return maxHeight > 0 ? maxHeight : UnknownHeight;
        }

        return maxHeight > 0 && height > maxHeight ? maxHeight : height;
    }

    /// <summary>
    /// Gets a source's bitrate in bits per second: its stated one, else its size over its runtime.
    /// </summary>
    /// <param name="source">The media source.</param>
    /// <returns>The bitrate, or <c>null</c> when neither is known.</returns>
    public static double? SourceBitrate(MediaSourceInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Bitrate is int bitrate && bitrate > 0)
        {
            return bitrate;
        }

        if (source.Size is long size && size > 0 && source.RunTimeTicks is long ticks && ticks > 0)
        {
            return size * 8.0 / TimeSpan.FromTicks(ticks).TotalSeconds;
        }

        return null;
    }

    private static long Bytes(double bitsPerSecond, double seconds, double overhead)
    {
        var bytes = Math.Ceiling(bitsPerSecond * seconds / 8 * overhead);
        return bytes >= long.MaxValue ? long.MaxValue : (long)bytes;
    }
}
