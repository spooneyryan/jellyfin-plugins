using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

public sealed class ClipExtractionServiceTests : IAsyncLifetime
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _directory = new();
    private readonly BookmarkStore _store;
    private readonly FakeClipPlanner _planner;
    private readonly FakeFfmpegRunner _runner = new();
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly ClipExtractionService _service;

    public ClipExtractionServiceTests()
    {
        _store = BookmarkStore.Create(_directory.Combine("bookmarks.json"), NullLogger.Instance);
        _planner = new FakeClipPlanner(_directory);
        _host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        _service = CreateService(_store, NullLogger<BackgroundWriteGate>.Instance, NullLogger<ClipExtractionService>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task QueuedClip_IsExtractedAndRecorded()
    {
        var bookmark = await AddBookmarkAsync();
        await _service.StartAsync(Ct);

        var queued = await _service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, null, Ct);
        Assert.Equal(BookmarkClipStatus.Queued, queued?.Clip?.Status);
        Assert.Equal(BookmarkClipMode.Fast, queued?.Clip?.Mode);

        var done = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Completed);
        var path = _planner.OutputPathFor(done);
        Assert.Equal(path, done.Clip!.FilePath);
        Assert.Equal("clip", await File.ReadAllTextAsync(path, Ct));
        Assert.False(File.Exists(path + ".partial"));
        Assert.Equal(4, done.Clip.FileSize);
        Assert.NotNull(done.Clip.CompletedAt);
        Assert.Equal(bookmark.StartTicks, done.Clip.StartTicks);
        Assert.Equal(bookmark.EndTicks, done.Clip.EndTicks);
    }

    [Fact]
    public async Task RunningClip_ReportsProgressAndCanBeCancelled()
    {
        _runner.HoldUntilReleased();
        var bookmark = await AddBookmarkAsync();
        await _service.StartAsync(Ct);
        await _service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Accurate, null, Ct);
        await _runner.Started.Task.WaitAsync(_timeout, Ct);

        Assert.Equal(BookmarkClipStatus.Running, _store.Get(bookmark.Id)?.Clip?.Status);
        Assert.Equal(0.5, _service.GetProgress(bookmark.Id));

        await _service.CancelAsync(bookmark.Id, Ct);

        var cancelled = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Cancelled);
        Assert.NotNull(cancelled.Clip?.CompletedAt);
        Assert.False(File.Exists(_planner.OutputPathFor(cancelled) + ".partial"));
        Assert.False(File.Exists(_planner.OutputPathFor(cancelled)));
    }

    [Fact]
    public async Task CancelledBeforeItStarts_IsSkipped()
    {
        var skipped = await AddBookmarkAsync();
        var next = await AddBookmarkAsync();
        await _service.EnqueueAsync(skipped.Id, skipped.UserId, BookmarkClipMode.Accurate, null, Ct);
        await _service.CancelAsync(skipped.Id, Ct);
        await _service.EnqueueAsync(next.Id, next.UserId, BookmarkClipMode.Accurate, null, Ct);

        await _service.StartAsync(Ct);
        await WaitForStatusAsync(next.Id, BookmarkClipStatus.Completed);

        Assert.Equal(BookmarkClipStatus.Cancelled, _store.Get(skipped.Id)?.Clip?.Status);
        Assert.Equal(1, _runner.Runs);
    }

    [Fact]
    public async Task EnqueueWhileRunning_DoesNotStartAnotherJob()
    {
        _runner.HoldUntilReleased();
        var bookmark = await AddBookmarkAsync();
        await _service.StartAsync(Ct);
        await _service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Accurate, null, Ct);
        await _runner.Started.Task.WaitAsync(_timeout, Ct);

        var again = await _service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, null, Ct);
        _runner.Release();

        Assert.Equal(BookmarkClipStatus.Running, again?.Clip?.Status);
        Assert.Equal(BookmarkClipMode.Accurate, again?.Clip?.Mode);
        await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Completed);
        Assert.Equal(1, _runner.Runs);
    }

    [Fact]
    public async Task RunnerFailure_IsRecordedAndPartialFileRemoved()
    {
        _runner.Failure = new ClipExtractionException("ffmpeg failed with exit code 1: boom");
        var bookmark = await AddBookmarkAsync();
        await _service.StartAsync(Ct);
        await _service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Accurate, null, Ct);

        var failed = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Failed);

        Assert.Equal("ffmpeg failed with exit code 1: boom", failed.Clip?.Error);
        Assert.False(File.Exists(_planner.OutputPathFor(failed) + ".partial"));
    }

    [Fact]
    public async Task PlannerFailure_IsRecorded()
    {
        _planner.Failure = new ClipExtractionException("The video file can't be found on the server.");
        var bookmark = await AddBookmarkAsync();
        await _service.StartAsync(Ct);
        await _service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Accurate, null, Ct);

        var failed = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Failed);

        Assert.Equal("The video file can't be found on the server.", failed.Clip?.Error);
        Assert.Equal(0, _runner.Runs);
    }

    [Fact]
    public async Task InterruptedJobs_ResumeOnStart()
    {
        var wasRunning = await AddBookmarkAsync(new ClipInfo { Status = BookmarkClipStatus.Running, StartTicks = 60 * TestData.OneSecond, EndTicks = 90 * TestData.OneSecond });
        var wasQueued = await AddBookmarkAsync(new ClipInfo { Status = BookmarkClipStatus.Queued, StartTicks = 60 * TestData.OneSecond, EndTicks = 90 * TestData.OneSecond });
        var finished = await AddBookmarkAsync(new ClipInfo { Status = BookmarkClipStatus.Failed });

        await _service.StartAsync(Ct);

        await WaitForStatusAsync(wasRunning.Id, BookmarkClipStatus.Completed);
        await WaitForStatusAsync(wasQueued.Id, BookmarkClipStatus.Completed);
        Assert.Equal(BookmarkClipStatus.Failed, _store.Get(finished.Id)?.Clip?.Status);
        Assert.Equal(2, _runner.Runs);
    }

    [Fact]
    public async Task InterruptedJobs_AreNotResumed_WhileTheStoreIsReadOnly()
    {
        // Data written by a newer Bookmarks can't record job states, so the worker stays idle (a schema 3 file:
        // read-only, worker idle).
        var running = TestData.CreateBookmark(Guid.NewGuid()) with
        {
            Clip = new ClipInfo { Status = BookmarkClipStatus.Running, StartTicks = 60 * TestData.OneSecond, EndTicks = 90 * TestData.OneSecond }
        };
        var queued = TestData.CreateBookmark(Guid.NewGuid()) with
        {
            Clip = new ClipInfo { Status = BookmarkClipStatus.Queued, StartTicks = 60 * TestData.OneSecond, EndTicks = 90 * TestData.OneSecond }
        };
        var file = _directory.Combine("bookmarks.json");
        var newer = JsonSerializer.Serialize(
            BookmarkStore.WithBookmarks(BookmarkStoreDocument.CreateEmpty() with { Version = BookmarkStoreDocument.CurrentVersion + 1 }, [running, queued]),
            JsonStoreFormat.Options);
        await File.WriteAllTextAsync(file, newer, Ct);
        var gateLogger = new ListLogger<BackgroundWriteGate>();
        var logger = new ListLogger<ClipExtractionService>();
        using var service = CreateService(_store, gateLogger, logger);

        await service.StartAsync(Ct);
        await WaitUntilAsync(() => gateLogger.Entries.Count > 0);
        await Task.Delay(200, Ct);

        var warning = Assert.Single(gateLogger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, warning.Level);
        Assert.Contains("read-only (NewerVersion), so ClipWorker is idle", warning.Message, StringComparison.Ordinal);
        Assert.Equal(0, _runner.Runs);
        Assert.Equal(BookmarkClipStatus.Running, _store.Get(running.Id)?.Clip?.Status);
        Assert.Equal(BookmarkClipStatus.Queued, _store.Get(queued.Id)?.Clip?.Status);
        Assert.Empty(logger.Entries);

        // A request to make a clip is refused by the store, and nothing reaches a worker.
        var refused = await Assert.ThrowsAsync<PluginException>(() => service.EnqueueAsync(running.Id, running.UserId, BookmarkClipMode.Fast, null, Ct));
        Assert.Equal(PluginErrorKind.Unavailable, refused.Kind);
        await Task.Delay(200, Ct);
        Assert.Equal(0, _runner.Runs);
        Assert.Equal(newer, await File.ReadAllTextAsync(file, Ct));
        Assert.Single(gateLogger.Entries);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AfterAFailedUpgrade_TheWorkerIsIdle_AndTheFileUntouched()
    {
        // A 1.0 file the upgrade refuses (here a hand edit that repeats a property) leaves the store empty and
        // read-only; the worker must not run, or its first status write would be refused anyway.
        var file = _directory.Combine("bookmarks.json");
        var original = StoreFixtures.Read(StoreFixtures.Bookmarks100DuplicateKey);
        await File.WriteAllBytesAsync(file, original, Ct);
        var gateLogger = new ListLogger<BackgroundWriteGate>();
        using var service = CreateService(_store, gateLogger, NullLogger<ClipExtractionService>.Instance);

        await service.StartAsync(Ct);
        await WaitUntilAsync(() => gateLogger.Entries.Count > 0);

        Assert.Contains("read-only (MigrationFailed), so ClipWorker is idle", Assert.Single(gateLogger.Entries).Message, StringComparison.Ordinal);
        Assert.Equal(StoreStatus.ReadOnlyMigrationFailed, _store.Documents.Status);
        Assert.Empty(_store.GetAll());
        await Assert.ThrowsAsync<PluginException>(() => service.EnqueueAsync(Guid.NewGuid(), Guid.NewGuid(), BookmarkClipMode.Fast, null, Ct));
        Assert.Equal(0, _runner.Runs);
        Assert.Equal(original, await File.ReadAllBytesAsync(file, Ct));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A10FileWithInterruptedJobs_IsUpgraded_AndTheJobsResume()
    {
        // Bookmarks 1.0 stopped while one clip was being made and another waited: after the upgrade the worker resumes both
        // (the 1.0.0 fixture's records; their old clip files moved into this test's folder, so nothing else is touched).
        var root = JsonNode.Parse(StoreFixtures.Read(StoreFixtures.Bookmarks100))!.AsObject();
        var records = root["Bookmarks"]!.AsArray().Select(n => n!.AsObject()).ToList();
        var wasRunning = records.Single(r => (string)r["Id"]! == "5f7ef9bc-14bc-4e9a-a9c6-07c0b46a9210");
        var wasQueued = records.Single(r => (string)r["Id"]! == "bff5bbea-1ebe-4669-9bc3-b5ae14fcbc6a");
        foreach (var (record, status) in new[] { (wasRunning, "Running"), (wasQueued, "Queued") })
        {
            var clip = record["Clip"]!.AsObject();
            clip["Status"] = status;
            clip.Remove("CompletedAt");
            clip["FilePath"] = _directory.Combine("old clips", (string)record["Id"]! + ".mkv");
        }

        foreach (var record in records.Where(r => r["Clip"] is JsonObject clip && (string?)clip["FilePath"] is { } path && !path.StartsWith(_directory.Path, StringComparison.Ordinal)))
        {
            record["Clip"]!["FilePath"] = _directory.Combine("old clips", (string)record["Id"]! + ".mp4");
        }

        await File.WriteAllTextAsync(_directory.Combine("bookmarks.json"), root.ToJsonString(), Ct);

        await _service.StartAsync(Ct);

        await WaitForStatusAsync(Guid.Parse((string)wasRunning["Id"]!), BookmarkClipStatus.Completed);
        await WaitForStatusAsync(Guid.Parse((string)wasQueued["Id"]!), BookmarkClipStatus.Completed);
        Assert.Equal(StoreStatus.Migrated, _store.Documents.Status);
        Assert.Equal(2, _runner.Runs);
        Assert.Equal("Bank job - the \"reflection\" in the window", _store.Get(Guid.Parse((string)wasRunning["Id"]!))!.Name);
        Assert.Equal(10, _store.GetAll().Count);
    }

    [Fact]
    public async Task ResumedJobs_WaitForServerStartupToFinish()
    {
        // Jellyfin only locates ffmpeg once core startup completes, after background services have started.
        var startupDone = false;
        _host.SetupGet(h => h.CoreStartupHasCompleted).Returns(() => startupDone);
        var pending = await AddBookmarkAsync(new ClipInfo { Status = BookmarkClipStatus.Queued, StartTicks = 60 * TestData.OneSecond, EndTicks = 90 * TestData.OneSecond });

        await _service.StartAsync(Ct);
        await Task.Delay(300, Ct);
        Assert.Equal(0, _runner.Runs);
        Assert.Equal(BookmarkClipStatus.Queued, _store.Get(pending.Id)?.Clip?.Status);

        startupDone = true;
        await WaitForStatusAsync(pending.Id, BookmarkClipStatus.Completed);
    }

    [Fact]
    public async Task FailedRemake_KeepsThePreviousClip()
    {
        var bookmark = await AddBookmarkAsync();
        await _service.StartAsync(Ct);
        await _service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Accurate, null, Ct);
        var first = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Completed);

        _runner.Failure = new ClipExtractionException("boom");
        await _store.UpdateAsync(bookmark.Id, b => b with { StartTicks = b.StartTicks + TestData.OneSecond }, Ct);
        var requeued = await _service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Accurate, null, Ct);
        Assert.Equal(first.Clip!.FilePath, requeued?.Clip?.FilePath);

        var failed = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Failed);

        Assert.Equal(first.Clip.FilePath, failed.Clip?.FilePath);
        Assert.Equal(first.Clip.FileStartTicks, failed.Clip?.FileStartTicks);
        Assert.True(File.Exists(first.Clip.FilePath));
    }

    [Fact]
    public async Task CompletedClip_RecordsTheRangeInTheFile()
    {
        var bookmark = await AddBookmarkAsync();
        await _service.StartAsync(Ct);
        await _service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Accurate, null, Ct);

        var done = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Completed);

        Assert.Equal(bookmark.StartTicks, done.Clip?.FileStartTicks);
        Assert.Equal(bookmark.EndTicks, done.Clip?.FileEndTicks);
    }

    [Fact]
    public void RedactPaths_ReplacesServerPathsWithFileNames()
    {
        var plan = new ClipPlan
        {
            EncoderPath = "ffmpeg",
            OutputPath = @"C:\data\clipslice\Heat.mp4",
            TempPath = @"C:\data\clipslice\Heat.mp4.partial",
            Duration = TimeSpan.FromSeconds(1),
            Root = @"C:\data\clipslice",
            Command = new ClipCommand
            {
                InputPath = @"Q:\Video\Heat (1995)\Heat.mkv",
                OutputPath = @"C:\data\clipslice\Heat.mp4.partial",
                Mode = BookmarkClipMode.Accurate,
                Start = TimeSpan.Zero,
                Duration = TimeSpan.FromSeconds(1)
            }
        };

        var message = ClipExtractionService.RedactPaths(
            @"ffmpeg failed: file:Q:\Video\Heat (1995)\Heat.mkv: Invalid data; could not write C:\data\clipslice\Heat.mp4.partial in C:\data\clipslice",
            plan);

        Assert.Equal("ffmpeg failed: file:Heat.mkv: Invalid data; could not write Heat.mp4.partial in the clip folder", message);
    }

    [Fact]
    public async Task ShutdownDuringExtraction_RequeuesJob()
    {
        _runner.HoldUntilReleased();
        var bookmark = await AddBookmarkAsync();
        await _service.StartAsync(Ct);
        await _service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Accurate, null, Ct);
        await _runner.Started.Task.WaitAsync(_timeout, Ct);

        await _service.StopAsync(Ct);

        var stored = _store.Get(bookmark.Id);
        Assert.Equal(BookmarkClipStatus.Queued, stored?.Clip?.Status);
        Assert.Null(stored?.Clip?.CompletedAt);
        Assert.False(File.Exists(_planner.OutputPathFor(stored!) + ".partial"));
    }

    [Fact]
    public async Task NewClip_ReplacesThePreviousFile()
    {
        var bookmark = await AddBookmarkAsync();
        await _service.StartAsync(Ct);
        await _service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Accurate, null, Ct);
        var first = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Completed);

        await _store.UpdateAsync(bookmark.Id, b => b with { StartTicks = b.StartTicks + TestData.OneSecond }, Ct);
        await _service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Accurate, null, Ct);
        await WaitUntilAsync(() => _store.Get(bookmark.Id)?.Clip is { Status: BookmarkClipStatus.Completed } clip && clip.FilePath != first.Clip!.FilePath);

        var second = _store.Get(bookmark.Id)!;
        Assert.True(File.Exists(second.Clip!.FilePath));
        // The record points at the new file first; the old one is deleted right after.
        await WaitUntilAsync(() => !File.Exists(first.Clip!.FilePath));
    }

    [Fact]
    public async Task BookmarkDeletedDuringExtraction_LeavesNoFileBehind()
    {
        _runner.HoldUntilReleased();
        var bookmark = await AddBookmarkAsync();
        await _service.StartAsync(Ct);
        await _service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Accurate, null, Ct);
        await _runner.Started.Task.WaitAsync(_timeout, Ct);
        var output = _planner.OutputPathFor(_store.Get(bookmark.Id)!);

        await _store.RemoveAsync(bookmark.Id, Ct);
        _runner.Release();
        await WaitUntilAsync(() => _service.GetProgress(bookmark.Id) is null);

        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".partial"));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    private ClipExtractionService CreateService(BookmarkStore store, ILogger<BackgroundWriteGate> gateLogger, ILogger<ClipExtractionService> logger)
        => new(
            store,
            new BackgroundWriteGate(store, gateLogger),
            _planner,
            _runner,
            ClipFileSupport.Ops(store),
            ClipFileSupport.Janitor(store, ClipFileSupport.Ops(store)),
            ClipFileSupport.Guard(store),
            TestData.ConfigurationProvider(new PluginConfiguration()),
            _host.Object,
            logger);

    public async ValueTask DisposeAsync()
    {
        _runner.Release();
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
        _store.Dispose();
        _directory.Dispose();
    }

    private async Task<Bookmark> AddBookmarkAsync(ClipInfo? clip = null)
    {
        var bookmark = TestData.CreateBookmark(Guid.NewGuid()) with { Clip = clip };
        await _store.AddAsync(bookmark, Ct);
        return bookmark;
    }

    private async Task<Bookmark> WaitForStatusAsync(Guid bookmarkId, BookmarkClipStatus status)
    {
        await WaitUntilAsync(() => _store.Get(bookmarkId)?.Clip?.Status == status, () => $"clip status is {_store.Get(bookmarkId)?.Clip?.Status}, expected {status}");
        return _store.Get(bookmarkId)!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string>? describe = null)
    {
        var deadline = DateTime.UtcNow + _timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting: " + (describe?.Invoke() ?? "condition not met"));
            }

            await Task.Delay(20, Ct);
        }
    }
}
