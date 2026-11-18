using System;
using System.Linq;
using Jellyfin.Plugin.Bookmarks.Clips;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The playlist export's ffmpeg command (since 1.5.0 / 2.3.0): one input per video, each read for its length only; a filter
/// graph (in a file) that brings every picture to one size, shape kept with black bars, square pixels, one frame rate,
/// 4:2:0, and every sound to 48 kHz stereo (silence for a video without sound), each cut to exactly its video's length so
/// sound and picture stay together, then joined in order; H.264 and AAC with the clip settings, into an MP4.
/// </summary>
public sealed class FfmpegJoinCommandBuilderTests
{
    private static readonly PlaylistExportFormat Format = new(1280, 720, 24000, 1001);

    private static readonly PlaylistExportItem Small = Item(@"C:\media\small.mkv", 3, 640, 360) with { AudioStreamIndex = 1 };

    private static readonly PlaylistExportItem Silent = Item(@"C:\media\silent.mp4", 2, 320, 240) with { AudioStreamIndex = null };

    private static readonly PlaylistExportItem Large = Item(@"C:\media\large [1].mkv", 4.5, 1280, 720) with { VideoStreamIndex = 1, AudioStreamIndex = 2 };

    [Fact]
    public void TheCommand_ReadsEachVideoForItsLength_InOrder_AndEncodesWithTheClipSettings()
    {
        var encoding = new ClipEncodingOptions { Preset = "faster", Crf = 23, AudioBitrateKbps = 160 };

        var args = FfmpegJoinCommandBuilder.Build([Small, Silent, Large], Format, encoding, @"C:\temp\graph.txt", @"D:\clips\Trip [00.00.00-00.00.09] (x).mp4.partial", "Trip");

        Assert.Equal(
            [
                "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-nostats", "-progress", "pipe:1",
                "-t", "3", "-i", @"file:C:\media\small.mkv",
                "-t", "2", "-i", @"file:C:\media\silent.mp4",
                "-t", "4.5", "-i", @"file:C:\media\large [1].mkv",
                "-/filter_complex", @"C:\temp\graph.txt", "-map", "[v]", "-map", "[a]",
                "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1",
                "-metadata", "title=Trip",
                "-c:v", "libx264", "-preset", "faster", "-crf", "23",
                "-c:a", "aac", "-b:a", "160k", "-ac", "2",
                "-max_muxing_queue_size", "4096", "-movflags", "+faststart",
                "-f", "mp4", @"file:D:\clips\Trip [00.00.00-00.00.09] (x).mp4.partial"
            ],
            args);
    }

    [Fact]
    public void TheGraph_FitsEveryPictureIntoOneFrame_AtOneRate_AndCutsItToItsLength()
    {
        var graph = FfmpegJoinCommandBuilder.BuildFilterGraph([Small, Silent, Large], Format, new ClipEncodingOptions());
        var lines = graph.TrimEnd('\n').Split('\n');

        Assert.Equal(7, lines.Length);
        Assert.Equal(
            "[0:0]setpts=PTS-STARTPTS,scale=trunc(iw*sar/2)*2:ih,setsar=1,scale=1280:720:force_original_aspect_ratio=decrease:force_divisible_by=2,format=yuv420p,"
            + "pad=1280:720:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=24000/1001,tpad=stop_mode=clone:stop_duration=3,trim=duration=3,setpts=PTS-STARTPTS[v0];",
            lines[0]);

        // The third video's picture is its second stream; every video is cut to its own length.
        Assert.StartsWith("[2:1]setpts=PTS-STARTPTS,", lines[4], StringComparison.Ordinal);
        Assert.EndsWith("trim=duration=4.5,setpts=PTS-STARTPTS[v2];", lines[4], StringComparison.Ordinal);
        Assert.Equal("[v0][a0][v1][a1][v2][a2]concat=n=3:v=1:a=1[v][a]", lines[6]);
    }

    [Fact]
    public void TheSound_Is48kHzStereo_PaddedAndCutToTheVideosLength_AndSilenceForAVideoWithout()
    {
        var graph = FfmpegJoinCommandBuilder.BuildFilterGraph([Small, Silent, Large], Format, new ClipEncodingOptions()).Split('\n');

        Assert.Equal(
            "[0:1]aresample=48000,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,asetpts=PTS-STARTPTS,apad=whole_dur=3,atrim=duration=3[a0];",
            graph[1]);
        Assert.Equal(
            "anullsrc=channel_layout=stereo:sample_rate=48000,atrim=duration=2,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[a1];",
            graph[3]);
        Assert.StartsWith("[2:2]aresample=48000,", graph[5], StringComparison.Ordinal);
    }

    [Fact]
    public void MonoClipSettings_MakeAMonoExport()
    {
        var mono = new ClipEncodingOptions { AudioChannels = 1 };

        var graph = FfmpegJoinCommandBuilder.BuildFilterGraph([Small, Silent], Format, mono);
        var args = FfmpegJoinCommandBuilder.Build([Small, Silent], Format, mono, "graph.txt", "out.mp4.partial", null);

        Assert.Contains("channel_layouts=mono", graph, StringComparison.Ordinal);
        Assert.Contains("anullsrc=channel_layout=mono", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("stereo", graph, StringComparison.Ordinal);
        Assert.Equal("1", args[args.ToList().IndexOf("-ac") + 1]);
        Assert.DoesNotContain("-metadata", args);
    }

    [Fact]
    public void AnInterlacedVideo_IsDeinterlaced_AndAnHdrOne_ToneMapped_ToBt709()
    {
        var interlaced = Small with { IsInterlaced = true };
        var hdr = Large with { Tonemap = TonemapMode.TonemapX };
        var dolbyVision = Large with { Tonemap = TonemapMode.TonemapX, IsDolbyVision = true };

        var graph = FfmpegJoinCommandBuilder.BuildFilterGraph([interlaced, hdr, dolbyVision], Format, new ClipEncodingOptions()).Split('\n');
        var args = FfmpegJoinCommandBuilder.Build([interlaced, hdr], Format, new ClipEncodingOptions(), "graph.txt", "out.mp4.partial", null);
        var plain = FfmpegJoinCommandBuilder.Build([Small], Format, new ClipEncodingOptions(), "graph.txt", "out.mp4.partial", null);

        Assert.StartsWith("[0:0]setpts=PTS-STARTPTS,bwdif=0:-1:0,", graph[0], StringComparison.Ordinal);
        Assert.Contains("tonemapx=tonemap=bt2390:desat=0:peak=100:", graph[2], StringComparison.Ordinal);
        Assert.Contains("tonemapx=tonemap=bt2390:desat=0:peak=400:", graph[4], StringComparison.Ordinal);
        Assert.DoesNotContain("tonemap", graph[0], StringComparison.Ordinal);
        Assert.Contains("-colorspace", args);
        Assert.DoesNotContain("-colorspace", plain);
    }

    [Fact]
    public void AnExportWithoutAVideo_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => FfmpegJoinCommandBuilder.Build([], Format, new ClipEncodingOptions(), "graph.txt", "out.mp4.partial", null));
    }

    private static PlaylistExportItem Item(string path, double seconds, int width, int height) => new()
    {
        ItemId = Guid.NewGuid(),
        Name = System.IO.Path.GetFileNameWithoutExtension(path),
        InputPath = path,
        Duration = TimeSpan.FromSeconds(seconds),
        VideoStreamIndex = 0,
        Width = width,
        Height = height,
        FrameRate = 25
    };
}
