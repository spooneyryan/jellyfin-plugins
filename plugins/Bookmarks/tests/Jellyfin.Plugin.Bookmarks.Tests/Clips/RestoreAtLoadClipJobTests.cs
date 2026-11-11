using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The kit's restore at load (<c>bookmarks.json</c> missing or empty, restored from a snapshot) runs the step an admin's
/// <i>Restore snapshot</i> runs: a clip job the snapshot held queued or being made is ended, so the
/// clip worker, which resumes such jobs at start, never makes a clip the snapshot's job may already have made (a second
/// clip, or a file no record names).
/// </summary>
public sealed class RestoreAtLoadClipJobTests : IAsyncLifetime
{
    private static readonly DateTime Requested = new(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc);

    private readonly TempDirectory _directory = new();
    private readonly ListLogger _logger = new();
    private readonly BookmarkStore _store;
    private readonly FakeClipPlanner _planner;
    private readonly FakeFfmpegRunner _runner = new();
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly Movie _movie = TestData.CreateMovie();
    private readonly ClipExtractionService _service;

    public RestoreAtLoadClipJobTests()
    {
        // Nothing is read until the store is first used: the clip worker's start does that.
        _store = BookmarkStore.Create(_directory.Combine("bookmarks.json"), _logger);
        _planner = new FakeClipPlanner(_directory);
        _host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        _service = new ClipExtractionService(
            _store,
            new BackgroundWriteGate(_store, NullLogger<BackgroundWriteGate>.Instance),
            _planner,
            _runner,
            ClipFileSupport.Ops(_store),
            ClipFileSupport.Janitor(_store, ClipFileSupport.Ops(_store)),
            ClipFileSupport.Guard(_store),
            TestData.ConfigurationProvider(new PluginConfiguration()),
            _host.Object,
            NullLogger<ClipExtractionService>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASnapshotRestoredAtLoad_WithClipJobsQueuedAndRunning_HasThemEnded_AndTheWorkerResumesNone()
    {
        var queued = TestData.CreateBookmark(_alice.Id, 60, 90, _movie.Id) with { Clip = Job(BookmarkClipStatus.Queued) };
        var running = TestData.CreateBookmark(_alice.Id, 100, 130, _movie.Id) with { Clip = Job(BookmarkClipStatus.Running) };
        var plain = TestData.CreateBookmark(_alice.Id, 200, 230, _movie.Id);
        var snapshot = Path.Combine(_directory.Combine("snapshots"), "bookmarks-20261001-031000.json");
        Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
        await File.WriteAllBytesAsync(
            snapshot,
            JsonSerializer.SerializeToUtf8Bytes(BookmarkStore.WithBookmarks(BookmarkStoreDocument.CreateEmpty(), [queued, running, plain]), JsonStoreFormat.Options),
            Ct);

        await _service.StartAsync(Ct);
        await Task.Delay(300, Ct);

        Assert.Equal(StoreStatus.RestoredFromSnapshot, _store.Documents.Status);
        Assert.Equal(0, _runner.Runs);
        foreach (var id in new[] { queued.Id, running.Id })
        {
            var clip = _store.Get(id)!.Clip!;
            Assert.Equal(BookmarkClipStatus.Failed, clip.Status);
            Assert.Equal(BookmarkAdminTools.RestoredClipJobMessage, clip.Error);
            Assert.NotNull(clip.CompletedAt);
        }

        Assert.Null(_store.Get(plain.Id)!.Clip);
        Assert.DoesNotContain(_logger.Entries, e => e.Message.StartsWith("Resuming ", StringComparison.Ordinal));
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.StartsWith("The snapshot restored at load had 2 clip jobs queued or being made", StringComparison.Ordinal));

        // Saved so, not only served: a restart reads the jobs ended, and the snapshot is left as it was.
        using var reread = BookmarkStore.Create(_directory.Combine("bookmarks.json"), NullLogger.Instance);
        Assert.Equal(StoreStatus.Ok, reread.Documents.Status);
        Assert.All(new[] { queued.Id, running.Id }, id => Assert.Equal(BookmarkClipStatus.Failed, reread.Get(id)!.Clip!.Status));
        Assert.Equal(2, JsonDocument.Parse(await File.ReadAllBytesAsync(snapshot, Ct)).RootElement.GetProperty("Bookmarks").EnumerateArray().Count(b => b.TryGetProperty("Clip", out _)));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _runner.Release();
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
        _store.Dispose();
        _directory.Dispose();
    }

    private static ClipInfo Job(BookmarkClipStatus status) => new()
    {
        Status = status,
        Mode = BookmarkClipMode.Accurate,
        StartTicks = 60 * TestData.OneSecond,
        EndTicks = 90 * TestData.OneSecond,
        RequestedAt = Requested
    };
}
