using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
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
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Services;

/// <summary>
/// The manager through <see cref="IBookmarkManager"/>, the public interface the controller takes:
/// every call returns DTOs, and the assertions are on them. The store only arranges records, and is read where a DTO
/// never shows a field: the identity snapshot, a clip's full path, the relink fields, a removed record.
/// </summary>
public sealed class BookmarkManagerTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly BookmarkStore _store;
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IClipExtractionService> _clips = new();
    private readonly Mock<IClipPlanner> _planner = new();
    private readonly PluginConfiguration _configuration = new();
    private readonly FakeDriveSpaceProbe _probe = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly User _bob = TestData.CreateUser("bob");
    private readonly Movie _movie = TestData.CreateMovie();
    private readonly IBookmarkManager _manager;

    public BookmarkManagerTests()
    {
        // The folder the clip files of these tests are in is a recorded clip folder, with its marker file as
        // the clip worker leaves it (the clip routes take a missing file for gone only there).
        ClipFileSupport.MarkClipFolder(_directory.Path);
        _store = ClipFileSupport.StoreWithClipRoots(_directory.Combine("bookmarks.json"), _directory.Path);
        _movie.ProviderIds[MetadataProvider.Imdb.ToString()] = "tt0113277";
        _libraryManager.Setup(m => m.GetItemById<Video>(_movie.Id, It.IsAny<User?>())).Returns(_movie);
        _clips.Setup(c => c.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<BookmarkClipMode>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, Guid owner, BookmarkClipMode mode, long? estimate, CancellationToken _) => Task.FromResult(_store.Get(id)));

        // Plans as the real planner makes them (the free-space guard needs the output folder and the estimate).
        _planner.Setup(p => p.Plan(It.IsAny<Bookmark>(), It.IsAny<BookmarkClipMode>()))
            .Returns((Bookmark b, BookmarkClipMode m) => ClipFileSupport.Plan(b, m, _directory.Combine("clips")));
        _manager = new BookmarkManager(
            _store,
            _libraryManager.Object,
            _clips.Object,
            _planner.Object,
            ClipFileSupport.Ops(_store),
            ClipFileSupport.Guard(_store, _probe, _configuration),
            TestData.ConfigurationProvider(_configuration),
            NullLogger<BookmarkManager>.Instance);
    }

    [Fact]
    public async Task CreateBookmarkAsync_StoresBookmarkWithItemSnapshot()
    {
        var bookmark = await _manager.CreateBookmarkAsync(
            _alice,
            new BookmarkCreateRequest
            {
                ItemId = _movie.Id,
                StartTicks = 60 * TestData.OneSecond,
                EndTicks = 90 * TestData.OneSecond,
                Note = "  Bank job  ",
                MediaSourceId = " source ",
                AudioStreamIndex = 2
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(_movie.Id, bookmark.ItemId);
        Assert.Equal("Bank job", bookmark.Note);
        Assert.Null(bookmark.Name);
        Assert.Equal("source", bookmark.MediaSourceId);
        Assert.Equal(2, bookmark.AudioStreamIndex);
        Assert.Equal("Heat (1995)", bookmark.ItemName);
        Assert.Equal("Movie", bookmark.ItemType);

        // Schema 2: the identity snapshot of the video, native, attached now; the owner's name is known.
        var stored = _store.Get(bookmark.Id)!;
        Assert.Equal(_alice.Id, stored.UserId);
        Assert.Equal(_movie.Id, stored.Identity.ItemId);
        Assert.Equal("Movie", stored.Identity.Kind);
        Assert.Equal("Heat", stored.Identity.Name);
        Assert.Equal(1995, stored.Identity.Year);
        Assert.Equal(10_000 * TestData.OneSecond, stored.Identity.RunTimeTicks);
        Assert.Equal("tt0113277", stored.Identity.ProviderIds["Imdb"]);
        Assert.Equal(_movie.Path, stored.Identity.Path);
        Assert.Equal(BookmarkOrigin.Native, stored.Origin);
        Assert.Equal(stored.CreatedAt, stored.AttachedUtc);
        Assert.Null(stored.DetachedUtc);
        Assert.Null(stored.DeletedUtc);
        Assert.Equal("alice", _store.KnownUsers.NameOf(_alice.Id));
    }

    [Fact]
    public async Task Dto_NeverCarriesTheIdentityOrItsPath()
    {
        // Identity.Path is admin-only, and the snapshot stays on the server.
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Request(60, null), TestContext.Current.CancellationToken);

        var json = System.Text.Json.JsonSerializer.Serialize(bookmark);

        Assert.DoesNotContain(@"C:\\media", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Identity", json, StringComparison.Ordinal);
        Assert.DoesNotContain("tt0113277", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateBookmarkAsync_PointBookmark_HasNoEnd()
    {
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Request(60, null), TestContext.Current.CancellationToken);

        Assert.Null(bookmark.EndTicks);
        Assert.Null(bookmark.Note);
    }

    [Fact]
    public async Task CreateBookmarkAsync_UnknownOrHiddenItem_IsNotFound()
    {
        var request = Request(60, null);
        request.ItemId = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<BookmarkException>(() => _manager.CreateBookmarkAsync(_alice, request, TestContext.Current.CancellationToken));

        Assert.Equal(PluginErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task CreateBookmarkAsync_EmptyItemId_IsInvalid()
    {
        var request = Request(60, null);
        request.ItemId = Guid.Empty;

        var ex = await Assert.ThrowsAsync<BookmarkException>(() => _manager.CreateBookmarkAsync(_alice, request, TestContext.Current.CancellationToken));

        Assert.Equal(PluginErrorKind.Invalid, ex.Kind);
    }

    [Fact]
    public async Task CreateBookmarkAsync_ItemThatCannotBeLoaded_IsNotFound()
    {
        var broken = Guid.NewGuid();
        _libraryManager.Setup(m => m.GetItemById<Video>(broken, It.IsAny<User?>()))
            .Throws(new InvalidOperationException("Cannot deserialize unknown type."));
        var request = Request(60, null);
        request.ItemId = broken;

        var ex = await Assert.ThrowsAsync<BookmarkException>(() => _manager.CreateBookmarkAsync(_alice, request, TestContext.Current.CancellationToken));

        Assert.Equal(PluginErrorKind.NotFound, ex.Kind);
    }

    [Theory]
    [InlineData(-1L, null)]
    [InlineData(100L, 100L)]
    [InlineData(100L, 50L)]
    [InlineData(20_000L, null)]
    [InlineData(100L, 20_000L)]
    public async Task CreateBookmarkAsync_InvalidRange_IsRejected(long startSeconds, long? endSeconds)
    {
        var ex = await Assert.ThrowsAsync<BookmarkException>(
            () => _manager.CreateBookmarkAsync(_alice, Request(startSeconds, endSeconds), TestContext.Current.CancellationToken));

        Assert.Equal(PluginErrorKind.Invalid, ex.Kind);
    }

    [Fact]
    public async Task CreateBookmarkAsync_AllowsSmallOverrunOfRuntime()
    {
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Request(9_990, 10_002), TestContext.Current.CancellationToken);

        Assert.Equal(10_002 * TestData.OneSecond, bookmark.EndTicks);
    }

    [Fact]
    public async Task CreateBookmarkAsync_NoteTooLong_IsRejected()
    {
        var request = Request(60, null);
        request.Note = new string('x', 501);

        var ex = await Assert.ThrowsAsync<BookmarkException>(() => _manager.CreateBookmarkAsync(_alice, request, TestContext.Current.CancellationToken));

        Assert.Equal(PluginErrorKind.Invalid, ex.Kind);
    }

    [Fact]
    public async Task GetBookmark_OtherUsersBookmark_IsNotFound()
    {
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Request(60, null), TestContext.Current.CancellationToken);

        var ex = Assert.Throws<BookmarkException>(() => _manager.GetBookmark(_bob, bookmark.Id));

        Assert.Equal(PluginErrorKind.NotFound, ex.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateBookmarkAsync_RecordThatChangesBeforeTheWrite_IsLeftAsItIs_AndNotFound(bool becomesATombstone)
    {
        // An update of a tombstone is 404 and leaves the record unchanged. Here the record passes the ownership
        // check and then, before the store's lock is taken, becomes a tombstone (a delete) or another user's (a remap);
        // the store below makes that change just before the manager's update runs.
        var bookmark = TestData.CreateBookmark(_alice.Id, 60, 90, _movie.Id) with
        {
            Note = "old",
            Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = _directory.CreateFile("kept.mkv") }
        };
        await _store.AddAsync(bookmark, TestContext.Current.CancellationToken);
        var file = _directory.Combine("bookmarks.json");
        Bookmark? changed = null;
        byte[]? changedBytes = null;
        var racing = new Mock<IBookmarkStore>(MockBehavior.Strict);
        racing.Setup(s => s.ThrowIfReadOnly());
        racing.Setup(s => s.Get(bookmark.Id)).Returns(() => _store.Get(bookmark.Id));
        racing.Setup(s => s.UpdateAsync(bookmark.Id, It.IsAny<Func<Bookmark, Bookmark>>(), It.IsAny<CancellationToken>(), It.IsAny<User?>()))
            .Returns(async (Guid id, Func<Bookmark, Bookmark> update, CancellationToken cancellationToken, User? writer) =>
            {
                changed = await _store.UpdateAsync(
                    id,
                    b => becomesATombstone ? b with { DeletedUtc = DateTime.UtcNow } : b with { UserId = _bob.Id },
                    cancellationToken);
                changedBytes = await File.ReadAllBytesAsync(file, cancellationToken);
                return await _store.UpdateAsync(id, update, cancellationToken, writer);
            });
        IBookmarkManager manager = new BookmarkManager(
            racing.Object,
            _libraryManager.Object,
            _clips.Object,
            _planner.Object,
            ClipFileSupport.Ops(_store),
            ClipFileSupport.Guard(_store),
            TestData.ConfigurationProvider(_configuration),
            NullLogger<BookmarkManager>.Instance);

        var ex = await Assert.ThrowsAsync<BookmarkException>(() => manager.UpdateBookmarkAsync(
            _alice,
            bookmark.Id,
            new BookmarkUpdateRequest { StartTicks = 0, EndTicks = 10 * TestData.OneSecond, Note = "new" },
            TestContext.Current.CancellationToken));

        Assert.Equal(PluginErrorKind.NotFound, ex.Kind);
        Assert.NotNull(changed);
        Assert.Same(changed, _store.Get(bookmark.Id));
        Assert.Equal(changedBytes, await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        Assert.Equal("old", changed.Note);
        Assert.Equal(bookmark.StartTicks, changed.StartTicks);
        Assert.Equal(bookmark.UpdatedAt, changed.UpdatedAt);
        racing.Verify(s => s.UpdateAsync(bookmark.Id, It.IsAny<Func<Bookmark, Bookmark>>(), It.IsAny<CancellationToken>(), It.IsAny<User?>()), Times.Once);
    }

    [Fact]
    public async Task MigratedName_IsInTheDto_AndAnUpdateReplacesIt_Except10sBody()
    {
        // A 1.0 note the upgrade moved into Name stays visible. Since 1.1.0 an update replaces the name as it
        // replaces the note: a client that keeps it sends it again (the web editor does). Bookmarks 1.0's own body (neither Name nor IfUpdatedAt: a tab still running 1.0's script, which shows no
        // name and, once its list was read again after the upgrade, no note either) keeps the name.
        var bookmark = TestData.CreateBookmark(_alice.Id, itemId: _movie.Id) with { Name = "Bank job" };
        await _store.AddAsync(bookmark, TestContext.Current.CancellationToken);

        Assert.Equal("Bank job", _manager.GetBookmark(_alice, bookmark.Id).Name);
        var kept = await _manager.UpdateBookmarkAsync(
            _alice,
            bookmark.Id,
            new BookmarkUpdateRequest { StartTicks = 10 * TestData.OneSecond, Name = "Bank job", Note = "a note" },
            TestContext.Current.CancellationToken);
        var from10 = await _manager.UpdateBookmarkAsync(
            _alice,
            bookmark.Id,
            new BookmarkUpdateRequest { StartTicks = 20 * TestData.OneSecond, EndTicks = null, Note = null },
            TestContext.Current.CancellationToken);
        var cleared = await _manager.UpdateBookmarkAsync(
            _alice,
            bookmark.Id,
            new BookmarkUpdateRequest { StartTicks = 10 * TestData.OneSecond, Note = "a note", IfUpdatedAt = from10.UpdatedAt },
            TestContext.Current.CancellationToken);

        Assert.Equal("Bank job", kept.Name);
        Assert.Equal("a note", kept.Note);
        Assert.Equal("Bank job", from10.Name);
        Assert.Null(from10.Note);
        Assert.Equal(20 * TestData.OneSecond, from10.StartTicks);
        Assert.Null(cleared.Name);
        Assert.Equal("a note", cleared.Note);
    }

    [Fact]
    public async Task UpdateBookmarkAsync_ClearsTheRelinkedBadge()
    {
        // The next successful update clears RelinkedUtc/RelinkStrength, even when nothing else changed.
        var bookmark = TestData.CreateBookmark(_alice.Id, 60, 90, _movie.Id) with
        {
            RelinkedUtc = DateTime.UtcNow,
            RelinkStrength = Jellyfin.Plugin.Kit.Library.MatchStrength.Strong
        };
        await _store.AddAsync(bookmark, TestContext.Current.CancellationToken);

        await _manager.UpdateBookmarkAsync(
            _alice,
            bookmark.Id,
            new BookmarkUpdateRequest { StartTicks = bookmark.StartTicks, EndTicks = bookmark.EndTicks },
            TestContext.Current.CancellationToken);

        var stored = _store.Get(bookmark.Id)!;
        Assert.Null(stored.RelinkedUtc);
        Assert.Null(stored.RelinkStrength);
    }

    [Fact]
    public async Task AnAdoptedRecord_IsNeverLookedUpInTheLibrary()
    {
        // HasLibraryItem guards every GetItemById; 10.11.5 throws for Guid.Empty (the mock does too).
        _libraryManager.Setup(m => m.GetItemById<Video>(Guid.Empty, It.IsAny<User?>())).Throws(new ArgumentException("Guid can't be empty"));
        var adopted = TestData.CreateBookmark(_alice.Id, 0, 15, Guid.Empty) with { Origin = BookmarkOrigin.Adopted };
        await _store.AddAsync(adopted, TestContext.Current.CancellationToken);

        var updated = await _manager.UpdateBookmarkAsync(
            _alice,
            adopted.Id,
            new BookmarkUpdateRequest { StartTicks = 0, EndTicks = 20_000 * TestData.OneSecond },
            TestContext.Current.CancellationToken);
        var clip = await Assert.ThrowsAsync<BookmarkException>(() => _manager.StartClipAsync(_alice, adopted.Id, null, isAdmin: false, TestContext.Current.CancellationToken));

        Assert.Equal(20_000 * TestData.OneSecond, updated.EndTicks);
        Assert.Equal(PluginErrorKind.NotFound, clip.Kind);
        _libraryManager.Verify(m => m.GetItemById<Video>(Guid.Empty, It.IsAny<User?>()), Times.Never);
        VerifyNothingQueued();
    }

    [Fact]
    public async Task GetBookmarks_ForItem_OrdersByPosition()
    {
        await _manager.CreateBookmarkAsync(_alice, Request(300, null), TestContext.Current.CancellationToken);
        await _manager.CreateBookmarkAsync(_alice, Request(100, null), TestContext.Current.CancellationToken);
        await _manager.CreateBookmarkAsync(_bob, Request(200, null), TestContext.Current.CancellationToken);

        var bookmarks = _manager.GetBookmarks(_alice, _movie.Id);

        Assert.Equal(new[] { 100 * TestData.OneSecond, 300 * TestData.OneSecond }, bookmarks.Select(b => b.StartTicks));
    }

    [Fact]
    public async Task UpdateBookmarkAsync_ReplacesEditableFields()
    {
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Request(60, 90), TestContext.Current.CancellationToken);

        var updated = await _manager.UpdateBookmarkAsync(
            _alice,
            bookmark.Id,
            new BookmarkUpdateRequest { StartTicks = 30 * TestData.OneSecond, EndTicks = null, Note = "new" },
            TestContext.Current.CancellationToken);

        Assert.Equal(30 * TestData.OneSecond, updated.StartTicks);
        Assert.Null(updated.EndTicks);
        Assert.Equal("new", updated.Note);
        Assert.True(updated.UpdatedAt >= bookmark.UpdatedAt);
        Assert.Equal(bookmark.CreatedAt, updated.CreatedAt);
    }

    [Fact]
    public async Task UpdateBookmarkAsync_OtherUsersBookmark_IsNotFound()
    {
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Request(60, 90), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<BookmarkException>(() => _manager.UpdateBookmarkAsync(
            _bob,
            bookmark.Id,
            new BookmarkUpdateRequest { StartTicks = 0 },
            TestContext.Current.CancellationToken));

        Assert.Equal(PluginErrorKind.NotFound, ex.Kind);
        Assert.Equal(60 * TestData.OneSecond, _manager.GetBookmark(_alice, bookmark.Id).StartTicks);
    }

    [Fact]
    public async Task DeleteBookmarkAsync_WithItsClip_DeletesFileAndRecord_AndStopsItsJob()
    {
        var clipFile = _directory.CreateFile("clip.mp4");
        var bookmark = TestData.CreateBookmark(_alice.Id, itemId: _movie.Id) with
        {
            Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = clipFile }
        };
        await _store.AddAsync(bookmark, TestContext.Current.CancellationToken);

        await _manager.DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: true, TestContext.Current.CancellationToken);

        Assert.Null(_store.Get(bookmark.Id));
        Assert.False(File.Exists(clipFile));
        // The file first, then the record; a queued job goes with the record, and a running one is stopped.
        _clips.Verify(c => c.StopRunning(bookmark.Id), Times.Once);
        _clips.Verify(c => c.CancelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteBookmarkAsync_KeepsClipFileByDefault_InATombstone()
    {
        // The bookmark is gone for its user, and a clip-only tombstone keeps the file (TombstoneTests has the rows).
        var clipFile = _directory.CreateFile("clip.mp4");
        var bookmark = TestData.CreateBookmark(_alice.Id, itemId: _movie.Id) with
        {
            Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = clipFile }
        };
        await _store.AddAsync(bookmark, TestContext.Current.CancellationToken);

        await _manager.DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: false, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(clipFile));
        Assert.Empty(_manager.GetBookmarks(_alice, null));
        Assert.NotNull(_store.Get(bookmark.Id)?.DeletedUtc);
        _clips.Verify(c => c.CancelAsync(bookmark.Id, It.IsAny<CancellationToken>()), Times.Once);
        _clips.Verify(c => c.StopRunning(bookmark.Id), Times.Once);
    }

    [Fact]
    public async Task StartClipAsync_QueuesWithDefaultMode()
    {
        _configuration.DefaultClipMode = BookmarkClipMode.Fast;
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Request(60, 90), TestContext.Current.CancellationToken);

        await _manager.StartClipAsync(_alice, bookmark.Id, null, isAdmin: false, TestContext.Current.CancellationToken);

        _planner.Verify(p => p.Plan(It.Is<Bookmark>(b => b.Id == bookmark.Id), BookmarkClipMode.Fast), Times.Once);
        _clips.Verify(c => c.EnqueueAsync(bookmark.Id, _alice.Id, BookmarkClipMode.Fast, It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartClipAsync_WithoutEnd_IsRejected()
    {
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Request(60, null), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<BookmarkException>(() => _manager.StartClipAsync(_alice, bookmark.Id, null, isAdmin: false, TestContext.Current.CancellationToken));

        Assert.Equal(PluginErrorKind.Invalid, ex.Kind);
        VerifyNothingQueued();
    }

    [Fact]
    public async Task StartClipAsync_ExplicitMode_OverridesDefault()
    {
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Request(0, 5_000), TestContext.Current.CancellationToken);

        await _manager.StartClipAsync(_alice, bookmark.Id, BookmarkClipMode.Accurate, isAdmin: false, TestContext.Current.CancellationToken);

        _clips.Verify(c => c.EnqueueAsync(bookmark.Id, _alice.Id, BookmarkClipMode.Accurate, It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelClipAsync_KeepsTheClipFile()
    {
        var clipFile = _directory.CreateFile("clip.mp4");
        var bookmark = TestData.CreateBookmark(_alice.Id, itemId: _movie.Id) with
        {
            Clip = new ClipInfo { Status = BookmarkClipStatus.Queued, FilePath = clipFile }
        };
        await _store.AddAsync(bookmark, TestContext.Current.CancellationToken);

        var result = await _manager.CancelClipAsync(_alice, bookmark.Id, TestContext.Current.CancellationToken);

        _clips.Verify(c => c.CancelAsync(bookmark.Id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("clip.mp4", result.Clip?.FileName);
        Assert.Equal(clipFile, _store.Get(bookmark.Id)?.Clip?.FilePath);
        Assert.True(File.Exists(clipFile));
    }

    [Fact]
    public async Task CancelClipAsync_OtherUsersBookmark_IsNotFound()
    {
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Request(60, 90), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<BookmarkException>(() => _manager.CancelClipAsync(_bob, bookmark.Id, TestContext.Current.CancellationToken));

        Assert.Equal(PluginErrorKind.NotFound, ex.Kind);
        _clips.Verify(c => c.CancelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartClipAsync_WithoutDownloadPermission_IsForbidden()
    {
        var carol = TestData.CreateUser("carol", canDownload: false);
        var bookmark = await _manager.CreateBookmarkAsync(carol, Request(60, 90), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<BookmarkException>(() => _manager.StartClipAsync(carol, bookmark.Id, null, isAdmin: false, TestContext.Current.CancellationToken));

        Assert.Equal(PluginErrorKind.Forbidden, ex.Kind);
        VerifyNothingQueued();
    }

    [Fact]
    public async Task StartClipAsync_ClipsDisabled_IsForbidden()
    {
        _configuration.EnableClips = false;
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Request(60, 90), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<BookmarkException>(() => _manager.StartClipAsync(_alice, bookmark.Id, null, isAdmin: false, TestContext.Current.CancellationToken));

        Assert.Equal(PluginErrorKind.Forbidden, ex.Kind);
    }

    [Fact]
    public async Task StartClipAsync_PlannerRejection_IsReportedAsInvalid()
    {
        _planner.Setup(p => p.Plan(It.IsAny<Bookmark>(), It.IsAny<BookmarkClipMode>()))
            .Throws(new ClipExtractionException("The video file can't be found on the server."));
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Request(60, 90), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<BookmarkException>(() => _manager.StartClipAsync(_alice, bookmark.Id, null, isAdmin: false, TestContext.Current.CancellationToken));

        Assert.Equal(PluginErrorKind.Invalid, ex.Kind);
        Assert.Equal("The video file can't be found on the server.", ex.Message);
        VerifyNothingQueued();
    }

    [Fact]
    public async Task StartClipAsync_AClipFolderThatCantBeReached_WhileTheNameIsChosen_Is503_AndNothingIsQueued()
    {
        // Found in testing: the planner's name look-up failed (a drive that isn't ready), which
        // is the server's 503, not the request's 400.
        var lookup = new IOException("The device is not ready.", unchecked((int)0x80070015));
        _planner.Setup(p => p.Plan(It.IsAny<Bookmark>(), It.IsAny<BookmarkClipMode>()))
            .Throws(new ClipExtractionException(ClipFileOps.FolderUnreachableClipMessage, new ClipFolderUnreachableException(ClipFileOps.FolderUnreachableMessage, lookup)) { FolderUnreachable = true });
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Request(60, 90), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<BookmarkException>(() => _manager.StartClipAsync(_alice, bookmark.Id, null, isAdmin: false, TestContext.Current.CancellationToken));

        Assert.Equal(PluginErrorKind.Unavailable, ex.Kind);
        Assert.Equal(ClipFileOps.FolderUnreachableClipMessage, ex.Message);
        VerifyNothingQueued();
    }

    [Fact]
    public async Task StartClipAsync_ProbesTheClipsFolder_AndQueuesTheJobWithItsEstimate()
    {
        // The plan is kept; its folder (here a per-user folder not created yet) is probed, and its
        // estimate goes with the job.
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Request(60, 90), TestContext.Current.CancellationToken);

        await _manager.StartClipAsync(_alice, bookmark.Id, BookmarkClipMode.Accurate, isAdmin: false, TestContext.Current.CancellationToken);

        Assert.Equal([_directory.Combine("clips")], _probe.Probed);
        Assert.False(Directory.Exists(_directory.Combine("clips")));
        _clips.Verify(c => c.EnqueueAsync(bookmark.Id, _alice.Id, BookmarkClipMode.Accurate, 1_000_000, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false, "Not enough free space on the server for this clip. Ask your server admin.")]
    [InlineData(true, "Not enough free space for this clip: C:\\ has 2 GB free; this clip needs about 977 KB, and 2 GB must stay free (\"Keep free on the clip drive\" in the Bookmarks settings).")]
    public async Task StartClipAsync_WithTooLittleSpace_Is503_WithTheCallersWording_AndQueuesNothing(bool isAdmin, string message)
    {
        // 503; an admin gets the drive and the numbers, a user neither. Nothing is queued.
        _probe.Available(ClipStorageGuard.MinFreeBytes(_configuration) + 1_000_000 - 1);
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Request(60, 90), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<BookmarkException>(() => _manager.StartClipAsync(_alice, bookmark.Id, null, isAdmin, TestContext.Current.CancellationToken));

        Assert.Equal(PluginErrorKind.Unavailable, ex.Kind);
        Assert.Equal(message, ex.Message);
        VerifyNothingQueued();
        Assert.Null(_store.Get(bookmark.Id)!.Clip);
    }

    [Fact]
    public async Task StartClipAsync_IntoAFolderWhoseDriveIsntThere_Is503_NotWavedThrough()
    {
        _probe.Unavailable(error: 3);
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Request(60, 90), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<BookmarkException>(() => _manager.StartClipAsync(_alice, bookmark.Id, null, isAdmin: false, TestContext.Current.CancellationToken));

        Assert.Equal(PluginErrorKind.Unavailable, ex.Kind);
        Assert.Equal("The clip folder isn't available right now. Ask your server admin.", ex.Message);
        VerifyNothingQueued();
    }

    [Fact]
    public async Task StartClipAsync_WhereFreeSpaceCantBeRead_Queues()
    {
        // The guard is off for such a folder (FreeSpaceUnknown).
        _probe.Unknown();
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Request(60, 90), TestContext.Current.CancellationToken);

        await _manager.StartClipAsync(_alice, bookmark.Id, null, isAdmin: false, TestContext.Current.CancellationToken);

        _clips.Verify(c => c.EnqueueAsync(bookmark.Id, _alice.Id, It.IsAny<BookmarkClipMode>(), 1_000_000, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteClipAsync_DeletesFileAndForgetsClip()
    {
        var clipFile = _directory.CreateFile("clip.mp4");
        var bookmark = TestData.CreateBookmark(_alice.Id, itemId: _movie.Id) with
        {
            Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = clipFile }
        };
        await _store.AddAsync(bookmark, TestContext.Current.CancellationToken);

        var updated = Assert.IsType<BookmarkDto>(await _manager.DeleteClipAsync(_alice, bookmark.Id, TestContext.Current.CancellationToken));

        Assert.Null(updated.Clip);
        Assert.False(File.Exists(clipFile));
    }

    [Fact]
    public async Task DeleteClipAsync_RunningJob_KeepsRecordForCancellation()
    {
        var bookmark = TestData.CreateBookmark(_alice.Id, itemId: _movie.Id) with
        {
            Clip = new ClipInfo { Status = BookmarkClipStatus.Running, FilePath = _directory.Combine("old.mp4") }
        };
        await _store.AddAsync(bookmark, TestContext.Current.CancellationToken);

        var updated = Assert.IsType<BookmarkDto>(await _manager.DeleteClipAsync(_alice, bookmark.Id, TestContext.Current.CancellationToken));

        Assert.Equal(BookmarkClipStatus.Running, updated.Clip?.Status);
        Assert.Null(updated.Clip?.FileName);
        Assert.Null(_store.Get(bookmark.Id)?.Clip?.FilePath);
        _clips.Verify(c => c.CancelAsync(bookmark.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetClipFilePath_ServesAnyExistingClipFile()
    {
        var clipFile = _directory.CreateFile("clip.mp4");
        var finished = TestData.CreateBookmark(_alice.Id) with { Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = clipFile } };
        var remaking = TestData.CreateBookmark(_alice.Id) with { Clip = new ClipInfo { Status = BookmarkClipStatus.Running, FilePath = clipFile } };
        var failedRemake = TestData.CreateBookmark(_alice.Id) with { Clip = new ClipInfo { Status = BookmarkClipStatus.Failed, FilePath = clipFile } };
        var neverMade = TestData.CreateBookmark(_alice.Id) with { Clip = new ClipInfo { Status = BookmarkClipStatus.Running } };
        var missing = TestData.CreateBookmark(_alice.Id) with
        {
            Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = _directory.Combine("gone.mp4") }
        };
        foreach (var bookmark in new[] { finished, remaking, failedRemake, neverMade, missing })
        {
            await _store.AddAsync(bookmark, TestContext.Current.CancellationToken);
        }

        Assert.Equal(clipFile, _manager.GetClipFilePath(_alice, finished.Id));
        Assert.Equal(clipFile, _manager.GetClipFilePath(_alice, remaking.Id));
        Assert.Equal(clipFile, _manager.GetClipFilePath(_alice, failedRemake.Id));
        Assert.Equal(PluginErrorKind.NotFound, Assert.Throws<BookmarkException>(() => _manager.GetClipFilePath(_alice, neverMade.Id)).Kind);
        Assert.Equal(PluginErrorKind.NotFound, Assert.Throws<BookmarkException>(() => _manager.GetClipFilePath(_alice, missing.Id)).Kind);
        Assert.Equal(PluginErrorKind.NotFound, Assert.Throws<BookmarkException>(() => _manager.GetClipFilePath(_bob, finished.Id)).Kind);
    }

    [Fact]
    public void GetCapabilities_ReflectsConfigurationAndPermission()
    {
        _configuration.MaxClipDurationSeconds = 120;

        Assert.True(_manager.GetCapabilities(_alice, isAdmin: false).CanCreateClips);
        Assert.False(_manager.GetCapabilities(TestData.CreateUser("carol", canDownload: false), isAdmin: false).CanCreateClips);
        Assert.Equal(120, _manager.GetCapabilities(_alice, isAdmin: false).MaxClipDurationSeconds);

        _configuration.EnableClips = false;
        Assert.False(_manager.GetCapabilities(_alice, isAdmin: false).CanCreateClips);
    }

    [Fact]
    public void GetCapabilities_CanDownloadClips_IsTheDownloadPermission_AlsoWithClipsTurnedOff()
    {
        // Existing clips stay downloadable while making clips is turned off; the clip file route
        // asks only for "Allow media downloading" (Policies.Download).
        var carol = TestData.CreateUser("carol", canDownload: false);
        Assert.True(_manager.GetCapabilities(_alice, isAdmin: false).CanDownloadClips);
        Assert.False(_manager.GetCapabilities(carol, isAdmin: false).CanDownloadClips);

        _configuration.EnableClips = false;
        Assert.True(_manager.GetCapabilities(_alice, isAdmin: false).CanDownloadClips);
        Assert.False(_manager.GetCapabilities(_alice, isAdmin: false).CanCreateClips);
        Assert.False(_manager.GetCapabilities(carol, isAdmin: false).CanDownloadClips);
    }

    [Fact]
    public void GetCapabilities_IsAdmin_IsTheElevationResultNotTheStoredFlag()
    {
        var admin = TestData.CreateUser("root");
        admin.SetPermission(PermissionKind.IsAdministrator, true);

        // The controller passes CurrentUser.IsElevatedAsync; the stored flag plays no part.
        Assert.False(_manager.GetCapabilities(admin, isAdmin: false).IsAdmin);
        Assert.True(_manager.GetCapabilities(_alice, isAdmin: true).IsAdmin);
    }

    [Fact]
    public void GetCapabilities_StoreReadOnly_IsFalseForAWritableStore()
    {
        var capabilities = _manager.GetCapabilities(_alice, isAdmin: false);
        Assert.False(capabilities.StoreReadOnly);
        Assert.Null(capabilities.StoreReadOnlyReason);
    }

    [Fact]
    public async Task NewerStoreFile_IsShownReadOnly_AndEveryChangeIs503()
    {
        // Data written by a newer Bookmarks: readable, never written.
        var existing = TestData.CreateBookmark(_alice.Id, itemId: _movie.Id);
        var file = _directory.Combine("bookmarks.json");
        var newer = System.Text.Json.JsonSerializer.Serialize(
            BookmarkStore.WithBookmarks(BookmarkStoreDocument.CreateEmpty() with { Version = BookmarkStoreDocument.CurrentVersion + 1 }, [existing]),
            Jellyfin.Plugin.Kit.Storage.JsonStoreFormat.Options);
        await File.WriteAllTextAsync(file, newer, TestContext.Current.CancellationToken);

        var capabilities = _manager.GetCapabilities(_alice, isAdmin: false);
        Assert.True(capabilities.StoreReadOnly);
        Assert.Equal(BookmarkStoreReadOnlyReason.NewerVersion, capabilities.StoreReadOnlyReason);
        Assert.Equal(existing.Id, Assert.Single(_manager.GetBookmarks(_alice, null)).Id);

        var create = await Assert.ThrowsAsync<PluginException>(() => _manager.CreateBookmarkAsync(_alice, Request(10, 20), TestContext.Current.CancellationToken));
        Assert.Equal(PluginErrorKind.Unavailable, create.Kind);
        var update = await Assert.ThrowsAsync<PluginException>(
            () => _manager.UpdateBookmarkAsync(_alice, existing.Id, new BookmarkUpdateRequest { StartTicks = 0 }, TestContext.Current.CancellationToken));
        Assert.Equal(PluginErrorKind.Unavailable, update.Kind);
        var delete = await Assert.ThrowsAsync<PluginException>(() => _manager.DeleteBookmarkAsync(_alice, existing.Id, false, TestContext.Current.CancellationToken));
        Assert.Equal(PluginErrorKind.Unavailable, delete.Kind);
        Assert.Equal(newer, await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(StoreFixtures.Schema3, Jellyfin.Plugin.Kit.Storage.StoreMessages.NewerVersion)]
    [InlineData(StoreFixtures.Bookmarks100DuplicateKey, Jellyfin.Plugin.Kit.Storage.StoreMessages.MigrationFailed)]
    public async Task ReadOnlyData_EveryChangeOfABookmark_IsTheStores503_BeforeTheBookmarkIsLookedUp(string fixture, string message)
    {
        // Writes return 503. After a failed upgrade the store serves an empty stand-in, so looking the bookmark
        // up first answered 404 "Bookmark not found." for one the untouched file still holds: a page loaded before the
        // upgrade said it was gone. Every change that names a bookmark is now the store's 503 first, for
        // any id, so it tells nothing about other users' bookmarks; no clip job or clip file is touched.
        var ct = TestContext.Current.CancellationToken;
        var folder = _directory.Combine("read-only");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "bookmarks.json");
        var original = StoreFixtures.Read(fixture);
        await File.WriteAllBytesAsync(file, original, ct);
        using var store = BookmarkStore.Create(file, NullLogger.Instance);
        var guard = ClipFileSupport.Guard(store, _probe, _configuration);
        IBookmarkManager manager = new BookmarkManager(
            store,
            _libraryManager.Object,
            _clips.Object,
            _planner.Object,
            ClipFileSupport.Ops(store),
            guard,
            TestData.ConfigurationProvider(_configuration),
            NullLogger<BookmarkManager>.Instance);
        var admin = TestData.CreateUser("admin");
        admin.Id = new Guid("9fa5c677-8cf7-4a94-bff0-c82a60581ccf");
        var known = new Guid("5f7ef9bc-14bc-4e9a-a9c6-07c0b46a9210");

        Assert.True(store.IsReadOnly);
        Assert.Equal(fixture == StoreFixtures.Schema3, manager.GetBookmarks(admin, null).Any(b => b.Id == known));

        foreach (var id in new[] { known, Guid.NewGuid() })
        {
            Func<Task>[] changes =
            [
                () => manager.UpdateBookmarkAsync(admin, id, new BookmarkUpdateRequest { StartTicks = 0, Name = "Vault" }, ct),
                () => manager.UpdateBookmarkAsync(admin, id, new BookmarkUpdateRequest { StartTicks = 0 }, ct),
                () => manager.RenameBookmarkAsync(admin, id, new BookmarkRenameRequest { Name = "Vault" }, ct),
                () => manager.DeleteBookmarkAsync(admin, id, deleteClipFile: false, ct),
                () => manager.DeleteBookmarkAsync(admin, id, deleteClipFile: true, ct),
                () => manager.StartClipAsync(admin, id, BookmarkClipMode.Fast, isAdmin: true, ct),
                () => manager.CancelClipAsync(admin, id, ct),
                () => manager.DeleteClipAsync(admin, id, ct)
            ];
            foreach (var change in changes)
            {
                var ex = await Assert.ThrowsAsync<PluginException>(change);
                Assert.Equal(PluginErrorKind.Unavailable, ex.Kind);
                Assert.Equal(message, ex.Message);
            }
        }

        _clips.Verify(c => c.CancelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _clips.Verify(c => c.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<BookmarkClipMode>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
        _planner.Verify(p => p.Plan(It.IsAny<Bookmark>(), It.IsAny<BookmarkClipMode>()), Times.Never);
        Assert.Equal(0, _probe.Calls);
        Assert.Equal(original, await File.ReadAllBytesAsync(file, ct));
    }

    [Fact]
    public void GetDisplayName_Episode_IncludesSeriesAndNumbering()
    {
        var episode = new Episode { SeriesName = "The Wire", ParentIndexNumber = 1, IndexNumber = 2, Name = "The Detail" };

        Assert.Equal("The Wire S01E02 - The Detail", BookmarkManager.GetDisplayName(episode));
    }

    [Fact]
    public void GetDisplayName_MovieWithoutYear_UsesName()
    {
        Assert.Equal("Heat", BookmarkManager.GetDisplayName(TestData.CreateMovie(year: null)));
    }

    [Fact]
    public void GetDisplayName_NameAlreadyEndingInYear_IsNotRepeated()
    {
        Assert.Equal("Heat (1995)", BookmarkManager.GetDisplayName(TestData.CreateMovie(name: "Heat (1995)", year: 1995)));
    }

    public void Dispose()
    {
        _store.Dispose();
        _directory.Dispose();
    }

    private BookmarkCreateRequest Request(long startSeconds, long? endSeconds) => new()
    {
        ItemId = _movie.Id,
        StartTicks = startSeconds * TestData.OneSecond,
        EndTicks = endSeconds * TestData.OneSecond
    };

    private void VerifyNothingQueued()
        => _clips.Verify(c => c.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<BookmarkClipMode>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
}
