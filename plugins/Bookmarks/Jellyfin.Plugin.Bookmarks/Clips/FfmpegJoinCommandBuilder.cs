using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Builds the ffmpeg command of a playlist export (since 1.5.0 / 2.3.0): every video an input of one ffmpeg run, joined end
/// to end by the <c>concat</c> filter into one H.264/AAC MP4 with the clip encoding settings (<see cref="ClipEncodingOptions"/>).
/// </summary>
/// <remarks>
/// <para>Each video is read for its length only (<c>-t</c> before its <c>-i</c>) and made alike before the join: its video
/// deinterlaced and tone-mapped as an Accurate clip's would be (<see cref="FfmpegClipCommandBuilder"/>), scaled to fit the
/// export's frame keeping its shape and padded with black (letterbox or pillarbox), square pixels, the export's frame rate
/// (<c>fps</c>), 4:2:0, then held on its last frame and cut to exactly its length (<c>tpad</c>, <c>trim</c>); its audio
/// (the stream Jellyfin would play) resampled to 48 kHz stereo (mono with a 1-channel clip setting), padded with silence
/// and cut to exactly its length (<c>apad</c>, <c>atrim</c>); a video without audio gets that much silence
/// (<c>anullsrc</c>). So every part's picture and sound are the same length, and the joined file stays in sync to the
/// end, its length the sum of the parts'.</para>
/// <para>The filter graph is read from a file (<c>-/filter_complex</c>, ffmpeg 7 and later), so a long playlist doesn't
/// make the command line too long; the inputs' paths stay on it (<see cref="PlaylistExportPlanner.MaxItems"/>). Subtitles,
/// chapters and the inputs' metadata are left out; the title is the playlist's name.</para>
/// </remarks>
internal static class FfmpegJoinCommandBuilder
{
    /// <summary>The joined audio's sample rate: 48 kHz.</summary>
    public const int SampleRate = 48000;

    /// <summary>
    /// Builds the arguments.
    /// </summary>
    /// <param name="items">The videos, in playlist order.</param>
    /// <param name="format">The joined file's frame and frame rate.</param>
    /// <param name="encoding">The clip encoding settings.</param>
    /// <param name="filterScriptPath">The file the filter graph (<see cref="BuildFilterGraph"/>) is written to.</param>
    /// <param name="outputPath">The file ffmpeg writes (the export's temporary file).</param>
    /// <param name="title">The title written into the file, or <c>null</c>.</param>
    /// <returns>The arguments.</returns>
    public static IReadOnlyList<string> Build(
        IReadOnlyList<PlaylistExportItem> items,
        PlaylistExportFormat format,
        ClipEncodingOptions encoding,
        string filterScriptPath,
        string outputPath,
        string? title)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentException.ThrowIfNullOrEmpty(filterScriptPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        if (items.Count == 0)
        {
            throw new ArgumentException("An export needs at least one video.", nameof(items));
        }

        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-nostats", "-progress", "pipe:1" };
        foreach (var item in items)
        {
            args.AddRange(["-t", FfmpegClipCommandBuilder.FormatSeconds(item.Duration), "-i", "file:" + item.InputPath]);
        }

        args.AddRange(["-/filter_complex", filterScriptPath, "-map", "[v]", "-map", "[a]"]);
        args.AddRange(["-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1"]);
        if (!string.IsNullOrWhiteSpace(title))
        {
            args.Add("-metadata");
            args.Add("title=" + title);
        }

        args.AddRange(["-c:v", "libx264", "-preset", encoding.Preset, "-crf", encoding.Crf.ToString(CultureInfo.InvariantCulture)]);
        if (items.Any(i => i.Tonemap != TonemapMode.None))
        {
            args.AddRange(["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709"]);
        }

        args.AddRange(
        [
            "-c:a",
            "aac",
            "-b:a",
            encoding.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture) + "k",
            "-ac",
            AudioChannels(encoding).ToString(CultureInfo.InvariantCulture),
            "-max_muxing_queue_size",
            "4096",
            "-movflags",
            "+faststart",
            "-f",
            "mp4",
            "file:" + outputPath
        ]);
        return args;
    }

    /// <summary>
    /// Builds the filter graph: each video's picture and sound made alike (<see cref="FfmpegJoinCommandBuilder"/>'s remarks),
    /// then joined by <c>concat</c> into <c>[v]</c> and <c>[a]</c>.
    /// </summary>
    /// <param name="items">The videos, in playlist order; input <c>i</c> is the <c>i</c>-th.</param>
    /// <param name="format">The joined file's frame and frame rate.</param>
    /// <param name="encoding">The clip encoding settings (the audio's channels).</param>
    /// <returns>The filter graph, one chain a line.</returns>
    public static string BuildFilterGraph(IReadOnlyList<PlaylistExportItem> items, PlaylistExportFormat format, ClipEncodingOptions encoding)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(encoding);
        var layout = AudioChannels(encoding) == 1 ? "mono" : "stereo";
        var audioFormat = string.Create(CultureInfo.InvariantCulture, $"aformat=sample_fmts=fltp:sample_rates={SampleRate}:channel_layouts={layout}");
        var graph = new StringBuilder();
        var join = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var duration = FfmpegClipCommandBuilder.FormatSeconds(item.Duration);
            graph.Append(CultureInfo.InvariantCulture, $"[{i}:{item.VideoStreamIndex}]")
                .Append(VideoChain(item, format, duration))
                .Append(CultureInfo.InvariantCulture, $"[v{i}];\n");
            if (item.AudioStreamIndex is int audio)
            {
                graph.Append(CultureInfo.InvariantCulture, $"[{i}:{audio}]aresample={SampleRate},{audioFormat},asetpts=PTS-STARTPTS,apad=whole_dur={duration},atrim=duration={duration}[a{i}];\n");
            }
            else
            {
                graph.Append(CultureInfo.InvariantCulture, $"anullsrc=channel_layout={layout}:sample_rate={SampleRate},atrim=duration={duration},{audioFormat}[a{i}];\n");
            }

            join.Append(CultureInfo.InvariantCulture, $"[v{i}][a{i}]");
        }

        graph.Append(join).Append(CultureInfo.InvariantCulture, $"concat=n={items.Count}:v=1:a=1[v][a]\n");
        return graph.ToString();
    }

    /// <summary>
    /// The joined audio's channels: 1 when the clip setting is mono, else 2 (stereo).
    /// </summary>
    /// <param name="encoding">The clip encoding settings.</param>
    /// <returns>1 or 2.</returns>
    public static int AudioChannels(ClipEncodingOptions encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return encoding.AudioChannels == 1 ? 1 : 2;
    }

    // One video's picture: from its start, deinterlaced and tone-mapped as a clip's, fitted into the frame and padded, square
    // pixels, the export's frame rate, 4:2:0, held on its last frame and cut to exactly its length.
    private static string VideoChain(PlaylistExportItem item, PlaylistExportFormat format, string duration)
    {
        var filters = new List<string> { "setpts=PTS-STARTPTS" };
        if (item.IsInterlaced)
        {
            filters.Add("bwdif=0:-1:0");
        }

        // Square pixels first: an anamorphic picture (a DVD's 720x480 at 16:9) is widened to its shape before it is fitted.
        filters.Add("scale=trunc(iw*sar/2)*2:ih");
        filters.Add("setsar=1");
        filters.Add(string.Create(CultureInfo.InvariantCulture, $"scale={format.Width}:{format.Height}:force_original_aspect_ratio=decrease:force_divisible_by=2"));
        switch (item.Tonemap)
        {
            case TonemapMode.TonemapX:
                var peak = item.IsDolbyVision ? 400 : 100;
                filters.Add(string.Create(CultureInfo.InvariantCulture, $"tonemapx=tonemap=bt2390:desat=0:peak={peak}:t=bt709:m=bt709:p=bt709:format=yuv420p"));
                break;
            case TonemapMode.ZScale:
                filters.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0:peak=100,zscale=t=bt709:m=bt709,format=yuv420p");
                break;
        }

        filters.Add("format=yuv420p");
        filters.Add(string.Create(CultureInfo.InvariantCulture, $"pad={format.Width}:{format.Height}:(ow-iw)/2:(oh-ih)/2:color=black"));
        filters.Add("setsar=1");
        filters.Add("fps=" + format.FrameRateText);
        filters.Add("tpad=stop_mode=clone:stop_duration=" + duration);
        filters.Add("trim=duration=" + duration);
        filters.Add("setpts=PTS-STARTPTS");
        return string.Join(',', filters);
    }
}
