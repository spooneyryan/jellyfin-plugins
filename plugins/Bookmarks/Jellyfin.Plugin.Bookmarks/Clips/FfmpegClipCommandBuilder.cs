using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Bookmarks.Model;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Builds ffmpeg argument lists for clip extraction. Arguments are returned as a list so they can be
/// passed through <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> without manual quoting.
/// </summary>
internal static class FfmpegClipCommandBuilder
{
    /// <summary>
    /// Builds the argument list for a clip.
    /// </summary>
    /// <param name="command">The clip to extract.</param>
    /// <returns>The ffmpeg arguments, excluding the executable.</returns>
    public static IReadOnlyList<string> Build(ClipCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-loglevel",
            "error",
            "-nostats",
            "-progress",
            "pipe:1",

            // Seeking before -i is fast; when re-encoding ffmpeg still trims to the exact frame.
            "-ss",
            FormatSeconds(command.Start),
            "-i",
            "file:" + command.InputPath,
            "-t",
            FormatSeconds(command.Duration)
        };

        if (command.VideoStreamIndex is int videoIndex)
        {
            args.Add("-map");
            args.Add(FormatStreamSpecifier(videoIndex));
        }

        if (command.AudioStreamIndex is int audioIndex)
        {
            args.Add("-map");
            args.Add(FormatStreamSpecifier(audioIndex));
        }

        args.AddRange(["-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1"]);

        if (!string.IsNullOrWhiteSpace(command.Title))
        {
            args.Add("-metadata");
            args.Add("title=" + command.Title);
        }

        if (command.Mode == BookmarkClipMode.Fast)
        {
            args.AddRange(["-c", "copy", "-avoid_negative_ts", "make_zero"]);
            args.AddRange(["-max_muxing_queue_size", "1024", "-f", "matroska"]);
        }
        else
        {
            AddAccurateEncoding(args, command);
        }

        args.Add("file:" + command.OutputPath);
        return args;
    }

    /// <summary>
    /// Builds the video filter chain used when re-encoding.
    /// </summary>
    /// <param name="command">The clip to extract.</param>
    /// <returns>The value for ffmpeg's <c>-vf</c> option.</returns>
    public static string BuildVideoFilter(ClipCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var filters = new List<string>();
        if (command.IsInterlaced)
        {
            filters.Add("bwdif=0:-1:0");
        }

        var maxHeight = command.Encoding.MaxHeight;
        if (maxHeight > 0 && command.SourceHeight > maxHeight)
        {
            filters.Add(string.Create(CultureInfo.InvariantCulture, $"scale=-2:{maxHeight}"));
        }
        else
        {
            // x264 with 4:2:0 chroma needs even dimensions; this is a no-op for normal sources.
            filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
        }

        switch (command.Tonemap)
        {
            case TonemapMode.TonemapX:
                var peak = command.IsDolbyVision ? 400 : 100;
                filters.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"tonemapx=tonemap=bt2390:desat=0:peak={peak}:t=bt709:m=bt709:p=bt709:format=yuv420p"));
                break;
            case TonemapMode.ZScale:
                filters.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0:peak=100,zscale=t=bt709:m=bt709,format=yuv420p");
                break;
        }

        filters.Add("format=yuv420p");
        return string.Join(',', filters);
    }

    /// <summary>
    /// Formats a time as seconds for ffmpeg, using an invariant decimal point.
    /// </summary>
    /// <param name="time">The time.</param>
    /// <returns>The formatted seconds, with millisecond precision.</returns>
    public static string FormatSeconds(TimeSpan time)
        => Math.Max(0, time.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);

    private static void AddAccurateEncoding(List<string> args, ClipCommand command)
    {
        var encoding = command.Encoding;
        args.AddRange(
        [
            "-c:v",
            "libx264",
            "-preset",
            encoding.Preset,
            "-crf",
            encoding.Crf.ToString(CultureInfo.InvariantCulture),
            "-vf",
            BuildVideoFilter(command)
        ]);

        if (command.Tonemap != TonemapMode.None)
        {
            args.AddRange(["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709"]);
        }

        // With an explicit video map and no audio map the clip has no audio, so skip the audio options.
        var hasAudio = command.AudioStreamIndex is not null || command.VideoStreamIndex is null;
        if (hasAudio)
        {
            args.AddRange(
            [
                "-c:a",
                "aac",
                "-b:a",
                encoding.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture) + "k",
                "-ac",
                encoding.AudioChannels.ToString(CultureInfo.InvariantCulture)
            ]);
        }

        args.AddRange(["-max_muxing_queue_size", "1024", "-movflags", "+faststart", "-f", "mp4"]);
    }

    private static string FormatStreamSpecifier(int index)
        => "0:" + index.ToString(CultureInfo.InvariantCulture);
}
