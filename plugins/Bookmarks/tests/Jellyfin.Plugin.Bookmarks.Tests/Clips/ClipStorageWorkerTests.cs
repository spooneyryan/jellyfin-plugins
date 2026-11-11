using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The free-space guard in the clip worker, with a fake ffmpeg, a fake
/// probe and a fake clock: the request's estimate is kept with the job and made again when an interrupted job is queued
/// again at startup; a job the check refuses when it is about to start fails with the user's wording and writes nothing (no
/// clip folder recorded, no <c>.partial</c>, no ffmpeg); the watchdog stops a running job, which fails with "Stopped: the
/// clip drive is almost full." and leaves no <c>.partial</c>; and a failure on a full disk (ffmpeg's lines as measured on Windows, a
/// move's HResult, or a probe after the failure, made before the <c>.partial</c> is deleted) is "The clip drive is full.".
/// </summary>
public sealed class ClipStorageWorkerTests : IAsyncLifetime
{
    private const long Gb = ClipStorageGuard.Gigabyte;
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _directory = new();
    private readonly BookmarkStore _store;
    private readonly FakeClipPlanner _planner;
    private readonly FakeFfmpegRunner _runner = new();
    private readonly FakeDriveSpaceProbe _probe = new();
    private readonly ManualTimerProvider _clock = new();
    private readonly PluginConfiguration _configuration = new();
    private readonly ListLogger _guardLogger = new();
    private readonly Mock<IServerApplicationHost> _host = new();
    private ClipExtractionService? _service;

    public ClipStorageWorkerTests()
    {
        _store = BookmarkStore.Create(_directory.Combine("bookmarks.json"), NullLogger.Instance);
        _planner = new FakeClipPlanner(_directory) { EstimatedBytes = 300_000_000 };
        _host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AQueuedJob_KeepsItsRequestsEstimate()
    {
        var bookmark = await AddAsync();
        var service = Create();

        var queued = await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, 123_456_789, Ct);

        Assert.Equal(123_456_789, queued?.Clip?.EstimatedBytes);
        Assert.Equal(123_456_789, _store.Get(bookmark.Id)?.Clip?.EstimatedBytes);
        using var reopened = BookmarkStore.Create(_directory.Combine("bookmarks.json"), NullLogger.Instance);
        Assert.Equal(123_456_789, reopened.Get(bookmark.Id)?.Clip?.EstimatedBytes);
    }

    [Fact]
    public async Task InterruptedJobs_GetTheirEstimateMadeAgain_AtStartup()
    {
        // RequeueInterruptedJobsAsync recomputes it for re-queued jobs (a stale one, and none: a job 1.0
        // queued). The runs are held so the recorded estimates can be read before the jobs finish.
        _runner.HoldUntilReleased();
        _configuration.MaxConcurrentClips = 1;
        var wasRunning = await AddAsync(new ClipInfo { Status = BookmarkClipStatus.Running, StartTicks = 0, EndTicks = 10 * TestData.OneSecond, EstimatedBytes = 5, RequestedAt = DateTime.UtcNow.AddMinutes(-2) });
        var wasQueued = await AddAsync(new ClipInfo { Status = BookmarkClipStatus.Queued, StartTicks = 0, EndTicks = 10 * TestData.OneSecond, RequestedAt = DateTime.UtcNow.AddMinutes(-1) });

        await Create().StartAsync(Ct);
        await _runner.Started.Task.WaitAsync(_timeout, Ct);

        Assert.Equal(300_000_000, _store.Get(wasRunning.Id)?.Clip?.EstimatedBytes);
        Assert.Equal(300_000_000, _store.Get(wasQueued.Id)?.Clip?.EstimatedBytes);
        _runner.Release();
        await WaitForStatusAsync(wasQueued.Id, BookmarkClipStatus.Completed);
    }

    [Fact]
    public async Task AnInterruptedJobThatCantBePlanned_KeepsItsEstimate()
    {
        _planner.Failure = new ClipExtractionException("The bookmarked video is no longer in the library.");
        var job = await AddAsync(new ClipInfo { Status = BookmarkClipStatus.Queued, StartTicks = 0, EndTicks = 10 * TestData.OneSecond, EstimatedBytes = 42 });

        await Create().StartAsync(Ct);
        var failed = await WaitForStatusAsync(job.Id, BookmarkClipStatus.Failed);

        Assert.Equal(42, failed.Clip?.EstimatedBytes);
        Assert.Equal("The bookmarked video is no longer in the library.", failed.Clip?.Error);
    }

    [Fact]
    public async Task AJobRefusedWhenItIsAboutToStart_Fails_AndWritesNothing()
    {
        // The same test when the job is claimed, before ffmpeg starts; Failed, not re-queued. Nothing is
        // written: the clip folder isn't recorded, no .partial is reserved, ffmpeg never runs.
        var bookmark = await AddAsync();
        var service = Create();
        await service.StartAsync(Ct);
        _probe.Available((2 * Gb) + 300_000_000 - 1);

        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Accurate, 300_000_000, Ct);
        var failed = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Failed);

        Assert.Equal("Not enough free space on the server for this clip. Ask your server admin.", failed.Clip?.Error);
        Assert.Equal(0, _runner.Runs);
        Assert.Empty(_store.ClipRoots);
        Assert.False(Directory.Exists(_planner.Root));
        Assert.Contains(_probe.Probed, folder => folder == _planner.Root);
        Assert.Contains(_guardLogger.At(LogLevel.Warning), e => e.Message.Contains("was refused when it was about to start: C:\\ has 2.3 GB free; this clip needs about 0.3 GB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AJobWhoseClipFolderIsntThere_Fails_WhenItIsAboutToStart()
    {
        var bookmark = await AddAsync();
        var service = Create();
        await service.StartAsync(Ct);
        _probe.Unavailable(error: 3);

        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, null, Ct);
        var failed = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Failed);

        Assert.Equal("The clip folder isn't available right now. Ask your server admin.", failed.Clip?.Error);
        Assert.Equal(0, _runner.Runs);
        Assert.Empty(_store.ClipRoots);
    }

    [Fact]
    public async Task AFolderWhoseFreeSpaceCantBeRead_LetsTheJobRun()
    {
        var bookmark = await AddAsync();
        var service = Create();
        await service.StartAsync(Ct);
        _probe.Unknown();

        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, 300_000_000, Ct);

        await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Completed);
    }

    [Fact]
    public async Task TheWatchdog_StopsARunningJob_WhichFails_AndLeavesNoPartialFile()
    {
        // Another program fills the drive during an encode.
        _runner.HoldUntilReleased();
        var bookmark = await AddAsync();
        var service = Create();
        await service.StartAsync(Ct);
        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Accurate, 300_000_000, Ct);
        await _runner.Started.Task.WaitAsync(_timeout, Ct);
        await _clock.WaitForArmedTimersAsync();
        var partial = _planner.OutputPathFor(_store.Get(bookmark.Id)!) + ClipFileGuard.PartialExtension;
        Assert.True(File.Exists(partial));

        _clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(100, Ct);
        Assert.Equal(BookmarkClipStatus.Running, _store.Get(bookmark.Id)?.Clip?.Status);

        _probe.Available(Gb - 1);
        _clock.Advance(TimeSpan.FromSeconds(5));
        var failed = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Failed);

        Assert.Equal("Stopped: the clip drive is almost full.", failed.Clip?.Error);
        Assert.NotNull(failed.Clip?.CompletedAt);
        Assert.Equal(1, _runner.CancelledRuns);
        Assert.False(File.Exists(partial));
        Assert.Empty(ClipFileSupport.FilesBesidesMarker(_planner.Root));
    }

    [Fact]
    public async Task TheWatchdog_EndsWithItsJob()
    {
        var bookmark = await AddAsync();
        var service = Create();
        await service.StartAsync(Ct);

        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, 300_000_000, Ct);
        await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Completed);

        await WaitUntilAsync(() => _clock.ArmedTimers == 0);
    }

    [Theory]
    [InlineData(-28)] // Fast into a full disk
    [InlineData(-22)] // the text alone
    public async Task FfmpegsLinesOfAFullDisk_FailTheJobWith_TheClipDriveIsFull(int exitCode)
    {
        _runner.FailureFor = partial => new ClipExtractionException(
            $"ffmpeg failed with exit code {exitCode}: [aost#0:1/copy @ 000001cebf328640] Error submitting a packet to the muxer: No space left on device Last message repeated 1 times [out#0/matroska @ 000001cebd615340] Error muxing a packet [out#0/matroska @ 000001cebd615340] Task finished with error code: -28 (No space left on device) [out#0/matroska @ 000001cebd615340] Terminating thread with return code -28 (No space left on device) [out#0/matroska @ 000001cebd615340] Error writing trailer: No space left on device [out#0/matroska @ 000001cebd615340] Error closing file: No space left on device")
        {
            ExitCode = exitCode
        };
        var bookmark = await AddAsync();
        var service = Create();
        await service.StartAsync(Ct);

        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, 300_000_000, Ct);
        var failed = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Failed);

        Assert.Equal("The clip drive is full.", failed.Clip?.Error);
        Assert.Empty(ClipFileSupport.FilesBesidesMarker(_planner.Root));
    }

    [Fact]
    public async Task AnyOtherFailure_WithTheDriveAlmostFullAfterIt_IsTheClipDriveIsFull_ProbedBeforeThePartialGoes()
    {
        // Measured on Windows: ERROR_HANDLE_DISK_FULL reads "Invalid argument" in ffmpeg; the probe after the failure decides, and it
        // runs while the job's .partial (which holds the space) is still there.
        var partialThereWhenProbed = new ConcurrentBag<bool>();
        _runner.FailureFor = partial => new ClipExtractionException("ffmpeg failed with exit code -22: Error muxing a packet: Invalid argument") { ExitCode = -22 };
        var bookmark = await AddAsync();
        var service = Create();
        await service.StartAsync(Ct);
        var probes = 0;
        _probe.Answer(folder =>
        {
            // The first probe is the check when the job starts; the second follows the failure.
            if (Interlocked.Increment(ref probes) == 1)
            {
                return FakeDriveSpaceProbe.Ok(folder, 10 * Gb);
            }

            partialThereWhenProbed.Add(Directory.GetFiles(_planner.Root, "*.partial").Length == 1);
            return FakeDriveSpaceProbe.Ok(folder, (Gb / 2) - 1);
        });

        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, 300_000_000, Ct);
        var failed = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Failed);

        Assert.Equal("The clip drive is full.", failed.Clip?.Error);
        Assert.Equal([true], partialThereWhenProbed);
        Assert.Empty(ClipFileSupport.FilesBesidesMarker(_planner.Root));
    }

    [Fact]
    public async Task AnyOtherFailure_WithSpaceLeft_KeepsItsOwnMessage()
    {
        _runner.Failure = new ClipExtractionException("ffmpeg failed with exit code 1: Invalid data found when processing input") { ExitCode = 1 };
        var bookmark = await AddAsync();
        var service = Create();
        await service.StartAsync(Ct);

        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, 300_000_000, Ct);
        var failed = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Failed);

        Assert.Equal("ffmpeg failed with exit code 1: Invalid data found when processing input", failed.Clip?.Error);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070070))]
    [InlineData(unchecked((int)0x80070027))]
    public async Task AFullDiskWhenTheFinishedClipIsMovedIntoPlace_IsTheClipDriveIsFull(int hresult)
    {
        // An IOException with HResult 0x80070070 or 0x80070027 during ReserveOutput or the final File.Move.
        var fileSystem = new FakeClipFileSystem { MoveFailure = (path, attempt) => new IOException("There is not enough space on the disk.", hresult) };
        var bookmark = await AddAsync();
        var service = Create(fileSystem);
        await service.StartAsync(Ct);

        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, 300_000_000, Ct);
        var failed = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Failed);

        Assert.Equal("The clip drive is full.", failed.Clip?.Error);
        Assert.Empty(ClipFileSupport.FilesBesidesMarker(_planner.Root));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _runner.Release();
        if (_service is not null)
        {
            await _service.StopAsync(CancellationToken.None);
            _service.Dispose();
        }

        _store.Dispose();
        _directory.Dispose();
    }

    private ClipExtractionService Create(IClipFileSystem? fileSystem = null)
    {
        var files = ClipFileSupport.Ops(_store, fileSystem: fileSystem);
        _service = new ClipExtractionService(
            _store,
            new BackgroundWriteGate(_store, NullLogger<BackgroundWriteGate>.Instance),
            _planner,
            _runner,
            files,
            ClipFileSupport.Janitor(_store, files),
            ClipFileSupport.Guard(_store, _probe, _configuration, _clock, _guardLogger),
            TestData.ConfigurationProvider(_configuration),
            _host.Object,
            NullLogger<ClipExtractionService>.Instance);
        return _service;
    }

    private async Task<Bookmark> AddAsync(ClipInfo? clip = null)
    {
        var bookmark = TestData.CreateBookmark(Guid.NewGuid()) with { Clip = clip };
        await _store.AddAsync(bookmark, Ct);
        return bookmark;
    }

    private async Task<Bookmark> WaitForStatusAsync(Guid bookmarkId, BookmarkClipStatus status)
    {
        await WaitUntilAsync(() => _store.Get(bookmarkId)?.Clip?.Status == status);
        return _store.Get(bookmarkId)!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + _timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(20, Ct);
        }
    }
}
