using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What a playlist export is expected to take (since 1.5.0 / 2.3.0): its file's size, which the free-space guard checks and
/// keeps as clip jobs' estimates are, and the time to make it, both shown before it starts.
/// </summary>
/// <remarks>
/// <para><b>Size</b>, as Accurate clips are estimated (<see cref="ClipSizeEstimator"/>): for each video, the budget of the
/// joined file's height (3, 5, 10 or 25 Mbit/s for up to 576, 720, 1080 lines or more), at most 1.2 × the video's own bitrate
/// when it isn't scaled up, plus the clip audio bitrate (a silent video's silence too), × its length, + 10 %.</para>
/// <para><b>Time</b>, a rough guide: x264 at the clip preset <c>veryfast</c> on a typical server CPU is taken to encode
/// 576, 720 and 1080 lines at 12, 6 and 3 × real time (1.2 × above), scaled by the preset (<c>ultrafast</c> 3 ×,
/// <c>superfast</c> 2 ×, <c>faster</c> 0.7 ×, <c>fast</c> 0.5 ×, <c>medium</c> 0.4 ×, <c>slow</c> 0.25 ×,
/// <c>slower</c> 0.12 ×, <c>veryslow</c> 0.06 ×), and halved for a video that is tone-mapped from HDR, + 10 % for joining and
/// writing.</para>
/// </remarks>
internal static class PlaylistExportEstimator
{
    /// <summary>Above this estimate an export is called very large: 20 GB.</summary>
    public const long LargeBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>Above this length an export is called very large: 3 hours.</summary>
    public static readonly TimeSpan LargeDuration = TimeSpan.FromHours(3);

    /// <summary>
    /// Estimates the joined file's size.
    /// </summary>
    /// <param name="items">The videos.</param>
    /// <param name="format">The joined file's format.</param>
    /// <param name="encoding">The clip encoding settings.</param>
    /// <returns>The size in bytes.</returns>
    public static long EstimateBytes(IReadOnlyList<PlaylistExportItem> items, PlaylistExportFormat format, ClipEncodingOptions encoding)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(encoding);
        var budget = ClipSizeEstimator.VideoBudget(format.Height);
        var audio = encoding.AudioBitrateKbps * 1000.0;
        double bits = 0;
        foreach (var item in items)
        {
            var video = budget;
            if (item.SourceBitrate is double source && source > 0 && (item.Height is not int height || height >= format.Height))
            {
                video = Math.Min(video, ClipSizeEstimator.SourceBitrateCap * source);
            }

            bits += (video + audio) * Math.Max(0, item.Duration.TotalSeconds);
        }

        // Rounded first, so a float's last digit doesn't add a byte.
        var bytes = Math.Ceiling(Math.Round(bits / 8 * ClipSizeEstimator.AccurateOverhead, 3));
        return bytes >= long.MaxValue ? long.MaxValue : (long)bytes;
    }

    /// <summary>
    /// Estimates the time to make the joined file.
    /// </summary>
    /// <param name="items">The videos.</param>
    /// <param name="format">The joined file's format.</param>
    /// <param name="encoding">The clip encoding settings.</param>
    /// <returns>The time.</returns>
    public static TimeSpan EstimateTime(IReadOnlyList<PlaylistExportItem> items, PlaylistExportFormat format, ClipEncodingOptions encoding)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(encoding);
        var speed = Speed(format.Height) * PresetFactor(encoding.Preset);
        double seconds = 0;
        foreach (var item in items)
        {
            var itemSpeed = item.Tonemap == TonemapMode.None ? speed : speed / 2;
            seconds += Math.Max(0, item.Duration.TotalSeconds) / itemSpeed;
        }

        return TimeSpan.FromSeconds(Math.Ceiling(Math.Round(seconds * 1.1, 3)));
    }

    /// <summary>
    /// Whether an export is very large: longer than <see cref="LargeDuration"/> or estimated over <see cref="LargeBytes"/>.
    /// </summary>
    /// <param name="duration">Its length.</param>
    /// <param name="estimatedBytes">Its estimated size.</param>
    /// <returns>Whether the dialog warns.</returns>
    public static bool IsLarge(TimeSpan duration, long estimatedBytes) => duration > LargeDuration || estimatedBytes > LargeBytes;

    /// <summary>
    /// The encoding speed taken for a height at the preset <c>veryfast</c>, in multiples of real time.
    /// </summary>
    /// <param name="height">The joined file's height.</param>
    /// <returns>The speed.</returns>
    public static double Speed(int height) => height switch
    {
        <= 576 => 12,
        <= 720 => 6,
        <= 1080 => 3,
        _ => 1.2
    };

    /// <summary>
    /// The speed of an x264 preset next to <c>veryfast</c>.
    /// </summary>
    /// <param name="preset">The preset.</param>
    /// <returns>The factor.</returns>
    public static double PresetFactor(string? preset) => preset switch
    {
        "ultrafast" => 3,
        "superfast" => 2,
        "faster" => 0.7,
        "fast" => 0.5,
        "medium" => 0.4,
        "slow" => 0.25,
        "slower" => 0.12,
        "veryslow" => 0.06,
        _ => 1
    };
}
