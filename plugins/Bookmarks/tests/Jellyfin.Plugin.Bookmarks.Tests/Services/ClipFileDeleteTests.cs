using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
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
/// Clip file deletes through <see cref="IBookmarkManager"/>: every delete route acts on the clip file first and changes the
/// record only once the file is gone. A file in use after three attempts is 409 with the record, its clip job and its file
/// unchanged; a path outside the clip folders is 404 and nothing is touched; a missing file counts as
/// deleted; read-only data, a clip folder that can't be read and one that isn't there (an unplugged drive) never
/// lose a file or its record. The faults come from <see cref="FakeClipFileSystem"/>; <c>Api/ClipFileInUseApiTests</c> holds
/// real files open behind the web pipeline.
/// </summary>
public sealed class ClipFileDeleteTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly string _root;
    private readonly BookmarkStore _store;
    private readonly FakeClipFileSystem _fileSystem = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IClipExtractionService> _clips = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly Movie _movie = TestData.CreateMovie();
    private readonly IBookmarkManager _manager;

    public ClipFileDeleteTests()
    {
        _root = _directory.Combine("clips");
        Directory.CreateDirectory(_root);

        // The clip folder as the clip worker leaves it since 1.2.0: with its marker file, whose token the store
        // records (the clip routes take a missing file for gone only in such a folder).
        ClipFileSupport.MarkClipFolder(_root);
        _store = ClipFileSupport.StoreWithClipRoots(_directory.Combine("bookmarks.json"), _root);
        _libraryManager.Setup(m => m.GetItemById<Video>(_movie.Id, It.IsAny<User?>())).Returns(_movie);
        _manager = CreateManager(_store);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StoreFile => _directory.Combine("bookmarks.json");

    [Fact]
    public async Task DeleteClip_FinishesARenameTheServerStoppedDuring_First_ThenDeletesTheRenamedFile()
    {
        // The rename's entry holds both paths, so without finishing it first the delete is 503 and a
        // clip whose file moved can't be deleted until the next start.
        var (bookmark, file) = await AddAsync();
        var renamed = Path.Combine(Path.GetDirectoryName(file)!, "renamed " + Path.GetFileName(file));
        new ClipRenameJournal(_directory.Path, PhysicalClipFileSystem.Instance, NullLogger.Instance).Add(new ClipRenameEntry
        {
            BookmarkId = bookmark.Id,
            From = file,
            To = renamed,
            File = PhysicalClipFileSystem.ReadFileIdentity(file),
            UpdatedAt = bookmark.UpdatedAt,
            StartedUtc = DateTime.UtcNow
        });
        File.Move(file, renamed);

        var after = await CreateManager(_store).DeleteClipAsync(_alice, bookmark.Id, Ct);

        Assert.Null(after?.Clip);
        Assert.False(File.Exists(renamed));
        Assert.False(File.Exists(file));
        Assert.Empty(Directory.GetFiles(_directory.Path, ClipRenameJournal.FilePrefix + "*"));
    }

    [Fact]
    public async Task DeleteClip_OfAFileInUse_Is409_AndChangesNothing_NotEvenItsJob()
    {
        var (bookmark, file) = await AddAsync(queuedRemake: true);
        _fileSystem.HoldOpen(file);
        var bytes = await File.ReadAllBytesAsync(StoreFile, Ct);

        var error = await Assert.ThrowsAnyAsync<PluginException>(() => _manager.DeleteClipAsync(_alice, bookmark.Id, Ct));

        Assert.Equal(PluginErrorKind.Conflict, error.Kind);
        Assert.Equal("The clip file is in use (being played or downloaded); try again shortly.", error.Message);
        Assert.Equal(3, _fileSystem.Deletes.Count);
        Assert.Same(bookmark, _store.Get(bookmark.Id));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.True(File.Exists(file));
        _clips.Verify(c => c.CancelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _clips.Verify(c => c.StopRunning(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task DeleteBookmarkWithItsClip_OfAFileInUse_Is409_AndChangesNothing()
    {
        // 409 "clip file in use" when deleteClip=true and Windows refuses the delete (nothing changed).
        var (bookmark, file) = await AddAsync(queuedRemake: true);
        _fileSystem.HoldOpen(file);
        var bytes = await File.ReadAllBytesAsync(StoreFile, Ct);

        var error = await Assert.ThrowsAnyAsync<PluginException>(() => _manager.DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: true, Ct));

        Assert.Equal(PluginErrorKind.Conflict, error.Kind);
        Assert.Same(bookmark, _store.Get(bookmark.Id));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.True(File.Exists(file));
        _clips.Verify(c => c.CancelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _clips.Verify(c => c.StopRunning(It.IsAny<Guid>()), Times.Never);

        // Deleting it without the clip needs no file operation, and keeps the file in a tombstone.
        await _manager.DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: false, Ct);
        Assert.True(BookmarkRecordRules.IsTombstone(_store.Get(bookmark.Id)!));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task DeleteClip_OfATombstoneWhoseFileIsInUse_Is409_AndKeepsIt()
    {
        var (bookmark, file) = await AddAsync();
        var tombstone = await _store.UpdateAsync(bookmark.Id, b => BookmarkRecordRules.ToTombstone(b, DateTime.UtcNow)!, Ct);
        _fileSystem.HoldOpen(file);

        var error = await Assert.ThrowsAnyAsync<PluginException>(() => _manager.DeleteClipAsync(_alice, bookmark.Id, Ct));

        Assert.Equal(PluginErrorKind.Conflict, error.Kind);
        Assert.Same(tombstone, _store.Get(bookmark.Id));
        Assert.True(File.Exists(file));

        _fileSystem.Release();
        Assert.Null(await _manager.DeleteClipAsync(_alice, bookmark.Id, Ct));
        Assert.Null(_store.Get(bookmark.Id));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task AFileInUseOnlyBriefly_IsDeletedOnALaterAttempt_AndThenTheRecordChanges()
    {
        var (bookmark, file) = await AddAsync();
        _fileSystem.DeleteFailure = (p, attempt) => attempt < 3 ? FakeClipFileSystem.InUse(p) : null;

        var result = await _manager.DeleteClipAsync(_alice, bookmark.Id, Ct);

        Assert.NotNull(result);
        Assert.Null(result.Clip);
        Assert.Null(_store.Get(bookmark.Id)?.Clip);
        Assert.False(File.Exists(file));
        Assert.Equal(3, _fileSystem.Deletes.Count);
        _clips.Verify(c => c.CancelAsync(bookmark.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EachAttempt_ChecksTheRecordAgain_UnderTheStoresLock()
    {
        // Between two attempts another request deletes the bookmark keeping its clip: the next attempt sees a tombstone,
        // which isn't the caller's live bookmark any more, and neither deletes its file nor writes (404).
        var (bookmark, file) = await AddAsync();
        _fileSystem.HoldOpen(file);
        var racing = new InterferingBookmarkStore(_store);
        var manager = CreateManager(racing);
        _fileSystem.DeleteFailure = (p, attempt) =>
        {
            if (attempt == 1)
            {
                racing.BeforeNextWrite(b => BookmarkRecordRules.ToTombstone(b, DateTime.UtcNow));
            }

            return FakeClipFileSystem.InUse(p);
        };

        var error = await Assert.ThrowsAnyAsync<PluginException>(() => manager.DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: true, Ct));

        Assert.Equal(PluginErrorKind.NotFound, error.Kind);
        Assert.Single(_fileSystem.Deletes);
        Assert.True(BookmarkRecordRules.IsTombstone(_store.Get(bookmark.Id)!));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task AClipFileOutsideTheClipFolders_Is404ForEveryClipRoute_AndNothingIsTouched()
    {
        // An imported or hand-edited record: its file is never deleted or served.
        var outside = _directory.CreateFile(Path.Combine("library", "Heat (1995).mkv"));
        var (bookmark, _) = await AddAsync(filePath: outside);
        var bytes = await File.ReadAllBytesAsync(StoreFile, Ct);

        var deleteClip = await Assert.ThrowsAnyAsync<PluginException>(() => _manager.DeleteClipAsync(_alice, bookmark.Id, Ct));
        var deleteWithClip = await Assert.ThrowsAnyAsync<PluginException>(() => _manager.DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: true, Ct));
        var serve = Assert.ThrowsAny<PluginException>(() => _manager.GetClipFilePath(_alice, bookmark.Id));

        Assert.All([deleteClip, deleteWithClip, serve], e => Assert.Equal(PluginErrorKind.NotFound, e.Kind));
        Assert.Equal(ClipFileOps.NotInClipFoldersMessage, deleteClip.Message);
        Assert.Equal(ClipFileOps.NotInClipFoldersMessage, deleteWithClip.Message);
        Assert.Empty(_fileSystem.Deletes);
        Assert.True(File.Exists(outside));
        Assert.Same(bookmark, _store.Get(bookmark.Id));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(StoreFile, Ct));
    }

    [Fact]
    public async Task AMissingClipFile_CountsAsDeleted()
    {
        var gone = Path.Combine(_root, "alice", "gone.mp4");
        var (live, _) = await AddAsync(filePath: gone);
        var (tombstone, _) = await AddAsync(filePath: gone);
        await _store.UpdateAsync(tombstone.Id, b => BookmarkRecordRules.ToTombstone(b, DateTime.UtcNow)!, Ct);
        var (other, _) = await AddAsync(filePath: gone);

        var afterClip = await _manager.DeleteClipAsync(_alice, live.Id, Ct);
        var afterTombstone = await _manager.DeleteClipAsync(_alice, tombstone.Id, Ct);
        await _manager.DeleteBookmarkAsync(_alice, other.Id, deleteClipFile: true, Ct);

        Assert.Null(afterClip?.Clip);
        Assert.Null(afterTombstone);
        Assert.Null(_store.Get(tombstone.Id));
        Assert.Null(_store.Get(other.Id));
        Assert.Empty(_fileSystem.Deletes);
    }

    [Fact]
    public async Task AClipFolderThatCantBeReached_Is503_WithoutTakingTheStoresLock()
    {
        // An offline drive or share: answered before the store's write, so a slow drive never holds up other writes.
        var (bookmark, file) = await AddAsync();
        _fileSystem.AttributesFailure = _ => new IOException("The network path was not found.");
        var racing = new InterferingBookmarkStore(_store);

        var error = await Assert.ThrowsAnyAsync<PluginException>(() => CreateManager(racing).DeleteClipAsync(_alice, bookmark.Id, Ct));

        Assert.Equal(PluginErrorKind.Unavailable, error.Kind);
        Assert.Equal(ClipFileOps.FailedMessage, error.Message);
        Assert.Equal(0, racing.RecordWrites);
        Assert.Empty(_fileSystem.Deletes);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task AClipFolderThatIsntThere_Is503ForEveryDelete_AndNoRecordChanges()
    {
        // An unplugged drive (its letter gone) or a clip folder moved or removed. On the real file system its
        // files read as missing, which once counted as deleted: the clip entry, a tombstone with its name
        // and note, or the bookmark went, and the file stayed on the drive, untracked.
        var usb = _directory.Combine("usb");
        await _store.AddClipRootAsync(usb, Ct);
        var (live, liveFile) = await AddAsync(filePath: _directory.CreateFile(Path.Combine("usb", "alice", "live.mp4")));
        var (withClip, withClipFile) = await AddAsync(filePath: _directory.CreateFile(Path.Combine("usb", "alice", "with clip.mp4")));
        var (tombstone, keptFile) = await AddAsync(filePath: _directory.CreateFile(Path.Combine("usb", "alice", "kept.mkv")));
        await _store.UpdateAsync(tombstone.Id, b => BookmarkRecordRules.ToTombstone(b, DateTime.UtcNow)!, Ct);
        var manager = CreateManager(_store, PhysicalClipFileSystem.Instance);
        var bytes = await File.ReadAllBytesAsync(StoreFile, Ct);
        ClipFileSupport.Unplug(usb);

        var deleteClip = await Assert.ThrowsAnyAsync<PluginException>(() => manager.DeleteClipAsync(_alice, live.Id, Ct));
        var deleteWithClip = await Assert.ThrowsAnyAsync<PluginException>(() => manager.DeleteBookmarkAsync(_alice, withClip.Id, deleteClipFile: true, Ct));
        var deleteKept = await Assert.ThrowsAnyAsync<PluginException>(() => manager.DeleteClipAsync(_alice, tombstone.Id, Ct));

        Assert.All([deleteClip, deleteWithClip, deleteKept], e =>
        {
            Assert.Equal(PluginErrorKind.Unavailable, e.Kind);
            Assert.Equal(ClipFileOps.FolderUnavailableMessage, e.Message);
        });
        Assert.Equal(bytes, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.Empty(_store.PendingFileDeletes);
        _clips.Verify(c => c.CancelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _clips.Verify(c => c.StopRunning(It.IsAny<Guid>()), Times.Never);

        // Plugged in again: every file is where it was, and the deletes go through.
        ClipFileSupport.PlugIn(usb);
        Assert.All([liveFile, withClipFile, keptFile], f => Assert.True(File.Exists(f)));
        Assert.Null((await manager.DeleteClipAsync(_alice, live.Id, Ct))?.Clip);
        await manager.DeleteBookmarkAsync(_alice, withClip.Id, deleteClipFile: true, Ct);
        Assert.Null(await manager.DeleteClipAsync(_alice, tombstone.Id, Ct));
        Assert.All([liveFile, withClipFile, keptFile], f => Assert.False(File.Exists(f)));
        Assert.Null(_store.Get(withClip.Id));
        Assert.Null(_store.Get(tombstone.Id));
    }

    [Fact]
    public async Task ReadOnlyData_Is503_AndTheClipFileIsNeverTouched()
    {
        // The store refuses the write before the file is deleted: a file never goes while its record stays.
        using var directory = new TempDirectory();
        var root = directory.Combine("clips");
        var file = directory.CreateFile(Path.Combine("clips", "a.mp4"));
        var bookmark = TestData.CreateBookmark(_alice.Id, 60, 90, _movie.Id) with { Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = file } };
        var newer = System.Text.Json.JsonSerializer.Serialize(
            BookmarkStore.WithBookmarks(BookmarkStoreDocument.CreateEmpty() with { Version = 3, ClipRoots = [root] }, [bookmark]),
            Jellyfin.Plugin.Kit.Storage.JsonStoreFormat.Options);
        await File.WriteAllTextAsync(directory.Combine("bookmarks.json"), newer, Ct);
        using var store = BookmarkStore.Create(directory.Combine("bookmarks.json"), NullLogger.Instance);
        var manager = CreateManager(store);

        var deleteClip = await Assert.ThrowsAnyAsync<PluginException>(() => manager.DeleteClipAsync(_alice, bookmark.Id, Ct));
        var deleteWithClip = await Assert.ThrowsAnyAsync<PluginException>(() => manager.DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: true, Ct));

        Assert.True(store.IsReadOnly);
        Assert.Equal(PluginErrorKind.Unavailable, deleteClip.Kind);
        Assert.Equal(PluginErrorKind.Unavailable, deleteWithClip.Kind);
        Assert.Empty(_fileSystem.Deletes);
        Assert.True(File.Exists(file));
        Assert.Equal(file, manager.GetClipFilePath(_alice, bookmark.Id));
    }

    [Fact]
    public async Task ARunningJob_IsStoppedOnlyAfterItsClipIsGone()
    {
        // The file first, then the record, then the job: a running remake keeps its record (Running, without a file) so the
        // worker can report the cancellation.
        var (bookmark, file) = await AddAsync();
        await _store.UpdateAsync(bookmark.Id, b => b with { Clip = b.Clip! with { Status = BookmarkClipStatus.Running } }, Ct);
        var order = new System.Collections.Generic.List<string>();
        _clips.Setup(c => c.CancelAsync(bookmark.Id, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add(File.Exists(file) ? "cancel, file there" : "cancel, file gone"))
            .Returns(Task.CompletedTask);

        var result = await _manager.DeleteClipAsync(_alice, bookmark.Id, Ct);

        Assert.Equal(BookmarkClipStatus.Running, result?.Clip?.Status);
        Assert.Null(result?.Clip?.FileName);
        Assert.Equal(["cancel, file gone"], order);
    }

    [Fact]
    public async Task ForgetClip_OfAClipWhoseFileIsThere_Is409_AndNeverDeletesIt()
    {
        // Found in testing: the page's Forget ("Remove this entry; the file is already gone.") of a row that read Missing while a
        // remake had put the file back deleted that new file. Forget only forgets: a file that is there stays, with its record.
        var (bookmark, file) = await AddAsync();
        var (other, _) = await AddAsync();
        var tombstone = await _store.UpdateAsync(other.Id, b => BookmarkRecordRules.ToTombstone(b, DateTime.UtcNow)!, Ct);
        var bytes = await File.ReadAllBytesAsync(StoreFile, Ct);

        var live = await Assert.ThrowsAnyAsync<PluginException>(() => _manager.ForgetClipAsync(_alice, bookmark.Id, Ct));
        var kept = await Assert.ThrowsAnyAsync<PluginException>(() => _manager.ForgetClipAsync(_alice, tombstone!.Id, Ct));

        Assert.All([live, kept], error =>
        {
            Assert.Equal(PluginErrorKind.Conflict, error.Kind);
            Assert.Equal(BookmarkManager.ClipFileIsThereMessage, error.Message);
        });
        Assert.Empty(_fileSystem.Deletes);
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(tombstone!.Clip!.FilePath));
        Assert.Same(bookmark, _store.Get(bookmark.Id));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(StoreFile, Ct));
        _clips.Verify(c => c.CancelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ForgetClip_OfAMissingFile_OrNone_ForgetsTheClip_WithoutAnyDelete()
    {
        // Forget (File missing) and Dismiss (a failed clip without a file) use the same route as Delete; with
        // onlyIfMissing they change the record as the delete would, and touch no file.
        var (missing, missingFile) = await AddAsync();
        File.Delete(missingFile);
        var (keptMissing, keptFile) = await AddAsync();
        await _store.UpdateAsync(keptMissing.Id, b => BookmarkRecordRules.ToTombstone(b, DateTime.UtcNow)!, Ct);
        File.Delete(keptFile);
        var (failed, failedFile) = await AddAsync();
        File.Delete(failedFile);
        await _store.UpdateAsync(failed.Id, b => b with { Clip = b.Clip! with { Status = BookmarkClipStatus.Failed, FilePath = null, FileSize = null } }, Ct);

        var forgotten = await _manager.ForgetClipAsync(_alice, missing.Id, Ct);
        var removed = await _manager.ForgetClipAsync(_alice, keptMissing.Id, Ct);
        var dismissed = await _manager.ForgetClipAsync(_alice, failed.Id, Ct);

        Assert.Null(forgotten!.Clip);
        Assert.Null(_store.Get(missing.Id)!.Clip);
        Assert.Null(removed);
        Assert.Null(_store.Get(keptMissing.Id));
        Assert.Null(dismissed!.Clip);
        Assert.Empty(_fileSystem.Deletes);
    }

    [Fact]
    public async Task ForgetClip_InAClipFolderThatIsntThere_Is503_AndOutsideTheClipFolders404_AsTheDeleteIs()
    {
        var usb = _directory.Combine("usb");
        var offlineFile = _directory.CreateFile(Path.Combine("usb", "alice", "a.mp4"));
        await _store.AddClipRootAsync(usb, Ct);
        var (offline, _) = await AddAsync(filePath: offlineFile);
        var (outside, _) = await AddAsync(filePath: _directory.CreateFile(Path.Combine("elsewhere", "b.mp4")));
        ClipFileSupport.Unplug(usb);

        var unavailable = await Assert.ThrowsAnyAsync<PluginException>(() => _manager.ForgetClipAsync(_alice, offline.Id, Ct));
        var refused = await Assert.ThrowsAnyAsync<PluginException>(() => _manager.ForgetClipAsync(_alice, outside.Id, Ct));

        Assert.Equal(PluginErrorKind.Unavailable, unavailable.Kind);
        Assert.Equal(ClipFileOps.FolderUnavailableMessage, unavailable.Message);
        Assert.Equal(PluginErrorKind.NotFound, refused.Kind);
        Assert.Same(offline, _store.Get(offline.Id));
        Assert.Same(outside, _store.Get(outside.Id));
        ClipFileSupport.PlugIn(usb);
        Assert.True(File.Exists(offlineFile));
    }

    [Fact]
    public async Task EveryDeleteWithTheClip_InAClipFolderThatIsntRecognised_LeavesAMissingFile_AsItIs_503_ButAFileThatIsThereStillGoes()
    {
        // A folder there without a marker of its own (an empty mount point, a folder in
        // place of the real one) isn't taken for the clips' folder: its missing file isn't gone, so neither the page's Forget
        // nor the clip's delete forgets it (503, nothing changed); a file that is there is deleted as anywhere. Found in testing:
        // the bookmark's own delete with its clip (DELETE /Bookmarks/{id}?deleteClip=true, reachable from a My Clips row's
        // Edit bookmark) took the file for gone and removed the record, while the same request for an offline folder changed
        // nothing; it answers as the clip routes do now.
        var stray = _directory.Combine("stray");
        var strayFile = _directory.CreateFile(Path.Combine("stray", "alice", "there.mp4"));
        await _store.AddClipRootAsync(stray, Ct);
        var (forget, _) = await AddAsync(filePath: Path.Combine(stray, "alice", "gone-1.mp4"));
        var (delete, _) = await AddAsync(filePath: Path.Combine(stray, "alice", "gone-2.mp4"));
        var (present, _) = await AddAsync(filePath: strayFile);
        var (bookmarkDelete, _) = await AddAsync(filePath: Path.Combine(stray, "alice", "gone-3.mp4"));
        var (kept, _) = await AddAsync(filePath: Path.Combine(stray, "alice", "gone-4.mp4"));
        var bytes = await File.ReadAllBytesAsync(StoreFile, Ct);

        var forgot = await Assert.ThrowsAnyAsync<PluginException>(() => _manager.ForgetClipAsync(_alice, forget.Id, Ct));
        var deleted = await Assert.ThrowsAnyAsync<PluginException>(() => _manager.DeleteClipAsync(_alice, delete.Id, Ct));
        var deletedWithClip = await Assert.ThrowsAnyAsync<PluginException>(() => _manager.DeleteBookmarkAsync(_alice, bookmarkDelete.Id, deleteClipFile: true, Ct));

        Assert.All([forgot, deleted, deletedWithClip], e =>
        {
            Assert.Equal(PluginErrorKind.Unavailable, e.Kind);
            Assert.Equal(ClipFileOps.FolderUnrecognisedMessage, e.Message);
        });
        Assert.Equal(bytes, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.Empty(_fileSystem.Deletes);
        _clips.Verify(c => c.CancelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _clips.Verify(c => c.StopRunning(It.IsAny<Guid>()), Times.Never);

        // A file that is there is deleted (the folder is there; the file is the clip's).
        Assert.Null((await _manager.DeleteClipAsync(_alice, present.Id, Ct))?.Clip);
        Assert.False(File.Exists(strayFile));

        // Deleting the bookmark and keeping its clip asks the file system nothing: the tombstone keeps the path, whatever the
        // folder is, so nothing can lose the record of a file that is fine elsewhere.
        await _manager.DeleteBookmarkAsync(_alice, kept.Id, deleteClipFile: false, Ct);
        Assert.True(BookmarkRecordRules.IsTombstone(_store.Get(kept.Id)!));
        Assert.Equal(kept.Clip!.FilePath, _store.Get(kept.Id)!.Clip!.FilePath);

        // Once the folder carries its marker (an admin's confirmation, or the worker's), the missing file is gone.
        await ClipFileSupport.MarkClipFolderAsync(_store, stray);
        Assert.Null((await _manager.ForgetClipAsync(_alice, forget.Id, Ct))!.Clip);
        Assert.Null((await _manager.DeleteClipAsync(_alice, delete.Id, Ct))?.Clip);
        await _manager.DeleteBookmarkAsync(_alice, bookmarkDelete.Id, deleteClipFile: true, Ct);
        Assert.Null(_store.Get(bookmarkDelete.Id));
        Assert.DoesNotContain(_fileSystem.Deletes, p => p.Contains("gone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEmptyFolderInPlaceOfTheClipFolder_NeverLosesTheRecord_OfAFileThatComesBackWithTheRealOne()
    {
        // The real clip folder (marked, its token recorded) away, an empty folder made at its path (a
        // drive letter or mount point reused, a folder made by hand). Its clips' files read as missing, but the folder isn't
        // the clips' own: deleting the bookmark with its clip is 503 with nothing changed, as for an offline folder. When the
        // real folder is back, the record still points at its file, and the delete then deletes it.
        var (bookmark, file) = await AddAsync();
        var manager = CreateManager(_store, PhysicalClipFileSystem.Instance);
        var bytes = await File.ReadAllBytesAsync(StoreFile, Ct);
        ClipFileSupport.Unplug(_root);
        Directory.CreateDirectory(_root);

        var error = await Assert.ThrowsAnyAsync<PluginException>(() => manager.DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: true, Ct));

        Assert.Equal(PluginErrorKind.Unavailable, error.Kind);
        Assert.Equal(ClipFileOps.FolderUnrecognisedMessage, error.Message);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.Empty(_store.PendingFileDeletes);

        // The real folder back: the file is there, and the bookmark's record still names it.
        Directory.Delete(_root);
        ClipFileSupport.PlugIn(_root);
        Assert.True(File.Exists(file));
        Assert.Equal(file, _store.Get(bookmark.Id)!.Clip!.FilePath);
        await manager.DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: true, Ct);
        Assert.Null(_store.Get(bookmark.Id));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task ADeleteRefusedForItsClipFolder_HasTheClipListJudgeThatClipAgainAtOnce()
    {
        // Found in testing: the list read a clip Missing while its folder was recognised; the real folder then went away and an
        // empty one took its place. The page's Forget answered 503, but the row it read again showed the "missing" the list
        // had kept, with Forget, for up to the cache's 30 s. A delete or Forget refused for its clip folder (not recognised,
        // or not there) has the list judge that clip again at once.
        var states = new ClipStateCache(_store);
        var path = Path.Combine(_root, "alice", "gone.mp4");
        var (bookmark, _) = await AddAsync(filePath: path);
        var manager = CreateManager(_store, PhysicalClipFileSystem.Instance, states);
        Assert.Equal((ClipPathVerdict.Missing, false), (states.Read().Check(path).Verdict, states.Read().Check(path).RootUnrecognised));
        ClipFileSupport.Unplug(_root);
        Directory.CreateDirectory(_root);
        Assert.False(states.Read().Check(path).RootUnrecognised, "the verdict kept from before");

        var forgot = await Assert.ThrowsAnyAsync<PluginException>(() => manager.ForgetClipAsync(_alice, bookmark.Id, Ct));

        Assert.Equal(ClipFileOps.FolderUnrecognisedMessage, forgot.Message);
        Assert.Equal((ClipPathVerdict.Missing, true), (states.Read().Check(path).Verdict, states.Read().Check(path).RootUnrecognised));

        // The empty folder gone too (the drive's letter away): the delete's 503 has the list read the folder offline at once.
        Directory.Delete(_root);
        var deleted = await Assert.ThrowsAnyAsync<PluginException>(() => manager.DeleteClipAsync(_alice, bookmark.Id, Ct));
        Assert.Equal(ClipFileOps.FolderUnavailableMessage, deleted.Message);
        Assert.Equal(ClipPathVerdict.FolderUnavailable, states.Read().Check(path).Verdict);
        ClipFileSupport.PlugIn(_root);
        Assert.NotNull(_store.Get(bookmark.Id)!.Clip);
    }

    [Fact]
    public async Task AClipFolderThatComesBackAfterTheFirstCheck_NeverHasItsFileDeleted_OutsideTheStoresWrite()
    {
        // Found in testing: the check before the store's lock found the clip folder unplugged, and the answer came from a second
        // look (DeleteOnce), which found the folder back and deleted the file, outside the store's write; its Done reached
        // ToRequestError's default arm (ArgumentException, a 500). The answer is the first check's: 503, nothing touched.
        var (bookmark, file) = await AddAsync();
        var flaky = new FolderBackAfterFirstLook();
        var manager = CreateManager(_store, flaky);

        var error = await Assert.ThrowsAnyAsync<PluginException>(() => manager.DeleteClipAsync(_alice, bookmark.Id, Ct));

        Assert.Equal(PluginErrorKind.Unavailable, error.Kind);
        Assert.Equal(ClipFileOps.FolderUnavailableMessage, error.Message);
        Assert.True(File.Exists(file));
        Assert.Same(bookmark, _store.Get(bookmark.Id));
        Assert.Equal((1, 0), (flaky.FolderLooks, flaky.Deletes));

        // The same for a bookmark deleted with its clip; with the folder there, the next request deletes the clip.
        var flakyAgain = new FolderBackAfterFirstLook();
        var withClip = await Assert.ThrowsAnyAsync<PluginException>(() => CreateManager(_store, flakyAgain).DeleteBookmarkAsync(_alice, bookmark.Id, deleteClipFile: true, Ct));
        Assert.Equal(PluginErrorKind.Unavailable, withClip.Kind);
        Assert.True(File.Exists(file));
        Assert.Equal(0, flakyAgain.Deletes);
        Assert.Null((await manager.DeleteClipAsync(_alice, bookmark.Id, Ct))?.Clip);
        Assert.False(File.Exists(file));
    }

    public void Dispose()
    {
        _store.Dispose();
        _directory.Dispose();
    }

    private IBookmarkManager CreateManager(IBookmarkStore store, IClipFileSystem? fileSystem = null, ClipStateCache? states = null) => new BookmarkManager(
        store,
        _libraryManager.Object,
        _clips.Object,
        Mock.Of<IClipPlanner>(),
        ClipFileSupport.Ops(store, fileSystem: fileSystem ?? _fileSystem, states: states),
        ClipFileSupport.Guard(store),
        TestData.ConfigurationProvider(new PluginConfiguration()),
        NullLogger<BookmarkManager>.Instance);

    // Alice's bookmark with a finished clip whose file is in the clip folder (or at the given path), optionally with a
    // remake queued.
    private async Task<(Bookmark Bookmark, string File)> AddAsync(bool queuedRemake = false, string? filePath = null)
    {
        var file = filePath ?? _directory.CreateFile(Path.Combine("clips", "alice", $"{Guid.NewGuid():N}.mp4"));
        var bookmark = TestData.CreateBookmark(_alice.Id, 60, 90, _movie.Id) with
        {
            Clip = new ClipInfo
            {
                Status = queuedRemake ? BookmarkClipStatus.Queued : BookmarkClipStatus.Completed,
                Mode = BookmarkClipMode.Accurate,
                StartTicks = 60 * TestData.OneSecond,
                EndTicks = 90 * TestData.OneSecond,
                RequestedAt = DateTime.UtcNow,
                FilePath = file,
                FileSize = 4
            }
        };
        await _store.AddAsync(bookmark, Ct);
        return (_store.Get(bookmark.Id)!, file);
    }
}
