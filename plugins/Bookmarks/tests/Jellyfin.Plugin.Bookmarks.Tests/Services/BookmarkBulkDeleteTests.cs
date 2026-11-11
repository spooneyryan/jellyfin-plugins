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
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Services;

/// <summary>
/// <c>POST /Bookmarks/BulkDelete</c> through <see cref="IBookmarkManager.BulkDeleteAsync"/>: without the clips one write that makes tombstones of the bookmarks with a clip file and removes
/// the others; with them each file first, a file in use tried three times and then left with its bookmark (<c>InUse</c>), a
/// file that can't be deleted otherwise left too (<c>Failed</c>); other users' ids, tombstones and unknown ids are
/// <c>NotFound</c> and never touched; read-only data 503 with nothing written.
/// </summary>
public sealed class BookmarkBulkDeleteTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly string _root;
    private readonly BookmarkStore _inner;
    private readonly InterferingBookmarkStore _store;
    private readonly FakeClipFileSystem _fileSystem = new();
    private readonly Mock<IClipExtractionService> _clips = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly User _bob = TestData.CreateUser("bob");
    private readonly IBookmarkManager _manager;

    public BookmarkBulkDeleteTests()
    {
        _root = _directory.Combine("clips");
        Directory.CreateDirectory(_root);

        // The clip folder as the clip worker leaves it since 1.2.0: with its marker file, whose token the store
        // records (a missing file is taken for gone only in such a folder).
        ClipFileSupport.MarkClipFolder(_root);
        _inner = ClipFileSupport.StoreWithClipRoots(_directory.Combine("bookmarks.json"), _root);
        _store = new InterferingBookmarkStore(_inner);
        _manager = CreateManager(_store);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StoreFile => _directory.Combine("bookmarks.json");

    [Fact]
    public async Task KeepingTheClips_InOneWrite_ATombstoneForEachClipFile_TheOthersRemoved_AndTheRestNotFound()
    {
        var (withFile, file) = await AddAsync(_alice, clipFile: true);
        var (plain, _) = await AddAsync(_alice);
        var (queued, _) = await AddAsync(_alice, queuedWithoutFile: true);
        var (bobs, bobsFile) = await AddAsync(_bob, clipFile: true);
        var (tombstone, _) = await AddAsync(_alice, clipFile: true);
        await _inner.UpdateAsync(tombstone.Id, b => BookmarkRecordRules.ToTombstone(b, DateTime.UtcNow)!, Ct);
        var unknown = Guid.NewGuid();
        var writes = _store.DocumentWrites;

        var result = await _manager.BulkDeleteAsync(
            _alice,
            new BookmarkBulkDeleteRequest { Ids = [withFile.Id, plain.Id, bobs.Id, withFile.Id, tombstone.Id, queued.Id, unknown], DeleteClips = false },
            Ct);

        Assert.Equal([plain.Id, queued.Id], result.Deleted);
        Assert.Equal([withFile.Id], result.Tombstoned);
        Assert.Equal([bobs.Id, tombstone.Id, unknown], result.NotFound);
        Assert.Empty(result.InUse);
        Assert.Empty(result.Failed);
        Assert.Equal(writes + 1, _store.DocumentWrites);
        Assert.Equal(0, _store.RecordWrites);

        // The tombstone keeps its file, reduced to it; the others are gone; nothing else changed.
        var kept = _store.Get(withFile.Id)!;
        Assert.NotNull(kept.DeletedUtc);
        Assert.Equal(BookmarkClipStatus.Completed, kept.Clip!.Status);
        Assert.Equal(file, kept.Clip.FilePath);
        Assert.True(File.Exists(file));
        Assert.Null(_store.Get(plain.Id));
        Assert.Null(_store.Get(queued.Id));
        Assert.Same(bobs, _store.Get(bobs.Id));
        Assert.True(File.Exists(bobsFile));
        Assert.Empty(_fileSystem.Deletes);
        Assert.Equal("alice", _store.KnownUsers.NameOf(_alice.Id));

        // A job claimed meanwhile stops, as for one delete; the other ids' jobs are left alone.
        _clips.Verify(c => c.StopRunning(withFile.Id), Times.Once);
        _clips.Verify(c => c.StopRunning(plain.Id), Times.Once);
        _clips.Verify(c => c.StopRunning(queued.Id), Times.Once);
        _clips.Verify(c => c.StopRunning(bobs.Id), Times.Never);
        _clips.Verify(c => c.StopRunning(tombstone.Id), Times.Never);
        _clips.Verify(c => c.CancelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WithTheClips_EachFileGoesFirst_ThenItsBookmark_AMissingFileCountsAsDeleted()
    {
        var (withFile, file) = await AddAsync(_alice, clipFile: true);
        var (plain, _) = await AddAsync(_alice);
        var (missing, missingFile) = await AddAsync(_alice, clipFile: true);
        File.Delete(missingFile);
        var (bobs, bobsFile) = await AddAsync(_bob, clipFile: true);

        var result = await _manager.BulkDeleteAsync(
            _alice,
            new BookmarkBulkDeleteRequest { Ids = [withFile.Id, plain.Id, missing.Id, bobs.Id], DeleteClips = true },
            Ct);

        Assert.Equal([withFile.Id, plain.Id, missing.Id], result.Deleted);
        Assert.Empty(result.Tombstoned);
        Assert.Equal([bobs.Id], result.NotFound);
        Assert.False(File.Exists(file));
        Assert.Null(_store.Get(withFile.Id));
        Assert.Null(_store.Get(plain.Id));
        Assert.Null(_store.Get(missing.Id));
        Assert.True(File.Exists(bobsFile));
        Assert.Same(bobs, _store.Get(bobs.Id));
        Assert.DoesNotContain(bobsFile, _fileSystem.Deletes);
        Assert.Equal(1, _store.DocumentWrites);
        _clips.Verify(c => c.StopRunning(withFile.Id), Times.Once);
        _clips.Verify(c => c.StopRunning(bobs.Id), Times.Never);
    }

    [Fact]
    public async Task WithTheClips_AFileInUse_IsTriedThreeTimes_ThenLeftWithItsBookmark_WhileTheOthersGo()
    {
        var (held, heldFile) = await AddAsync(_alice, clipFile: true);
        var (other, otherFile) = await AddAsync(_alice, clipFile: true);
        _fileSystem.HoldOpen(heldFile);

        var result = await _manager.BulkDeleteAsync(_alice, new BookmarkBulkDeleteRequest { Ids = [held.Id, other.Id], DeleteClips = true }, Ct);

        Assert.Equal([other.Id], result.Deleted);
        Assert.Equal([held.Id], result.InUse);
        Assert.Empty(result.Failed);
        Assert.Empty(result.NotFound);
        Assert.Equal(3, _fileSystem.Deletes.Count(p => string.Equals(p, heldFile, StringComparison.OrdinalIgnoreCase)));
        Assert.Same(held, _store.Get(held.Id));
        Assert.True(File.Exists(heldFile));
        Assert.False(File.Exists(otherFile));

        // One write per attempt: the first removes the other bookmark, the next two find the held file still in use.
        Assert.Equal(3, _store.DocumentWrites);
        _clips.Verify(c => c.StopRunning(held.Id), Times.Never);

        // Released, it goes.
        _fileSystem.Release();
        var again = await _manager.BulkDeleteAsync(_alice, new BookmarkBulkDeleteRequest { Ids = [held.Id], DeleteClips = true }, Ct);
        Assert.Equal([held.Id], again.Deleted);
        Assert.False(File.Exists(heldFile));
    }

    [Fact]
    public async Task WithTheClips_CancelledAfterTheFirstWrite_TheBookmarksItRemovedHaveTheirJobsStopped()
    {
        // The request is aborted (a closed tab) while a file in use waits for its next attempt. The first
        // write has removed the other bookmark, so its running job must stop then, not after attempts that never come.
        var (held, heldFile) = await AddAsync(_alice, clipFile: true);
        var (other, otherFile) = await AddAsync(_alice, clipFile: true);
        using var aborted = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _fileSystem.DeleteFailure = (path, _) =>
        {
            if (!string.Equals(path, heldFile, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Inside the first write, which the store always finishes: the pause after it is what the abort ends.
            aborted.Cancel();
            return FakeClipFileSystem.InUse(path);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _manager.BulkDeleteAsync(_alice, new BookmarkBulkDeleteRequest { Ids = [held.Id, other.Id], DeleteClips = true }, aborted.Token));

        Assert.Null(_store.Get(other.Id));
        Assert.False(File.Exists(otherFile));
        Assert.Same(held, _store.Get(held.Id));
        Assert.True(File.Exists(heldFile));
        Assert.Equal(1, _store.DocumentWrites);
        _clips.Verify(c => c.StopRunning(other.Id), Times.Once);
        _clips.Verify(c => c.StopRunning(held.Id), Times.Never);
    }

    [Fact]
    public async Task WithTheClips_AFileInUseOnlyBriefly_IsDeletedOnALaterAttempt()
    {
        var (bookmark, file) = await AddAsync(_alice, clipFile: true);
        _fileSystem.DeleteFailure = (p, attempt) => attempt < 2 ? FakeClipFileSystem.InUse(p) : null;

        var result = await _manager.BulkDeleteAsync(_alice, new BookmarkBulkDeleteRequest { Ids = [bookmark.Id], DeleteClips = true }, Ct);

        Assert.Equal([bookmark.Id], result.Deleted);
        Assert.False(File.Exists(file));
        Assert.Equal(2, _fileSystem.Deletes.Count);
    }

    [Fact]
    public async Task WithTheClips_APathOutsideTheClipFolders_OrAnUnpluggedFolder_IsFailed_AndNothingOfItIsTouched()
    {
        // Nothing the guard refuses is touched, and a clip folder that isn't there never loses a record.
        var outside = new TempDirectory();
        try
        {
            var outsideFile = outside.CreateFile("elsewhere.mp4");
            var (refused, _) = await AddAsync(_alice, clipFile: true, filePath: outsideFile);
            var unplugged = _directory.Combine("usb");
            Directory.CreateDirectory(unplugged);
            await _inner.AddClipRootAsync(unplugged, Ct);
            var (offline, offlineFile) = await AddAsync(_alice, clipFile: true, filePath: _directory.CreateFile(Path.Combine("usb", "clip.mkv")));
            var (fine, fineFile) = await AddAsync(_alice, clipFile: true);
            ClipFileSupport.Unplug(unplugged);

            var result = await _manager.BulkDeleteAsync(_alice, new BookmarkBulkDeleteRequest { Ids = [refused.Id, offline.Id, fine.Id], DeleteClips = true }, Ct);

            Assert.Equal([fine.Id], result.Deleted);
            Assert.Equal([refused.Id, offline.Id], result.Failed);
            Assert.Empty(result.InUse);
            Assert.Same(refused, _store.Get(refused.Id));
            Assert.Same(offline, _store.Get(offline.Id));
            Assert.True(File.Exists(outsideFile));
            Assert.False(File.Exists(fineFile));
            Assert.Equal([fineFile], _fileSystem.Deletes);

            ClipFileSupport.PlugIn(unplugged);
            Assert.True(File.Exists(offlineFile));
        }
        finally
        {
            outside.Dispose();
        }
    }

    [Fact]
    public async Task WithTheClips_AMissingFile_InAClipFolderThatIsntRecognised_IsFailed_AndItsBookmarkStays()
    {
        // A clip folder there without a marker of its own (an empty folder in place of the real one, a mount
        // point whose disk isn't mounted) isn't the clips' own, so a file gone from it isn't taken for gone: the bookmark
        // stays with its clip (Failed, which My Bookmarks words as "its folder isn't available"), as for an offline folder,
        // instead of being removed while its file may be fine in the real folder. A file that is there still goes, and the
        // bookmarks in the recognised folder go as before.
        var stray = _directory.Combine("stray");
        Directory.CreateDirectory(stray);
        await _inner.AddClipRootAsync(stray, Ct);
        var (gone, _) = await AddAsync(_alice, clipFile: true, filePath: Path.Combine(stray, "alice", "gone.mp4"));
        var (there, thereFile) = await AddAsync(_alice, clipFile: true, filePath: _directory.CreateFile(Path.Combine("stray", "alice", "there.mp4")));
        var (own, ownFile) = await AddAsync(_alice, clipFile: true);
        var (ownGone, ownGoneFile) = await AddAsync(_alice, clipFile: true);
        File.Delete(ownGoneFile);

        var result = await _manager.BulkDeleteAsync(_alice, new BookmarkBulkDeleteRequest { Ids = [gone.Id, there.Id, own.Id, ownGone.Id], DeleteClips = true }, Ct);

        Assert.Equal([gone.Id], result.Failed);
        Assert.Equal([there.Id, own.Id, ownGone.Id], result.Deleted);
        Assert.Empty(result.InUse);
        Assert.Empty(result.NotFound);
        Assert.Same(gone, _store.Get(gone.Id));
        Assert.False(File.Exists(thereFile));
        Assert.False(File.Exists(ownFile));
        _clips.Verify(c => c.StopRunning(gone.Id), Times.Never);

        // The folder confirmed (its marker written and recorded): the missing file is gone, and the bookmark with it.
        await ClipFileSupport.MarkClipFolderAsync(_inner, stray);
        var again = await _manager.BulkDeleteAsync(_alice, new BookmarkBulkDeleteRequest { Ids = [gone.Id], DeleteClips = true }, Ct);
        Assert.Equal([gone.Id], again.Deleted);
        Assert.Null(_store.Get(gone.Id));
    }

    [Fact]
    public async Task WithTheClips_AClipFolderThatComesBackAfterTheFirstCheck_LeavesTheBookmarkAndItsFile()
    {
        // Found in testing (the shared ClipFileBatchDelete): the answer to a clip folder found unplugged before the first write
        // came from a second look, which found it back and deleted the file outside the store's write while the bookmark,
        // reported Failed, still pointed at it. The answer is the first check's now; nothing is touched.
        var (bookmark, file) = await AddAsync(_alice, clipFile: true);
        var flaky = new FolderBackAfterFirstLook();
        var manager = new BookmarkManager(
            _store,
            Mock.Of<ILibraryManager>(),
            _clips.Object,
            Mock.Of<IClipPlanner>(),
            ClipFileSupport.Ops(_store, fileSystem: flaky),
            ClipFileSupport.Guard(_store),
            TestData.ConfigurationProvider(new PluginConfiguration()),
            NullLogger<BookmarkManager>.Instance);

        var result = await manager.BulkDeleteAsync(_alice, new BookmarkBulkDeleteRequest { Ids = [bookmark.Id], DeleteClips = true }, Ct);

        Assert.Equal([bookmark.Id], result.Failed);
        Assert.Empty(result.Deleted);
        Assert.Same(bookmark, _store.Get(bookmark.Id));
        Assert.True(File.Exists(file));
        Assert.Equal((1, 0), (flaky.FolderLooks, flaky.Deletes));
        _clips.Verify(c => c.StopRunning(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ABookmarkDeletedMeanwhile_KeepingItsClip_IsNotFound_AndItsFileStays()
    {
        // Each write checks the record again under the store's lock: another tab made it a tombstone between the request
        // and the write, so it isn't the caller's live bookmark any more.
        var (bookmark, file) = await AddAsync(_alice, clipFile: true);
        _store.BeforeNextDocumentWrite = () => _inner.UpdateAsync(bookmark.Id, b => BookmarkRecordRules.ToTombstone(b, DateTime.UtcNow)!, CancellationToken.None);

        var result = await _manager.BulkDeleteAsync(_alice, new BookmarkBulkDeleteRequest { Ids = [bookmark.Id], DeleteClips = true }, Ct);

        Assert.Equal([bookmark.Id], result.NotFound);
        Assert.True(BookmarkRecordRules.IsTombstone(_store.Get(bookmark.Id)!));
        Assert.True(File.Exists(file));
        Assert.Empty(_fileSystem.Deletes);
    }

    [Fact]
    public async Task TheIds_AreOneTo200_AnIdGivenTwiceCountingOnce()
    {
        var none = await Assert.ThrowsAsync<BookmarkException>(() => _manager.BulkDeleteAsync(_alice, new BookmarkBulkDeleteRequest { Ids = [] }, Ct));
        Assert.Equal(PluginErrorKind.Invalid, none.Kind);
        Assert.Equal("Choose at least one bookmark to delete.", none.Message);
        await Assert.ThrowsAsync<BookmarkException>(() => _manager.BulkDeleteAsync(_alice, new BookmarkBulkDeleteRequest(), Ct));

        var tooMany = await Assert.ThrowsAsync<BookmarkException>(
            () => _manager.BulkDeleteAsync(_alice, new BookmarkBulkDeleteRequest { Ids = Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToList() }, Ct));
        Assert.Equal("At most 200 bookmarks can be deleted at once.", tooMany.Message);

        var (bookmark, _) = await AddAsync(_alice);
        var ids = Enumerable.Range(0, 199).Select(_ => Guid.NewGuid()).Append(bookmark.Id).ToList();
        var result = await _manager.BulkDeleteAsync(_alice, new BookmarkBulkDeleteRequest { Ids = ids }, Ct);
        Assert.Equal([bookmark.Id], result.Deleted);
        Assert.Equal(199, result.NotFound.Count);

        var (twice, _) = await AddAsync(_alice);
        var repeated = await _manager.BulkDeleteAsync(_alice, new BookmarkBulkDeleteRequest { Ids = [twice.Id, twice.Id] }, Ct);
        Assert.Equal([twice.Id], repeated.Deleted);
        Assert.Empty(repeated.NotFound);
    }

    [Fact]
    public async Task ReadOnlyData_Is503_BeforeAnythingIsLookedUpOrTouched()
    {
        // A newer schema's file: the store's 503, the file and the clip file as they were.
        var file = _directory.Combine("readonly", "bookmarks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var clip = _directory.CreateFile(Path.Combine("clips", "kept.mp4"));
        var id = Guid.NewGuid();
        var document = new System.Text.Json.Nodes.JsonObject
        {
            ["Version"] = 3,
            ["ClipRoots"] = new System.Text.Json.Nodes.JsonArray(_root),
            ["Bookmarks"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["Id"] = id,
                ["UserId"] = _alice.Id,
                ["ItemId"] = Guid.NewGuid(),
                ["StartTicks"] = 0,
                ["ItemName"] = "Heat",
                ["Clip"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["Status"] = "Completed",
                    ["Mode"] = "Accurate",
                    ["StartTicks"] = 0,
                    ["EndTicks"] = 1,
                    ["RequestedAt"] = "2026-09-01T00:00:00Z",
                    ["FilePath"] = clip
                }
            })
        };
        await File.WriteAllTextAsync(file, document.ToJsonString(), Ct);
        var bytes = await File.ReadAllBytesAsync(file, Ct);
        using var readOnly = BookmarkStore.Create(file, NullLogger.Instance);
        var manager = CreateManager(readOnly);
        Assert.True(readOnly.IsReadOnly);

        foreach (var deleteClips in new[] { false, true })
        {
            var error = await Assert.ThrowsAnyAsync<PluginException>(
                () => manager.BulkDeleteAsync(_alice, new BookmarkBulkDeleteRequest { Ids = [id, Guid.NewGuid()], DeleteClips = deleteClips }, Ct));
            Assert.Equal(PluginErrorKind.Unavailable, error.Kind);
        }

        Assert.Equal(bytes, await File.ReadAllBytesAsync(file, Ct));
        Assert.True(File.Exists(clip));
        Assert.Empty(_fileSystem.Deletes);
        _clips.Verify(c => c.StopRunning(It.IsAny<Guid>()), Times.Never);
    }

    public void Dispose()
    {
        _inner.Dispose();
        _directory.Dispose();
    }

    private IBookmarkManager CreateManager(IBookmarkStore store) => new BookmarkManager(
        store,
        Mock.Of<ILibraryManager>(),
        _clips.Object,
        Mock.Of<IClipPlanner>(),
        ClipFileSupport.Ops(store, fileSystem: _fileSystem),
        ClipFileSupport.Guard(store),
        TestData.ConfigurationProvider(new PluginConfiguration()),
        NullLogger<BookmarkManager>.Instance);

    // A bookmark of the user's, optionally with a finished clip whose file is in the clip folder (or at the given path), or
    // with a clip job queued that has no file yet.
    private async Task<(Bookmark Bookmark, string File)> AddAsync(User user, bool clipFile = false, bool queuedWithoutFile = false, string? filePath = null)
    {
        var file = clipFile ? filePath ?? _directory.CreateFile(Path.Combine("clips", user.Username, $"{Guid.NewGuid():N}.mp4")) : string.Empty;
        var bookmark = TestData.CreateBookmark(user.Id) with
        {
            Clip = clipFile || queuedWithoutFile
                ? new ClipInfo
                {
                    Status = clipFile ? BookmarkClipStatus.Completed : BookmarkClipStatus.Queued,
                    Mode = BookmarkClipMode.Accurate,
                    StartTicks = 60 * TestData.OneSecond,
                    EndTicks = 90 * TestData.OneSecond,
                    RequestedAt = DateTime.UtcNow,
                    FilePath = clipFile ? file : null,
                    FileSize = clipFile ? 4 : null
                }
                : null
        };
        await _inner.AddAsync(bookmark, Ct, user);
        return (_store.Get(bookmark.Id)!, file);
    }
}
