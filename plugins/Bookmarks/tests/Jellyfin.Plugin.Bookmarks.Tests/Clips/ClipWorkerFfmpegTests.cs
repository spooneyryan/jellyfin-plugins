using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The clip worker with the real ffmpeg (skipped without ffmpeg): the kill-and-restart simulation. A server that died while a clip was made left the job Running and its <c>.partial</c> on disk; at the next
/// start the janitor deletes the stale file before the job resumes, so the clip is written under its own name, not
/// <c>(2)</c>, and plays.
/// </summary>
public sealed class ClipWorkerFfmpegTests : IAsyncLifetime
{
    private readonly TempDirectory _directory = new();
    private readonly ListLogger<ClipExtractionService> _logger = new();
    private BookmarkStore? _store;
    private ClipExtractionService? _service;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AClipACrashInterrupted_IsMadeAgainUnderItsOwnName_AfterTheStalePartialIsDeleted()
    {
        var tools = FfmpegTools.Require();
        var source = _directory.Combine("source.mkv");
        await tools.RunAsync(
            [
                "-hide_banner", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-t", "8",
                "-c:v", "libx264", "-preset", "ultrafast", "-g", "50", "-pix_fmt", "yuv420p",
                "-c:a", "aac",
                source
            ],
            Ct);
        var planner = new FakeClipPlanner(_directory)
        {
            NamedFiles = true,
            EncoderPath = tools.Ffmpeg,
            AdjustCommand = command => command with
            {
                InputPath = source,
                VideoStreamIndex = 0,
                AudioStreamIndex = 1,
                Title = "Kill and restart",
                Encoding = new ClipEncodingOptions { Preset = "ultrafast" }
            }
        };

        // The state the crash left: the record Running, its clip folder recorded, half a clip under the job's name.
        _store = BookmarkStore.Create(_directory.Combine("bookmarks.json"), NullLogger.Instance);
        var bookmark = TestData.CreateBookmark(Guid.NewGuid(), 2, 5) with
        {
            Name = "Bank job",
            Clip = new ClipInfo
            {
                Status = BookmarkClipStatus.Running,
                Mode = BookmarkClipMode.Accurate,
                StartTicks = 2 * TestData.OneSecond,
                EndTicks = 5 * TestData.OneSecond,
                RequestedAt = DateTime.UtcNow.AddMinutes(-3)
            }
        };
        await _store.AddAsync(bookmark, Ct);
        await _store.AddClipRootAsync(planner.Root, Ct);
        var output = planner.OutputPathFor(bookmark);
        Assert.Equal("Heat (1995) - Bank job [00.00.02-00.00.05].mp4", Path.GetFileName(output));
        var stale = _directory.CreateFile(Path.Combine("clips", Path.GetFileName(output) + ClipFileGuard.PartialExtension), "half a clip");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-2));

        // The next start.
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var files = ClipFileSupport.Ops(_store, _logger);
        _service = new ClipExtractionService(
            _store,
            new BackgroundWriteGate(_store, NullLogger<BackgroundWriteGate>.Instance),
            planner,
            new FfmpegRunner(NullLogger<FfmpegRunner>.Instance),
            files,
            ClipFileSupport.Janitor(_store, files, startedUtc: DateTime.UtcNow, logger: _logger),
            ClipFileSupport.Guard(_store),
            TestData.ConfigurationProvider(new PluginConfiguration()),
            host.Object,
            _logger);
        await _service.StartAsync(Ct);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (_store.Get(bookmark.Id)?.Clip?.Status is not (BookmarkClipStatus.Completed or BookmarkClipStatus.Failed))
        {
            Assert.True(DateTime.UtcNow < deadline, "The resumed clip didn't finish.");
            await Task.Delay(50, Ct);
        }

        var clip = _store.Get(bookmark.Id)!.Clip!;
        Assert.True(clip.Status == BookmarkClipStatus.Completed, clip.Error);
        Assert.Equal(output, clip.FilePath);
        Assert.False(File.Exists(stale));
        Assert.Equal([output], ClipFileSupport.FilesBesidesMarker(planner.Root, SearchOption.AllDirectories));
        var probe = await tools.ProbeAsync(output, Ct);
        Assert.InRange(probe.Duration, 2.9, 3.15);
        Assert.Contains(("video", "h264"), probe.Streams);
        Assert.Contains(_logger.Entries, e => e.Message.StartsWith("Cleaned up the unfinished clip files (.partial) of clip jobs from before the last shutdown: 1 deleted, 0 could not be deleted", StringComparison.Ordinal));
        Assert.Equal(clip.FileSize, new FileInfo(output).Length);
    }

    [Fact]
    public async Task ALongAccurateClip_IsStoppedByTheWatchdog_WhenTheDriveRunsLow_AndLeavesNoPartialFile()
    {
        // Start a longer Accurate clip and flip the fake probe below the floor mid-encode → Failed, no
        // .partial left. The fake clock makes the watchdog's tick happen while ffmpeg is certainly still encoding.
        var tools = FfmpegTools.Require();
        var source = _directory.Combine("long.mkv");
        await tools.RunAsync(
            [
                "-hide_banner", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-t", "20",
                "-c:v", "libx264", "-preset", "ultrafast", "-g", "50", "-pix_fmt", "yuv420p",
                "-c:a", "aac",
                source
            ],
            Ct);
        var planner = new FakeClipPlanner(_directory)
        {
            NamedFiles = true,
            EncoderPath = tools.Ffmpeg,
            EstimatedBytes = 10_000_000,
            AdjustCommand = command => command with
            {
                InputPath = source,
                VideoStreamIndex = 0,
                AudioStreamIndex = 1,
                SourceHeight = 720,
                Encoding = new ClipEncodingOptions { Preset = "veryslow" }
            }
        };
        _store = BookmarkStore.Create(_directory.Combine("bookmarks.json"), NullLogger.Instance);
        var bookmark = TestData.CreateBookmark(Guid.NewGuid(), 0, 20) with { Name = "Watchdog" };
        await _store.AddAsync(bookmark, Ct);
        var probe = new FakeDriveSpaceProbe();
        var clock = new ManualTimerProvider();
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var files = ClipFileSupport.Ops(_store, _logger);
        _service = new ClipExtractionService(
            _store,
            new BackgroundWriteGate(_store, NullLogger<BackgroundWriteGate>.Instance),
            planner,
            new FfmpegRunner(NullLogger<FfmpegRunner>.Instance),
            files,
            ClipFileSupport.Janitor(_store, files),
            ClipFileSupport.Guard(_store, probe, timeProvider: clock),
            TestData.ConfigurationProvider(new PluginConfiguration()),
            host.Object,
            _logger);
        await _service.StartAsync(Ct);

        await _service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Accurate, 10_000_000, Ct);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (_service.GetProgress(bookmark.Id) is not (> 0 and < 0.9) || clock.ArmedTimers == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "ffmpeg didn't report progress.");
            Assert.NotEqual(BookmarkClipStatus.Completed, _store.Get(bookmark.Id)?.Clip?.Status);
            await Task.Delay(20, Ct);
        }

        var partial = planner.OutputPathFor(_store.Get(bookmark.Id)!) + ClipFileGuard.PartialExtension;
        Assert.True(File.Exists(partial), "ffmpeg is writing its .partial");
        probe.Available(ClipStorageGuard.Gigabyte - 1);
        clock.Advance(ClipStorageGuard.DefaultWatchdogInterval);

        while (_store.Get(bookmark.Id)?.Clip?.Status is not (BookmarkClipStatus.Failed or BookmarkClipStatus.Completed or BookmarkClipStatus.Cancelled))
        {
            Assert.True(DateTime.UtcNow < deadline, "The job didn't stop.");
            await Task.Delay(20, Ct);
        }

        var clip = _store.Get(bookmark.Id)!.Clip!;
        Assert.Equal(BookmarkClipStatus.Failed, clip.Status);
        Assert.Equal("Stopped: the clip drive is almost full.", clip.Error);
        Assert.Null(clip.FilePath);
        Assert.False(File.Exists(partial));
        Assert.Empty(ClipFileSupport.FilesBesidesMarker(planner.Root, SearchOption.AllDirectories));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.StopAsync(CancellationToken.None);
            _service.Dispose();
        }

        _store?.Dispose();
        _directory.Dispose();
    }
}
