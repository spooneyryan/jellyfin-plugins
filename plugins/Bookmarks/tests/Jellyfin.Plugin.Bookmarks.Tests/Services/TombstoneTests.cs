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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Services;

/// <summary>
/// Clip-only tombstones, through <see cref="IBookmarkManager"/>. Each row of the tombstone table, for a live
/// bookmark and for a tombstone:
/// <list type="number">
/// <item>Lists and lookups (<c>GET /Bookmarks</c>, <c>?itemId=</c> for the detail and player lists, <c>/{id}</c>): live
/// included, tombstone excluded and 404. Query and Groups came with 1.1.0; <c>Api/TombstoneApiTests</c> fails until every
/// route that takes a bookmark id is listed with its tombstone answer.</item>
/// <item>Update, rename (<c>POST /{id}/Name</c>), remake (<c>POST /{id}/Clip</c>), cancel: live allowed, tombstone 404
/// with nothing changed.</item>
/// <item>The clip file and deleting the clip: live and tombstone alike (the clip list and rename came with 1.2.0).</item>
/// <item><c>DELETE /{id}/Clip</c>: live keeps the bookmark without its clip; tombstone loses file and record.</item>
/// <item><c>DELETE /{id}?deleteClip=false</c>: live with a file becomes a tombstone, without one it is removed;
/// tombstone 404.</item>
/// </list>
/// Then tombstone creation's clip states, and the manager's race guards: every write checks the record again under the
/// store's lock (a store that changes it just before the manager's write). The worker's guards, with a fake ffmpeg, are
/// in <c>Clips/TombstoneRaceTests</c>.
/// </summary>
public sealed class TombstoneTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly BookmarkStore _store;
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IClipExtractionService> _clips = new();
    private readonly Mock<IClipPlanner> _planner = new();
    private readonly PluginConfiguration _configuration = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly User _bob = TestData.CreateUser("bob");
    private readonly Movie _movie = TestData.CreateMovie();
    private readonly IBookmarkManager _manager;

    public TombstoneTests()
    {
        // The folder the clip files of these tests are in is a recorded clip folder.
        _store = ClipFileSupport.StoreWithClipRoots(_directory.Combine("bookmarks.json"), _directory.Path);
        _libraryManager.Setup(m => m.GetItemById<Video>(_movie.Id, It.IsAny<User?>())).Returns(_movie);
        _clips.Setup(c => c.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<BookmarkClipMode>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, Guid owner, BookmarkClipMode mode, long? estimate, CancellationToken _) => Task.FromResult(_store.Get(id)));
        _planner.Setup(p => p.Plan(It.IsAny<Bookmark>(), It.IsAny<BookmarkClipMode>()))
            .Returns((Bookmark b, BookmarkClipMode m) => ClipFileSupport.Plan(b, m, _directory.Path));
        _manager = CreateManager(_store);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StoreFile => _directory.Combine("bookmarks.json");

    [Fact]
    public async Task Row1_ListsAndLookups_ShowLiveBookmarks_AndNeverATombstone()
    {
        var (live, liveFile) = await AddLiveWithClipAsync("live.mp4");
        var (tombstone, _) = await AddTombstoneAsync("kept.mkv");

        Assert.Equal([live.Id], _manager.GetBookmarks(_alice, null).Select(b => b.Id));
        Assert.Equal([live.Id], _manager.GetBookmarks(_alice, _movie.Id).Select(b => b.Id));
        Assert.Equal(live.Id, _manager.GetBookmark(_alice, live.Id).Id);
        AssertNotFound(() => _manager.GetBookmark(_alice, tombstone.Id));
        Assert.True(File.Exists(liveFile));
    }

    [Fact]
    public async Task Row2_UpdateRenameRemakeAndCancel_WorkOnALiveBookmark()
    {
        var (live, _) = await AddLiveWithClipAsync("live.mp4");

        var updated = await _manager.UpdateBookmarkAsync(_alice, live.Id, new BookmarkUpdateRequest { StartTicks = 0, EndTicks = 30 * TestData.OneSecond }, Ct);
        var renamed = await _manager.RenameBookmarkAsync(_alice, live.Id, new BookmarkRenameRequest { Name = "Vault", IfUpdatedAt = updated.UpdatedAt }, Ct);
        var remade = await _manager.StartClipAsync(_alice, live.Id, BookmarkClipMode.Fast, isAdmin: false, Ct);
        var cancelled = await _manager.CancelClipAsync(_alice, live.Id, Ct);

        Assert.Equal(0, updated.StartTicks);
        Assert.Equal("Vault", renamed.Name);
        Assert.Equal(live.Id, remade.Id);
        Assert.Equal(live.Id, cancelled.Id);
        _clips.Verify(c => c.EnqueueAsync(live.Id, _alice.Id, BookmarkClipMode.Fast, It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Once);
        _clips.Verify(c => c.CancelAsync(live.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Row2_UpdateRenameRemakeAndCancel_OfATombstone_Are404_AndChangeNothing()
    {
        var (tombstone, file) = await AddTombstoneAsync("kept.mkv");
        var bytes = await File.ReadAllBytesAsync(StoreFile, Ct);

        await AssertNotFoundAsync(() => _manager.UpdateBookmarkAsync(_alice, tombstone.Id, new BookmarkUpdateRequest { StartTicks = 0, EndTicks = TestData.OneSecond }, Ct));
        await AssertNotFoundAsync(() => _manager.RenameBookmarkAsync(_alice, tombstone.Id, new BookmarkRenameRequest { Name = "Vault" }, Ct));
        await AssertNotFoundAsync(() => _manager.RenameBookmarkAsync(_alice, tombstone.Id, new BookmarkRenameRequest { Name = "Vault", IfUpdatedAt = tombstone.UpdatedAt }, Ct));
        await AssertNotFoundAsync(() => _manager.StartClipAsync(_alice, tombstone.Id, BookmarkClipMode.Accurate, isAdmin: false, Ct));
        await AssertNotFoundAsync(() => _manager.CancelClipAsync(_alice, tombstone.Id, Ct));

        Assert.Same(tombstone, _store.Get(tombstone.Id));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.True(File.Exists(file));
        _clips.Verify(c => c.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<BookmarkClipMode>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
        _clips.Verify(c => c.CancelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _planner.Verify(p => p.Plan(It.IsAny<Bookmark>(), It.IsAny<BookmarkClipMode>()), Times.Never);
    }

    [Fact]
    public async Task Row3_TheClipFile_IsServedForALiveBookmark_AndForATombstone_OnlyToItsOwner()
    {
        var (live, liveFile) = await AddLiveWithClipAsync("live.mp4");
        var (tombstone, keptFile) = await AddTombstoneAsync("kept.mkv");

        Assert.Equal(liveFile, _manager.GetClipFilePath(_alice, live.Id));
        Assert.Equal(keptFile, _manager.GetClipFilePath(_alice, tombstone.Id));
        AssertNotFound(() => _manager.GetClipFilePath(_bob, tombstone.Id));
        AssertNotFound(() => _manager.GetClipFilePath(_bob, live.Id));

        // A tombstone whose file is gone (deleted outside Jellyfin) is 404, like a live bookmark's missing file.
        File.Delete(keptFile);
        AssertNotFound(() => _manager.GetClipFilePath(_alice, tombstone.Id));
    }

    [Fact]
    public async Task Row4_DeletingTheClipOfALiveBookmark_KeepsTheBookmark()
    {
        var (live, file) = await AddLiveWithClipAsync("live.mp4");

        var result = await _manager.DeleteClipAsync(_alice, live.Id, Ct);

        Assert.NotNull(result);
        Assert.Null(result.Clip);
        Assert.False(File.Exists(file));
        Assert.Null(_store.Get(live.Id)?.Clip);
        Assert.Equal(live.Id, _manager.GetBookmark(_alice, live.Id).Id);
    }

    [Fact]
    public async Task Row4_DeletingTheClipOfATombstone_RemovesFileAndRecord()
    {
        var (tombstone, file) = await AddTombstoneAsync("kept.mkv");

        var result = await _manager.DeleteClipAsync(_alice, tombstone.Id, Ct);

        Assert.Null(result);
        Assert.False(File.Exists(file));
        Assert.Null(_store.Get(tombstone.Id));
        AssertNotFound(() => _manager.GetClipFilePath(_alice, tombstone.Id));
        await AssertNotFoundAsync(() => _manager.DeleteClipAsync(_alice, tombstone.Id, Ct));
    }

    [Fact]
    public async Task Row4_AnotherUsersTombstone_Is404_AndKeepsItsFile()
    {
        var (tombstone, file) = await AddTombstoneAsync("kept.mkv");

        await AssertNotFoundAsync(() => _manager.DeleteClipAsync(_bob, tombstone.Id, Ct));

        Assert.True(File.Exists(file));
        Assert.Same(tombstone, _store.Get(tombstone.Id));
    }

    [Fact]
    public async Task Row5_DeletingALiveBookmarkWithoutItsClip_KeepsATombstoneWithTheFile()
    {
        var (live, file) = await AddLiveWithClipAsync("live.mp4");
        var before = DateTime.UtcNow;

        await _manager.DeleteBookmarkAsync(_alice, live.Id, deleteClipFile: false, Ct);

        var stored = _store.Get(live.Id);
        Assert.NotNull(stored);
        Assert.True(BookmarkRecordRules.IsTombstone(stored));
        Assert.InRange(stored.DeletedUtc!.Value, before, DateTime.UtcNow);
        Assert.Equal(stored.DeletedUtc, stored.UpdatedAt);
        Assert.Equal(live.Name, stored.Name);
        Assert.Equal(file, stored.Clip?.FilePath);
        Assert.Equal(BookmarkClipStatus.Completed, stored.Clip?.Status);
        Assert.True(File.Exists(file));
        Assert.Empty(_manager.GetBookmarks(_alice, null));
        AssertNotFound(() => _manager.GetBookmark(_alice, live.Id));
        Assert.Equal(file, _manager.GetClipFilePath(_alice, live.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Row5_DeletingALiveBookmarkWithoutAClipFile_RemovesIt(bool firstJobQueued)
    {
        // Step 3: no clip, or a first job that had not written a file yet: nothing to keep.
        var bookmark = TestData.CreateBookmark(_alice.Id, itemId: _movie.Id) with
        {
            Clip = firstJobQueued ? new ClipInfo { Status = BookmarkClipStatus.Queued, StartTicks = 60 * TestData.OneSecond, EndTicks = 90 * TestData.OneSecond } : null
        };
        await _store.AddAsync(bookmark, Ct);

        await _manager.DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: false, Ct);

        Assert.Null(_store.Get(bookmark.Id));
        _clips.Verify(c => c.CancelAsync(bookmark.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Row5_DeletingATombstone_Is404_AndChangesNothing(bool deleteClipFile)
    {
        var (tombstone, file) = await AddTombstoneAsync("kept.mkv");
        var bytes = await File.ReadAllBytesAsync(StoreFile, Ct);

        await AssertNotFoundAsync(() => _manager.DeleteBookmarkAsync(_alice, tombstone.Id, deleteClipFile, Ct));

        Assert.Same(tombstone, _store.Get(tombstone.Id));
        Assert.True(File.Exists(file));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(StoreFile, Ct));
        _clips.Verify(c => c.CancelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeletingWithTheClip_RemovesRecordAndFile_AsIn10()
    {
        var (live, file) = await AddLiveWithClipAsync("live.mp4");

        await _manager.DeleteBookmarkAsync(_alice, live.Id, deleteClipFile: true, Ct);

        Assert.Null(_store.Get(live.Id));
        Assert.False(File.Exists(file));
    }

    public static TheoryData<BookmarkClipStatus, string, bool> ClipStates => new()
    {
        { BookmarkClipStatus.Completed, "clip.mp4", true },
        { BookmarkClipStatus.Running, "previous.mkv", true },
        { BookmarkClipStatus.Queued, "previous.mp4", true },
        { BookmarkClipStatus.Failed, "previous.MKV", true },
        { BookmarkClipStatus.Cancelled, "previous.mp4", false },
    };

    [Theory]
    [MemberData(nameof(ClipStates))]
    public async Task Creation_NormalisesTheClipToItsFile_WhateverTheLastJobDid(BookmarkClipStatus status, string fileName, bool withFileRange)
    {
        // Step 2: Completed, no error, the file's range (when known; 1.0 clips may lack it), the file's mode, the request time
        // kept, and the completion time only when it is the file's (Completed): in every other state it is a later job's, and
        // the tombstone has none (BookmarkRecordRules.FileOnly). The clip service is a mock here, so a Queued job reaches the
        // write as Queued; the real delete cancels it first, and it arrives as Cancelled with the delete's time (the Cancelled
        // row; Clips/TombstoneRaceTests runs that path).
        var file = _directory.CreateFile(fileName);
        var requested = new DateTime(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);
        var completed = status is BookmarkClipStatus.Queued or BookmarkClipStatus.Running ? (DateTime?)null : requested.AddMinutes(1);
        var bookmark = TestData.CreateBookmark(_alice.Id, 70, 95, _movie.Id) with
        {
            Name = "Bank job",
            Clip = new ClipInfo
            {
                Status = status,
                Mode = fileName.EndsWith(".mp4", StringComparison.Ordinal) ? BookmarkClipMode.Fast : BookmarkClipMode.Accurate,
                StartTicks = 70 * TestData.OneSecond,
                EndTicks = 95 * TestData.OneSecond,
                RequestedAt = requested,
                CompletedAt = completed,
                Error = status == BookmarkClipStatus.Failed ? "ffmpeg failed" : null,
                FilePath = file,
                FileSize = 4,
                FileStartTicks = withFileRange ? 60 * TestData.OneSecond : null,
                FileEndTicks = withFileRange ? 90 * TestData.OneSecond : null,
                EstimatedBytes = 5_000
            }
        };
        await _store.AddAsync(bookmark, Ct);

        await _manager.DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: false, Ct);

        var clip = _store.Get(bookmark.Id)?.Clip;
        Assert.NotNull(clip);
        Assert.Equal(BookmarkClipStatus.Completed, clip.Status);
        Assert.Null(clip.Error);
        Assert.Equal(withFileRange ? 60 * TestData.OneSecond : 70 * TestData.OneSecond, clip.StartTicks);
        Assert.Equal(withFileRange ? 90 * TestData.OneSecond : 95 * TestData.OneSecond, clip.EndTicks);
        Assert.Equal(ClipFileNaming.GetMode(fileName), clip.Mode);
        Assert.Equal(requested, clip.RequestedAt);
        Assert.Equal(status == BookmarkClipStatus.Completed ? completed : null, clip.CompletedAt);
        Assert.Equal(file, clip.FilePath);
        Assert.Equal(4, clip.FileSize);
        Assert.Null(clip.EstimatedBytes);
        Assert.True(File.Exists(file));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_OfARecordThatBecameATombstoneMeanwhile_Is404_AndWritesNothing(bool deleteClipFile)
    {
        // Two deletes of one bookmark at once: the first keeps its clip; the second passed the check before that, and must
        // neither delete the kept file nor write the tombstone again.
        var (live, file) = await AddLiveWithClipAsync("live.mp4");
        var racing = new InterferingBookmarkStore(_store);
        var first = DateTime.UtcNow.AddSeconds(-1);
        racing.BeforeNextWrite(b => BookmarkRecordRules.ToTombstone(b, first));
        var manager = CreateManager(racing);

        await AssertNotFoundAsync(() => manager.DeleteBookmarkAsync(_alice, live.Id, deleteClipFile, Ct));

        Assert.Equal(first, _store.Get(live.Id)?.DeletedUtc);
        Assert.True(File.Exists(file));
        _clips.Verify(c => c.StopRunning(It.IsAny<Guid>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rename_OfARecordThatBecameATombstoneMeanwhile_Is404_AndWritesNothing(bool withIfUpdatedAt)
    {
        // Row 2 under the store's lock: the rename passed the check, then the bookmark was deleted with its clip kept
        // (another tab). It stays a tombstone with its name, even with a token that was current when the rename began.
        var (live, file) = await AddLiveWithClipAsync("live.mp4");
        var racing = new InterferingBookmarkStore(_store);
        racing.BeforeNextWrite(b => BookmarkRecordRules.ToTombstone(b, DateTime.UtcNow));
        var manager = CreateManager(racing);

        await AssertNotFoundAsync(() => manager.RenameBookmarkAsync(
            _alice,
            live.Id,
            new BookmarkRenameRequest { Name = "Vault", IfUpdatedAt = withIfUpdatedAt ? live.UpdatedAt : null },
            Ct));

        var stored = _store.Get(live.Id)!;
        Assert.NotNull(stored.DeletedUtc);
        Assert.Equal("Bank job", stored.Name);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Delete_OfARecordThatChangedOwnerMeanwhile_Is404_AndLeavesIt()
    {
        var (live, file) = await AddLiveWithClipAsync("live.mp4");
        var racing = new InterferingBookmarkStore(_store);
        racing.BeforeNextWrite(b => b with { UserId = _bob.Id });
        var manager = CreateManager(racing);

        await AssertNotFoundAsync(() => manager.DeleteBookmarkAsync(_alice, live.Id, deleteClipFile: true, Ct));

        Assert.Equal(_bob.Id, _store.Get(live.Id)?.UserId);
        Assert.Null(_store.Get(live.Id)?.DeletedUtc);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Delete_OfARecordRemovedMeanwhile_Is404()
    {
        var (live, _) = await AddLiveWithClipAsync("live.mp4");
        var racing = new InterferingBookmarkStore(_store);
        racing.BeforeNextWrite(_ => null);

        await AssertNotFoundAsync(() => CreateManager(racing).DeleteBookmarkAsync(_alice, live.Id, deleteClipFile: false, Ct));

        Assert.Null(_store.Get(live.Id));
    }

    [Fact]
    public async Task DeleteClip_OfABookmarkDeletedWithItsClipKeptMeanwhile_RemovesTheTombstoneToo()
    {
        // The user deletes the clip while (in another tab) the bookmark is deleted with its clip kept: both asked for
        // something to go, and nothing of either is left (204).
        var (live, file) = await AddLiveWithClipAsync("live.mp4");
        var racing = new InterferingBookmarkStore(_store);
        racing.BeforeNextWrite(b => BookmarkRecordRules.ToTombstone(b, DateTime.UtcNow));

        var result = await CreateManager(racing).DeleteClipAsync(_alice, live.Id, Ct);

        Assert.Null(result);
        Assert.Null(_store.Get(live.Id));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task DeleteClip_OfARecordThatChangedOwnerMeanwhile_Is404_AndKeepsTheFile()
    {
        var (tombstone, file) = await AddTombstoneAsync("kept.mkv");
        var racing = new InterferingBookmarkStore(_store);
        racing.BeforeNextWrite(b => b with { UserId = _bob.Id });

        await AssertNotFoundAsync(() => CreateManager(racing).DeleteClipAsync(_alice, tombstone.Id, Ct));

        Assert.Equal(_bob.Id, _store.Get(tombstone.Id)?.UserId);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Cancel_OfABookmarkThatBecameATombstoneMeanwhile_Is404()
    {
        var (live, _) = await AddLiveWithClipAsync("live.mp4");
        _clips.Setup(c => c.CancelAsync(live.Id, It.IsAny<CancellationToken>()))
            .Returns(async (Guid id, CancellationToken cancellationToken) =>
            {
                await _store.UpdateAsync(id, b => BookmarkRecordRules.ToTombstone(b, DateTime.UtcNow)!, cancellationToken);
            });

        await AssertNotFoundAsync(() => _manager.CancelClipAsync(_alice, live.Id, Ct));
    }

    [Fact]
    public async Task Remake_ThatTheQueueRefuses_Is404()
    {
        // The queue checks the record again under the store's lock (Clips/TombstoneRaceTests); its "no" is a 404.
        var (live, _) = await AddLiveWithClipAsync("live.mp4");
        _clips.Setup(c => c.EnqueueAsync(live.Id, _alice.Id, It.IsAny<BookmarkClipMode>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Bookmark?)null);

        await AssertNotFoundAsync(() => _manager.StartClipAsync(_alice, live.Id, BookmarkClipMode.Fast, isAdmin: false, Ct));
    }

    public void Dispose()
    {
        _store.Dispose();
        _directory.Dispose();
    }

    private static void AssertNotFound(Action action)
        => Assert.Equal(PluginErrorKind.NotFound, Assert.Throws<BookmarkException>(action).Kind);

    private static async Task AssertNotFoundAsync(Func<Task> action)
        => Assert.Equal(PluginErrorKind.NotFound, (await Assert.ThrowsAsync<BookmarkException>(action)).Kind);

    private IBookmarkManager CreateManager(IBookmarkStore store) => new BookmarkManager(
        store,
        _libraryManager.Object,
        _clips.Object,
        _planner.Object,
        ClipFileSupport.Ops(store),
        ClipFileSupport.Guard(store),
        TestData.ConfigurationProvider(_configuration),
        NullLogger<BookmarkManager>.Instance);

    private async Task<(Bookmark Bookmark, string File)> AddLiveWithClipAsync(string fileName)
    {
        var file = _directory.CreateFile(fileName);
        var bookmark = TestData.CreateBookmark(_alice.Id, 60, 90, _movie.Id) with
        {
            Name = "Bank job",
            Clip = new ClipInfo
            {
                Status = BookmarkClipStatus.Completed,
                Mode = ClipFileNaming.GetMode(fileName),
                StartTicks = 60 * TestData.OneSecond,
                EndTicks = 90 * TestData.OneSecond,
                FilePath = file,
                FileSize = 4,
                FileStartTicks = 60 * TestData.OneSecond,
                FileEndTicks = 90 * TestData.OneSecond
            }
        };
        await _store.AddAsync(bookmark, Ct);
        return (bookmark, file);
    }

    private async Task<(Bookmark Bookmark, string File)> AddTombstoneAsync(string fileName)
    {
        var (bookmark, file) = await AddLiveWithClipAsync(fileName);
        var tombstone = await _store.UpdateAsync(bookmark.Id, b => BookmarkRecordRules.ToTombstone(b, DateTime.UtcNow)!, Ct);
        return (tombstone!, file);
    }
}
