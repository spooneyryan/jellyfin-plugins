using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// Runs the real ffmpeg against a generated test video. Skipped when ffmpeg isn't installed.
/// </summary>
public sealed class FfmpegIntegrationTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly FfmpegRunner _runner = new(NullLogger<FfmpegRunner>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AccurateClip_IsFrameAccurateH264Mp4()
    {
        var tools = FfmpegTools.Require();
        var source = await CreateSourceAsync(tools, seconds: 12);
        var command = Command(source, "accurate.mp4", BookmarkClipMode.Accurate, start: 3.5, duration: 4);
        var progress = new List<double>();

        await _runner.RunAsync(tools.Ffmpeg, FfmpegClipCommandBuilder.Build(command), command.Duration, new ListProgress(progress), Ct);

        var probe = await tools.ProbeAsync(command.OutputPath, Ct);
        Assert.InRange(probe.Duration, 3.9, 4.15);
        Assert.Contains(("video", "h264"), probe.Streams);
        Assert.Contains(("audio", "aac"), probe.Streams);
        Assert.Equal(2, probe.Streams.Count);
        Assert.NotEmpty(progress);
        Assert.Equal(1, progress[^1]);
    }

    [Fact]
    public async Task FastClip_CopiesStreamsIntoMatroska()
    {
        var tools = FfmpegTools.Require();
        var source = await CreateSourceAsync(tools, seconds: 12);
        var command = Command(source, "fast.mkv", BookmarkClipMode.Fast, start: 3.5, duration: 4);

        await _runner.RunAsync(tools.Ffmpeg, FfmpegClipCommandBuilder.Build(command), command.Duration, null, Ct);

        var probe = await tools.ProbeAsync(command.OutputPath, Ct);

        // Stream copy starts on the keyframe before the requested start (keyframes are 2 seconds apart here).
        Assert.InRange(probe.Duration, 3.9, 6.1);
        Assert.Contains(("video", "h264"), probe.Streams);
        Assert.Contains(("audio", "aac"), probe.Streams);
    }

    [Fact]
    public async Task AccurateClip_Downscales()
    {
        var tools = FfmpegTools.Require();
        var source = await CreateSourceAsync(tools, seconds: 4);
        var command = Command(source, "small.mp4", BookmarkClipMode.Accurate, start: 0, duration: 2) with
        {
            SourceHeight = 240,
            Encoding = new ClipEncodingOptions { Preset = "ultrafast", MaxHeight = 120 }
        };

        await _runner.RunAsync(tools.Ffmpeg, FfmpegClipCommandBuilder.Build(command), command.Duration, null, Ct);

        Assert.True(File.Exists(command.OutputPath));
    }

    [Fact]
    public async Task MissingInput_ReportsFfmpegError()
    {
        var tools = FfmpegTools.Require();
        var command = Command(_directory.Combine("missing.mkv"), "out.mp4", BookmarkClipMode.Accurate, start: 0, duration: 1);

        var ex = await Assert.ThrowsAsync<ClipExtractionException>(
            () => _runner.RunAsync(tools.Ffmpeg, FfmpegClipCommandBuilder.Build(command), command.Duration, null, Ct));

        Assert.Contains("exit code", ex.Message, StringComparison.Ordinal);
        Assert.Contains("missing.mkv", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_StopsFfmpeg()
    {
        var tools = FfmpegTools.Require();
        var source = await CreateSourceAsync(tools, seconds: 20, size: "1280x720");
        var command = Command(source, "cancelled.mp4", BookmarkClipMode.Accurate, start: 0, duration: 20) with
        {
            Encoding = new ClipEncodingOptions { Preset = "veryslow" }
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var progress = new CallbackProgress(_ => cts.Cancel());

        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _runner.RunAsync(tools.Ffmpeg, FfmpegClipCommandBuilder.Build(command), command.Duration, progress, cts.Token));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20), "ffmpeg should be killed promptly");
    }

    [Fact]
    public async Task MissingExecutable_ReportsClipExtractionException()
    {
        var command = Command("in.mkv", "out.mp4", BookmarkClipMode.Accurate, start: 0, duration: 1);

        var ex = await Assert.ThrowsAsync<ClipExtractionException>(
            () => _runner.RunAsync(_directory.Combine("no-ffmpeg.exe"), FfmpegClipCommandBuilder.Build(command), command.Duration, null, Ct));

        Assert.Contains("could not be started", ex.Message, StringComparison.Ordinal);
    }

    public void Dispose() => _directory.Dispose();

    private async Task<string> CreateSourceAsync(FfmpegTools tools, int seconds, string size = "320x240")
    {
        var path = _directory.Combine($"source-{seconds}-{size}.mkv");
        await tools.RunAsync(
            [
                "-hide_banner", "-y",
                "-f", "lavfi", "-i", $"testsrc2=size={size}:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-c:v", "libx264", "-preset", "ultrafast", "-g", "50", "-pix_fmt", "yuv420p",
                "-c:a", "aac",
                path
            ],
            Ct);
        return path;
    }

    private ClipCommand Command(string source, string outputName, BookmarkClipMode mode, double start, double duration) => new()
    {
        InputPath = source,
        OutputPath = _directory.Combine(outputName),
        Mode = mode,
        Start = TimeSpan.FromSeconds(start),
        Duration = TimeSpan.FromSeconds(duration),
        VideoStreamIndex = 0,
        AudioStreamIndex = 1,
        Title = "Integration test clip",
        Encoding = new ClipEncodingOptions { Preset = "ultrafast" }
    };

    private sealed class ListProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value)
        {
            lock (values)
            {
                values.Add(value);
            }
        }
    }

    private sealed class CallbackProgress(Action<double> callback) : IProgress<double>
    {
        public void Report(double value) => callback(value);
    }
}
