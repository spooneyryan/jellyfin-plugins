using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.TestSupport;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The clip worker's race guards, with the real manager, store and clip worker and a fake ffmpeg that can be
/// held, released, made to fail, or made to finish although its job was cancelled: a bookmark deleted with its clip kept
/// at every moment of a clip job, and tombstones that carry a job state (another writer, another version, a hand edit).
/// Whatever the job does afterwards, the tombstone keeps its file and its normalised <c>Completed</c> state, and no file
/// of the job is left behind.
/// </summary>
public sealed class TombstoneRaceTests : IAsyncLifetime
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    private readonly TempDirectory _directory = new();
    private readonly BookmarkStore _store;
    private readonly FakeClipPlanner _planner;
    private readonly FakeFfmpegRunner _runner = new();
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly User _bob = TestData.CreateUser("bob");
    private readonly Movie _movie = TestData.CreateMovie();
    private readonly ClipExtractionService _service;
    private readonly IBookmarkManager _manager;

    public TombstoneRaceTests()
    {
        _store = BookmarkStore.Create(_directory.Combine("bookmarks.json"), NullLogger.Instance);
        _planner = new FakeClipPlanner(_directory);
        _host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        _library.Setup(m => m.GetItemById<Video>(_movie.Id, It.IsAny<User?>())).Returns(_movie);
        _service = CreateService(_store);
        _manager = CreateManager(_store, _service);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeletingWithTheClipKept_WhileARemakeRuns_KeepsTheEarlierFile_AndTheJobLeavesNothing()
    {
        await _service.StartAsync(Ct);
        var (bookmark, kept) = await AddBookmarkWithFirstClipAsync();
        var remake = await StartHeldRemakeAsync(bookmark);

        await _manager.DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: false, Ct);
        var tombstone = _store.Get(bookmark.Id)!;
        await WaitForJobToEndAsync(bookmark.Id);

        AssertTombstoneOf(kept, tombstone);
        Assert.Same(tombstone, _store.Get(bookmark.Id)); // the job's late "Cancelled" wrote nothing

        // Whether the job's "Cancelled" (with its time) landed before the tombstone's write or not, the tombstone has no
        // completion time: the kept file's was dropped when the remake was queued (BookmarkRecordRules.FileOnly).
        Assert.Null(tombstone.Clip?.CompletedAt);
        Assert.Equal(1, _runner.CancelledRuns);
        Assert.Equal("clip", await File.ReadAllTextAsync(kept.FilePath!, Ct));
        Assert.False(File.Exists(remake));
        Assert.False(File.Exists(remake + ".partial"));
    }

    [Fact]
    public async Task DeletingWithTheClipKept_WhileTheFirstClipRuns_RemovesTheRecord_AndTheJobLeavesNothing()
    {
        // Step 3: the job had not produced a file yet, so there is nothing to keep.
        await _service.StartAsync(Ct);
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Range(60, 90), Ct);
        _runner.HoldUntilReleased();
        await _manager.StartClipAsync(_alice, bookmark.Id, BookmarkClipMode.Accurate, isAdmin: false, Ct);
        await _runner.Started.Task.WaitAsync(_timeout, Ct);

        await _manager.DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: false, Ct);
        await WaitForJobToEndAsync(bookmark.Id);

        Assert.Null(_store.Get(bookmark.Id));
        Assert.Empty(ClipFileSupport.FilesBesidesMarker(_directory.Combine("clips")));
    }

    [Fact]
    public async Task DeletingWithTheClip_WhileARemakeRuns_RemovesRecordAndFiles_AsIn10()
    {
        await _service.StartAsync(Ct);
        var (bookmark, kept) = await AddBookmarkWithFirstClipAsync();
        var remake = await StartHeldRemakeAsync(bookmark);

        await _manager.DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: true, Ct);
        await WaitForJobToEndAsync(bookmark.Id);

        Assert.Null(_store.Get(bookmark.Id));
        Assert.False(File.Exists(kept.FilePath));
        Assert.False(File.Exists(remake));
        Assert.False(File.Exists(remake + ".partial"));
    }

    [Fact]
    public async Task DeletingWithTheClipKept_WhileARemakeIsQueued_TheJobNeverRuns()
    {
        var file = _directory.CreateFile("kept.mkv", "clip");
        var bookmark = await AddAsync(TestData.CreateBookmark(_alice.Id, 60, 90, _movie.Id) with { Clip = CompletedClip(file) });
        await _manager.StartClipAsync(_alice, bookmark.Id, BookmarkClipMode.Accurate, isAdmin: false, Ct);
        var queued = _store.Get(bookmark.Id)!.Clip!;
        Assert.Equal(BookmarkClipStatus.Queued, queued.Status);
        Assert.Null(queued.CompletedAt); // queuing the remake dropped the kept file's completion time (as 1.0 does)

        // What the delete's step 1 (CancelAsync) left, seen just before its step 2 (the tombstone write).
        ClipInfo? cancelled = null;
        var racing = new InterferingBookmarkStore(_store);
        racing.BeforeNextWriteRun(id =>
        {
            cancelled = _store.Get(id)?.Clip;
            return Task.CompletedTask;
        });
        await CreateManager(racing, _service).DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: false, Ct);
        var tombstone = _store.Get(bookmark.Id)!;
        await _service.StartAsync(Ct);
        await Task.Delay(300, Ct);

        Assert.Equal(0, _runner.Runs);
        Assert.Same(tombstone, _store.Get(bookmark.Id));
        Assert.Equal(BookmarkClipStatus.Completed, tombstone.Clip?.Status);
        Assert.Equal(file, tombstone.Clip?.FilePath);
        Assert.True(File.Exists(file));

        // The cancellation made the remake Cancelled at the delete's time; that is no completion time of the kept file, so
        // the tombstone has none (BookmarkRecordRules.FileOnly), and the request time stays the remake's.
        Assert.Equal(BookmarkClipStatus.Cancelled, cancelled?.Status);
        Assert.NotNull(cancelled?.CompletedAt);
        Assert.Null(tombstone.Clip?.CompletedAt);
        Assert.Equal(queued.RequestedAt, tombstone.Clip?.RequestedAt);
    }

    [Fact]
    public async Task ALateCompletion_AfterTheBookmarkWasDeletedWithItsClipKept_IsDiscarded()
    {
        // ffmpeg finished before the cancellation reached it: CompleteAsync meets a tombstone, deletes the new output and
        // leaves the record (the worker's first guard).
        await _service.StartAsync(Ct);
        var (bookmark, kept) = await AddBookmarkWithFirstClipAsync();
        _runner.IgnoreCancellation = true;
        var remake = await StartHeldRemakeAsync(bookmark);

        await _manager.DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: false, Ct);
        var tombstone = _store.Get(bookmark.Id)!;
        _runner.Release();
        await WaitForJobToEndAsync(bookmark.Id);

        Assert.Same(tombstone, _store.Get(bookmark.Id));
        AssertTombstoneOf(kept, tombstone);
        Assert.True(File.Exists(kept.FilePath));
        Assert.False(File.Exists(remake));
        Assert.False(File.Exists(remake + ".partial"));
    }

    [Fact]
    public async Task ARemakePlannedOverTheKeptFile_CantReplaceIt_AndTheTombstoneKeepsIt()
    {
        // Before 1.1 the real planner gave a remake the previous clip's name when it was the same, and the worker moved
        // the finished job over it: the kept file was replaced by a clip of another range. The planner no longer does that
        // (a remake gets a fresh name, ClipPlannerTests), and the worker's move never replaces a file either: a
        // planner that still named the kept file (the fake's ReusePreviousFile) makes the job fail, and the tombstone keeps
        // the file it had, byte for byte.
        await _service.StartAsync(Ct);
        var (bookmark, kept) = await AddBookmarkWithFirstClipAsync();
        await File.WriteAllTextAsync(kept.FilePath!, "the kept clip", Ct);
        _planner.ReusePreviousFile = true;
        _runner.IgnoreCancellation = true;
        var remake = await StartHeldRemakeAsync(bookmark);
        Assert.Equal(kept.FilePath, remake);

        await _manager.DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: false, Ct);
        var tombstone = _store.Get(bookmark.Id)!;
        _runner.Release();
        await WaitForJobToEndAsync(bookmark.Id);

        Assert.Same(tombstone, _store.Get(bookmark.Id));
        AssertTombstoneOf(kept, tombstone);
        Assert.Equal("the kept clip", await File.ReadAllTextAsync(kept.FilePath!, Ct));
        Assert.False(File.Exists(remake + ".partial"));
    }

    [Fact]
    public async Task ALateCompletion_UnderTheKeptFilesPath_WhenThatFileWasGone_IsKept_AndTheTombstoneRecordsIt()
    {
        // CompleteAsync's tombstone branch: the kept file was deleted outside Jellyfin before the remake was planned, so the
        // remake (same range and mode) got its free name; when it finishes after the bookmark was deleted with its clip
        // kept, the file on disk under the tombstone's path is this clip, and the tombstone says so, in its file-only state.
        await _service.StartAsync(Ct);
        var (bookmark, kept) = await AddBookmarkWithFirstClipAsync();
        File.Delete(kept.FilePath!);
        _runner.IgnoreCancellation = true;
        _runner.HoldUntilReleased();
        await _manager.StartClipAsync(_alice, bookmark.Id, BookmarkClipMode.Fast, isAdmin: false, Ct);
        await _runner.Started.Task.WaitAsync(_timeout, Ct);
        Assert.Equal(kept.FilePath, _planner.OutputPathFor(_store.Get(bookmark.Id)!));

        await _manager.DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: false, Ct);
        var tombstone = _store.Get(bookmark.Id)!;
        _runner.Release();
        await WaitForJobToEndAsync(bookmark.Id);

        var after = _store.Get(bookmark.Id)!;
        Assert.Equal(tombstone.DeletedUtc, after.DeletedUtc);
        Assert.Equal(tombstone.UpdatedAt, after.UpdatedAt);
        Assert.Equal(BookmarkClipStatus.Completed, after.Clip?.Status);
        Assert.Equal(kept.FilePath, after.Clip?.FilePath);
        Assert.Equal(60 * TestData.OneSecond, after.Clip?.FileStartTicks);
        Assert.Equal(BookmarkClipMode.Fast, after.Clip?.Mode); // from the file's .mkv
        Assert.Null(after.Clip?.Error);
        Assert.Null(tombstone.Clip?.CompletedAt); // the remake had not completed when the bookmark was deleted
        Assert.NotNull(after.Clip?.CompletedAt);
        Assert.Equal("clip", await File.ReadAllTextAsync(kept.FilePath!, Ct));
        Assert.False(File.Exists(kept.FilePath + ".partial"));
    }

    public static TheoryData<string> LateFailures => new() { "expected", "unexpected", "cancelled" };

    [Theory]
    [MemberData(nameof(LateFailures))]
    public async Task ALateFailure_AfterTheBookmarkWasDeletedWithItsClipKept_ChangesNothing(string kind)
    {
        // TrySetOutcomeAsync does nothing for tombstones: a late Failed (or Cancelled, when ffmpeg notices the cancellation
        // only after the delete's write) can't overwrite the normalised Completed.
        await _service.StartAsync(Ct);
        var (bookmark, kept) = await AddBookmarkWithFirstClipAsync();
        _runner.IgnoreCancellation = true;
        _runner.Failure = kind switch
        {
            "expected" => new ClipExtractionException("ffmpeg failed with exit code 1"),
            "cancelled" => new OperationCanceledException(),
            _ => new InvalidOperationException("boom")
        };
        var remake = await StartHeldRemakeAsync(bookmark);

        await _manager.DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: false, Ct);
        var tombstone = _store.Get(bookmark.Id)!;
        _runner.Release();
        await WaitForJobToEndAsync(bookmark.Id);

        Assert.Same(tombstone, _store.Get(bookmark.Id));
        Assert.Equal(BookmarkClipStatus.Completed, tombstone.Clip?.Status);
        Assert.Null(tombstone.Clip?.Error);
        Assert.True(File.Exists(kept.FilePath));
        Assert.False(File.Exists(remake + ".partial"));
    }

    [Fact]
    public async Task AShutdown_WhileATombstonesJobStillRuns_LeavesItCompleted_AndTheNextStartDoesNotResumeIt()
    {
        // A writer that makes a tombstone without stopping the job (a later version's, or a future policy): the shutdown's
        // "Queued" is a late outcome too, and the next start skips tombstones.
        await _service.StartAsync(Ct);
        var (bookmark, kept) = await AddBookmarkWithFirstClipAsync();
        var remake = await StartHeldRemakeAsync(bookmark);
        var tombstone = await _store.UpdateAsync(bookmark.Id, b => BookmarkRecordRules.ToTombstone(b, DateTime.UtcNow)!, Ct);

        await _service.StopAsync(CancellationToken.None);

        Assert.Same(tombstone, _store.Get(bookmark.Id));
        Assert.False(File.Exists(remake + ".partial"));
        var runs = _runner.Runs;
        using var restarted = CreateService(_store);
        await restarted.StartAsync(Ct);
        await Task.Delay(300, Ct);
        Assert.Equal(runs, _runner.Runs);
        Assert.Same(tombstone, _store.Get(bookmark.Id));
        Assert.True(File.Exists(kept.FilePath));
        await restarted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ResumingAtStartup_SkipsTombstones_WithAJobStateOnThem()
    {
        // RequeueInterruptedJobsAsync skips tombstones (another version's file, or a hand edit, can leave a job state on one).
        var live = await AddAsync(TestData.CreateBookmark(_alice.Id, 60, 90, _movie.Id) with { Clip = JobClip(BookmarkClipStatus.Running, null) });
        var running = await AddAsync(TestData.CreateBookmark(_alice.Id, 60, 90, _movie.Id) with
        {
            DeletedUtc = DateTime.UtcNow,
            Clip = JobClip(BookmarkClipStatus.Running, _directory.CreateFile("kept-1.mkv", "clip"))
        });
        var queued = await AddAsync(TestData.CreateBookmark(_alice.Id, 60, 90, _movie.Id) with
        {
            DeletedUtc = DateTime.UtcNow,
            Clip = JobClip(BookmarkClipStatus.Queued, _directory.CreateFile("kept-2.mkv", "clip"))
        });

        await _service.StartAsync(Ct);
        await WaitUntilAsync(() => _store.Get(live.Id)?.Clip?.Status == BookmarkClipStatus.Completed);
        await Task.Delay(300, Ct);

        Assert.Equal(1, _runner.Runs);
        Assert.Same(running, _store.Get(running.Id));
        Assert.Same(queued, _store.Get(queued.Id));
    }

    [Fact]
    public async Task AQueuedJob_OfARecordThatBecameATombstone_IsNeverClaimed()
    {
        // The claim checks under the store's lock: the id is in the queue, but the record is a tombstone by then (here one
        // that kept its job state, which no code of this version writes).
        var bookmark = await AddAsync(TestData.CreateBookmark(_alice.Id, 60, 90, _movie.Id));
        await _service.EnqueueAsync(bookmark.Id, _alice.Id, BookmarkClipMode.Accurate, null, Ct);
        var tombstone = await _store.UpdateAsync(bookmark.Id, b => b with { DeletedUtc = DateTime.UtcNow, Clip = b.Clip! with { FilePath = _directory.CreateFile("kept.mkv") } }, Ct);

        await _service.StartAsync(Ct);
        await Task.Delay(300, Ct);

        Assert.Equal(0, _runner.Runs);
        Assert.Same(tombstone, _store.Get(bookmark.Id));
        Assert.Equal(BookmarkClipStatus.Queued, tombstone!.Clip?.Status);
    }

    [Fact]
    public async Task Enqueue_RefusesATombstone_AndAnotherUsersBookmark()
    {
        var tombstone = await AddAsync(TestData.CreateBookmark(_alice.Id, 60, 90, _movie.Id) with
        {
            DeletedUtc = DateTime.UtcNow,
            Clip = CompletedClip(_directory.CreateFile("kept.mkv", "clip"))
        });
        var alices = await AddAsync(TestData.CreateBookmark(_alice.Id, 60, 90, _movie.Id));

        Assert.Null(await _service.EnqueueAsync(tombstone.Id, _alice.Id, BookmarkClipMode.Accurate, null, Ct));
        Assert.Null(await _service.EnqueueAsync(alices.Id, _bob.Id, BookmarkClipMode.Accurate, null, Ct));
        Assert.Null(await _service.EnqueueAsync(Guid.NewGuid(), _alice.Id, BookmarkClipMode.Accurate, null, Ct));
        await _service.StartAsync(Ct);
        await Task.Delay(300, Ct);

        Assert.Equal(0, _runner.Runs);
        Assert.Same(tombstone, _store.Get(tombstone.Id));
        Assert.Same(alices, _store.Get(alices.Id));
    }

    [Fact]
    public async Task Cancel_NeverChangesATombstonesClip()
    {
        var tombstone = await AddAsync(TestData.CreateBookmark(_alice.Id, 60, 90, _movie.Id) with
        {
            DeletedUtc = DateTime.UtcNow,
            Clip = JobClip(BookmarkClipStatus.Queued, _directory.CreateFile("kept.mkv", "clip"))
        });

        await _service.CancelAsync(tombstone.Id, Ct);

        Assert.Same(tombstone, _store.Get(tombstone.Id));
    }

    [Fact]
    public async Task ARemake_RacingADelete_GetsNoJob_And404()
    {
        // The manager found a live bookmark; before the queue's write, another request deleted it with its clip kept.
        var file = _directory.CreateFile("kept.mkv", "clip");
        var bookmark = await AddAsync(TestData.CreateBookmark(_alice.Id, 60, 90, _movie.Id) with { Clip = CompletedClip(file) });
        var racing = new InterferingBookmarkStore(_store);
        using var service = CreateService(racing);
        var manager = CreateManager(racing, service);
        racing.BeforeNextWrite(b => BookmarkRecordRules.ToTombstone(b, DateTime.UtcNow));

        var error = await Assert.ThrowsAsync<BookmarkException>(() => manager.StartClipAsync(_alice, bookmark.Id, BookmarkClipMode.Accurate, isAdmin: false, Ct));
        var tombstone = _store.Get(bookmark.Id)!;
        await service.StartAsync(Ct);
        await Task.Delay(300, Ct);

        Assert.Equal(PluginErrorKind.NotFound, error.Kind);
        Assert.Equal(0, _runner.Runs);
        Assert.True(BookmarkRecordRules.IsTombstone(tombstone));
        Assert.Same(tombstone, _store.Get(bookmark.Id));
        Assert.Equal(BookmarkClipStatus.Completed, tombstone.Clip?.Status);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AJobClaimed_BetweenTheDeletesCancelAndItsWrite_IsStopped()
    {
        // The delete cancels the job first (step 1) and writes the tombstone after (step 2). A remake requested and claimed
        // in between would otherwise run to the end for nothing; the delete stops it once its write is done.
        await _service.StartAsync(Ct);
        var file = _directory.CreateFile("kept.mkv", "clip");
        var bookmark = await AddAsync(TestData.CreateBookmark(_alice.Id, 60, 90, _movie.Id) with { Clip = CompletedClip(file) });
        _runner.HoldUntilReleased();
        var racing = new InterferingBookmarkStore(_store);
        racing.BeforeNextWriteRun(async id =>
        {
            await _service.EnqueueAsync(id, _alice.Id, BookmarkClipMode.Accurate, null, Ct);
            await _runner.Started.Task.WaitAsync(_timeout, Ct);
        });

        await CreateManager(racing, _service).DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: false, Ct);
        var tombstone = _store.Get(bookmark.Id)!;
        await WaitForJobToEndAsync(bookmark.Id); // still held: only the delete's stop can end it

        Assert.Equal(1, _runner.CancelledRuns);
        Assert.Same(tombstone, _store.Get(bookmark.Id));
        Assert.Equal(BookmarkClipStatus.Completed, tombstone.Clip?.Status);
        Assert.Equal(file, tombstone.Clip?.FilePath);
        Assert.Null(tombstone.Clip?.CompletedAt); // the tombstone met the remake Running: no time is the file's
        Assert.True(File.Exists(file));
        Assert.Empty(ClipFileSupport.FilesBesidesMarker(_directory.Combine("clips")));
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

    private BookmarkCreateRequest Range(long startSeconds, long endSeconds) => new()
    {
        ItemId = _movie.Id,
        StartTicks = startSeconds * TestData.OneSecond,
        EndTicks = endSeconds * TestData.OneSecond
    };

    private static ClipInfo CompletedClip(string file) => new()
    {
        Status = BookmarkClipStatus.Completed,
        Mode = BookmarkClipMode.Fast,
        StartTicks = 60 * TestData.OneSecond,
        EndTicks = 90 * TestData.OneSecond,
        RequestedAt = DateTime.UtcNow.AddMinutes(-2),
        CompletedAt = DateTime.UtcNow.AddMinutes(-1),
        FilePath = file,
        FileSize = 4,
        FileStartTicks = 60 * TestData.OneSecond,
        FileEndTicks = 90 * TestData.OneSecond
    };

    private static ClipInfo JobClip(BookmarkClipStatus status, string? file) => new()
    {
        Status = status,
        Mode = BookmarkClipMode.Accurate,
        StartTicks = 60 * TestData.OneSecond,
        EndTicks = 90 * TestData.OneSecond,
        RequestedAt = DateTime.UtcNow,
        FilePath = file,
        FileSize = file is null ? null : 4
    };

    private static void AssertTombstoneOf(ClipInfo kept, Bookmark tombstone)
    {
        Assert.True(BookmarkRecordRules.IsTombstone(tombstone));
        Assert.Equal(BookmarkClipStatus.Completed, tombstone.Clip?.Status);
        Assert.Null(tombstone.Clip?.Error);
        Assert.Equal(kept.FilePath, tombstone.Clip?.FilePath);
        Assert.Equal(kept.FileSize, tombstone.Clip?.FileSize);
        Assert.Equal(kept.FileStartTicks, tombstone.Clip?.StartTicks);
        Assert.Equal(kept.FileEndTicks, tombstone.Clip?.EndTicks);
        Assert.Equal(kept.Mode, tombstone.Clip?.Mode);
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

    private ClipExtractionService CreateService(IBookmarkStore store) => new(
        store,
        new BackgroundWriteGate(store, NullLogger<BackgroundWriteGate>.Instance),
        _planner,
        _runner,
        ClipFileSupport.Ops(store),
        ClipFileSupport.Janitor(store, ClipFileSupport.Ops(store)),
        ClipFileSupport.Guard(store),
        TestData.ConfigurationProvider(new PluginConfiguration()),
        _host.Object,
        NullLogger<ClipExtractionService>.Instance);

    private IBookmarkManager CreateManager(IBookmarkStore store, IClipExtractionService clips) => new BookmarkManager(
        store,
        _library.Object,
        clips,
        _planner,
        ClipFileSupport.Ops(store),
        ClipFileSupport.Guard(store),
        TestData.ConfigurationProvider(new PluginConfiguration()),
        NullLogger<BookmarkManager>.Instance);

    private async Task<Bookmark> AddAsync(Bookmark bookmark)
    {
        await _store.AddAsync(bookmark, Ct);
        return bookmark;
    }

    // A bookmark on the movie (1:00-1:30) with a finished Fast clip made by the worker: the file a tombstone keeps.
    private async Task<(Bookmark Bookmark, ClipInfo Kept)> AddBookmarkWithFirstClipAsync()
    {
        var created = await _manager.CreateBookmarkAsync(_alice, Range(60, 90), Ct);
        await _manager.StartClipAsync(_alice, created.Id, BookmarkClipMode.Fast, isAdmin: false, Ct);
        await WaitUntilAsync(() => _store.Get(created.Id)?.Clip?.Status == BookmarkClipStatus.Completed);
        var bookmark = _store.Get(created.Id)!;
        Assert.True(File.Exists(bookmark.Clip!.FilePath));
        return (bookmark, bookmark.Clip);
    }

    // Moves the range to 1:01 and starts an Accurate remake that ffmpeg holds; returns the remake's output path.
    private async Task<string> StartHeldRemakeAsync(Bookmark bookmark)
    {
        _runner.HoldUntilReleased();
        await _manager.UpdateBookmarkAsync(_alice, bookmark.Id, new BookmarkUpdateRequest { StartTicks = 61 * TestData.OneSecond, EndTicks = 90 * TestData.OneSecond }, Ct);
        await _manager.StartClipAsync(_alice, bookmark.Id, BookmarkClipMode.Accurate, isAdmin: false, Ct);
        await _runner.Started.Task.WaitAsync(_timeout, Ct);
        var running = _store.Get(bookmark.Id)!;
        Assert.Equal(BookmarkClipStatus.Running, running.Clip?.Status);
        return _planner.ReusePreviousFile ? running.Clip!.FilePath! : _planner.OutputPathFor(running);
    }

    private async Task WaitForJobToEndAsync(Guid bookmarkId)
    {
        var deadline = DateTime.UtcNow + _timeout;
        while (_service.GetProgress(bookmarkId) is not null)
        {
            Assert.True(DateTime.UtcNow < deadline, "The clip job didn't end.");
            await Task.Delay(20, Ct);
        }

        // The worker unregisters the job in its finally, after its last write; give the write a moment to land.
        await Task.Delay(50, Ct);
    }
}
