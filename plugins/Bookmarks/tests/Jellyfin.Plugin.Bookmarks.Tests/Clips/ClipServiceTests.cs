using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// <see cref="IBookmarkClips"/> (query, derived fields, delete) over real files:
/// only the caller's clips, mapped with the file's name and no path; <c>ItemAvailable</c> asked as the caller, once per video
/// on the page, never for a detached bookmark or an adopted clip (the mock throws on the empty GUID as 10.11.5 does); the In
/// library badge with the library's name only for a library the caller may open; the summary; the status of some clips; and
/// the bulk delete, file first: a live bookmark keeps no clip, a tombstone goes, a clip being made, a file in use and a folder
/// offline (also one that comes back after the first check) leave their clips as they were, other users' ids are not found.
/// </summary>
public sealed class ClipServiceTests : IDisposable
{
    private static readonly Guid ClipsLibraryId = Guid.Parse("c11b5e1b0b0a4c6e8f3a2d1c0b9a8e7f");

    private readonly ClipListFixture _fixture = new();
    private readonly FakeClipFileSystem _fileSystem = new();
    private readonly Mock<IClipExtractionService> _clips = new();
    private readonly Mock<ILibraryManager> _library = new(MockBehavior.Strict);
    private readonly HashSet<Guid> _visible = [];
    private readonly HashSet<Guid> _openLibraries = [];
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly User _bob = TestData.CreateUser("bob");
    private List<VirtualFolderInfo> _virtualFolders = [];

    public ClipServiceTests()
    {
        // Only the per-user lookup; the empty GUID throws as on 10.11.5 (LibraryManager.cs:1357-1362).
        _library.Setup(m => m.GetItemById<Video>(It.IsAny<Guid>(), It.IsAny<User?>()))
            .Returns((Guid id, User? user) => id == Guid.Empty
                ? throw new ArgumentException("Guid can't be empty", nameof(id))
                : user is not null && _visible.Contains(id) ? new MediaBrowser.Controller.Entities.Movies.Movie { Id = id } : null);
        _library.Setup(m => m.GetVirtualFolders()).Returns(() => _virtualFolders);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetClips_MapsTheCallersClips_TheFileByNameOnly_AndKeptOnesAsDeletedBookmarks()
    {
        var live = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job", Note = "Watch the window", MediaSourceId = "ms1", AudioStreamIndex = 2, Mode = BookmarkClipMode.Fast }, Ct);
        var kept = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Kept", Tombstone = true }, Ct);
        await _fixture.AddAsync(_bob.Id, new ClipSpec { Name = "Bob's", UserFolder = "bob" }, Ct);
        _visible.Add(live.ItemId);

        var result = Service().GetClips(_alice, new BookmarkClipQueryFilter(), null);

        Assert.Equal(2, result.TotalRecordCount);
        Assert.Equal(0, result.StartIndex);
        Assert.Equal([kept.Id, live.Id], result.Items.Select(i => i.Id));
        var row = result.Items[1];
        Assert.False(row.BookmarkDeleted);
        Assert.Equal("Bank job", row.Name);
        Assert.Equal("Watch the window", row.Note);
        Assert.Equal(live.ItemId, row.ItemId);
        Assert.Equal("Heat (1995)", row.ItemName);
        Assert.Equal("Movie", row.ItemType);
        Assert.True(row.ItemAvailable);
        Assert.Equal("ms1", row.MediaSourceId);
        Assert.Equal(2, row.AudioStreamIndex);
        Assert.Equal(live.StartTicks, row.BookmarkStartTicks);
        Assert.Equal(live.EndTicks, row.BookmarkEndTicks);
        Assert.Equal(BookmarkClipState.Ready, row.Clip.State);
        Assert.Equal(BookmarkClipMode.Fast, row.Clip.Mode);
        Assert.Equal("mkv", row.Clip.Container);
        Assert.True(row.Clip.FileExists);
        Assert.True(row.Clip.RootAvailable);
        Assert.False(row.Clip.InLibrary);
        Assert.Null(row.Clip.LibraryName);
        Assert.False(row.Clip.IsNameOutdated);
        Assert.False(row.Clip.LastJobFailed);
        Assert.Equal(30 * TestData.OneSecond, row.Clip.DurationTicks);
        Assert.Equal(live.Clip!.CompletedAt, row.Clip.MadeAt);
        Assert.Equal(Path.GetFileName(live.Clip.FilePath), row.Clip.FileName);
        Assert.Equal(1000, row.Clip.FileSize);

        Assert.True(result.Items[0].BookmarkDeleted);
        Assert.False(result.Items[0].ItemAvailable);

        // No server path in any of it: the clip folder, the user's folder and the video's path are the admin's.
        var json = JsonSerializer.Serialize(result.Items);
        Assert.DoesNotContain(JsonSerializer.Serialize(_fixture.Root).Trim('"'), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\\media", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ItemAvailable_IsAskedAsTheCaller_OncePerVideoOnThePage_NeverForADetachedOrAdoptedClip()
    {
        var item = Guid.NewGuid();
        var one = await _fixture.AddAsync(_alice.Id, new ClipSpec { ItemId = item, Name = "one" }, Ct);
        var two = await _fixture.AddAsync(_alice.Id, new ClipSpec { ItemId = item, Name = "two" }, Ct);
        var hidden = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "hidden" }, Ct);
        var detached = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "detached", Detached = ClipListFixture.T0 }, Ct);
        var adopted = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "adopted", Origin = BookmarkOrigin.Adopted, Tombstone = true }, Ct);
        var broken = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "broken" }, Ct);
        _visible.UnionWith([item, detached.ItemId]);
        _library.Setup(m => m.GetItemById<Video>(broken.ItemId, It.IsAny<User?>())).Throws(new InvalidOperationException("broken item"));

        var rows = Service().GetClips(_alice, new BookmarkClipQueryFilter(), BookmarkClipSortBy.Name).Items.ToDictionary(r => r.Id);

        Assert.True(rows[one.Id].ItemAvailable);
        Assert.True(rows[two.Id].ItemAvailable);
        Assert.False(rows[hidden.Id].ItemAvailable);
        Assert.False(rows[detached.Id].ItemAvailable);
        Assert.False(rows[adopted.Id].ItemAvailable);
        Assert.False(rows[broken.Id].ItemAvailable);
        _library.Verify(m => m.GetItemById<Video>(item, _alice), Times.Once);
        _library.Verify(m => m.GetItemById<Video>(detached.ItemId, It.IsAny<User?>()), Times.Never);
        _library.Verify(m => m.GetItemById<Video>(Guid.Empty, It.IsAny<User?>()), Times.Never);
        _library.Verify(m => m.GetItemById<Video>(It.IsAny<Guid>(), null), Times.Never);

        // Only the page's videos: a page of one asks for one.
        _library.Invocations.Clear();
        Service().GetClips(_alice, new BookmarkClipQueryFilter { Limit = 1 }, BookmarkClipSortBy.Name);
        _library.Verify(m => m.GetItemById<Video>(It.IsAny<Guid>(), It.IsAny<User?>()), Times.AtMostOnce);

        // Never kept past the request: access taken away shows at once.
        _visible.Remove(item);
        Assert.False(Service().GetClips(_alice, new BookmarkClipQueryFilter(), null).Items.Single(r => r.Id == one.Id).ItemAvailable);
    }

    [Fact]
    public async Task InLibrary_ForAClipInALibrarysFolder_WithTheLibrarysName_OnlyWhenTheCallerMayOpenIt()
    {
        var inLibrary = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "in library" }, Ct);
        var elsewhere = _fixture.Directory.Combine("private-clips");
        Directory.CreateDirectory(elsewhere);
        await _fixture.Store.AddClipRootAsync(elsewhere, Ct);
        var outside = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "outside", Folder = Path.Combine(elsewhere, "alice") }, Ct);
        _virtualFolders =
        [
            new VirtualFolderInfo { Name = "Clips", Locations = [_fixture.Root.ToUpperInvariant()], CollectionType = CollectionTypeOptions.homevideos, ItemId = ClipsLibraryId.ToString("N") }
        ];

        _openLibraries.Add(ClipsLibraryId);
        var seen = Service().GetClips(_alice, new BookmarkClipQueryFilter(), null).Items.ToDictionary(r => r.Id);
        Assert.True(seen[inLibrary.Id].Clip.InLibrary);
        Assert.Equal("Clips", seen[inLibrary.Id].Clip.LibraryName);
        Assert.False(seen[outside.Id].Clip.InLibrary);
        Assert.Null(seen[outside.Id].Clip.LibraryName);

        // A library the caller can't open: the clip is still in a library everyone with access to it sees, but its name is
        // not the caller's to know (the kit's privacy rule: only the caller's own library access).
        _openLibraries.Clear();
        var unseen = Service().GetClips(_alice, new BookmarkClipQueryFilter(), null).Items.Single(r => r.Id == inLibrary.Id);
        Assert.True(unseen.Clip.InLibrary);
        Assert.Null(unseen.Clip.LibraryName);
    }

    [Fact]
    public async Task GetSummary_CountsOnlyTheCallersClips()
    {
        await _fixture.AddAsync(_alice.Id, new ClipSpec { Size = 10 }, Ct);
        await _fixture.AddAsync(_alice.Id, new ClipSpec { Size = 20, Tombstone = true }, Ct);
        await _fixture.AddAsync(_alice.Id, new ClipSpec { Status = BookmarkClipStatus.Queued, HasFile = false }, Ct);
        await _fixture.AddAsync(_bob.Id, new ClipSpec { Size = 999, UserFolder = "bob" }, Ct);

        var summary = Service().GetSummary(_alice);

        Assert.Equal(3, summary.Count);
        Assert.Equal(30, summary.TotalBytes);
        Assert.Equal(1, summary.InProgress);
        Assert.Equal(0, summary.Failed + summary.Missing + summary.FolderUnavailable);
        Assert.Equal(0, Service().GetSummary(TestData.CreateUser("carol")).Count);
    }

    [Fact]
    public async Task GetStatus_AnswersTheCallersClips_InTheOrderAsked_LeavingEveryOtherIdOut()
    {
        var running = await _fixture.AddAsync(_alice.Id, new ClipSpec { Status = BookmarkClipStatus.Running }, Ct);
        var ready = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "ready" }, Ct);
        var kept = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "kept", Tombstone = true }, Ct);
        var noClip = await _fixture.AddAsync(_alice.Id, new ClipSpec { NoClip = true }, Ct);
        var bobs = await _fixture.AddAsync(_bob.Id, new ClipSpec { UserFolder = "bob" }, Ct);
        _clips.Setup(c => c.GetProgress(running.Id)).Returns(0.4333);

        var status = Service().GetStatus(_alice, [ready.Id, bobs.Id, running.Id, Guid.NewGuid(), noClip.Id, ready.Id, kept.Id]);

        Assert.Equal([ready.Id, running.Id, kept.Id], status.Select(s => s.Id));
        Assert.Equal(BookmarkClipState.Ready, status[0].State);
        Assert.Equal(BookmarkClipStatus.Completed, status[0].Status);
        Assert.Null(status[0].Progress);
        Assert.Equal(Path.GetFileName(ready.Clip!.FilePath), status[0].FileName);
        Assert.Equal(BookmarkClipState.InProgress, status[1].State);
        Assert.Equal(BookmarkClipStatus.Running, status[1].Status);
        Assert.Equal(43.3, status[1].Progress);
        Assert.Equal(BookmarkClipState.Ready, status[2].State);

        foreach (var (ids, message) in new (IReadOnlyList<Guid>?, string)[]
        {
            (null, ClipService.StatusNoIdsMessage),
            ([], ClipService.StatusNoIdsMessage),
            (Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToList(), ClipService.StatusTooManyMessage)
        })
        {
            var error = Assert.Throws<BookmarkException>(() => Service().GetStatus(_alice, ids));
            Assert.Equal(PluginErrorKind.Invalid, error.Kind);
            Assert.Equal(message, error.Message);
        }

        // 200 ids are fine (none of them a clip of the caller here).
        Assert.Empty(Service().GetStatus(_alice, Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToList()));
    }

    [Fact]
    public async Task BulkDelete_FileFirst_ALiveBookmarkKeepsNoClip_AKeptClipGoes_AndEveryOtherIdIsNotFound()
    {
        var ready = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "ready" }, Ct);
        var kept = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "kept", Tombstone = true }, Ct);
        var missing = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "missing", OnDisk = false }, Ct);
        var failed = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "failed", Status = BookmarkClipStatus.Failed, HasFile = false }, Ct);
        var keptMissing = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "kept missing", Tombstone = true, OnDisk = false }, Ct);
        var noClip = await _fixture.AddAsync(_alice.Id, new ClipSpec { NoClip = true }, Ct);
        var bobs = await _fixture.AddAsync(_bob.Id, new ClipSpec { Name = "bob's", UserFolder = "bob" }, Ct);
        var unknown = Guid.NewGuid();

        var result = await Service().BulkDeleteAsync(
            _alice,
            new BookmarkClipBulkDeleteRequest { Ids = [kept.Id, bobs.Id, ready.Id, unknown, missing.Id, ready.Id, failed.Id, noClip.Id, keptMissing.Id] },
            Ct);

        Assert.Equal([ready.Id, missing.Id, failed.Id], result.FilesDeleted);
        Assert.Equal([kept.Id, keptMissing.Id], result.RecordsRemoved);
        Assert.Equal([bobs.Id, unknown, noClip.Id], result.NotFound);
        Assert.Empty(result.InProgress);
        Assert.Empty(result.InUse);
        Assert.Empty(result.FolderUnavailable);
        Assert.Empty(result.Failed);

        // Live bookmarks stay without their clips (as DELETE /Bookmarks/{id}/Clip leaves them); the kept clips are gone.
        foreach (var live in new[] { ready, missing, failed })
        {
            var stored = _fixture.Store.Get(live.Id);
            Assert.NotNull(stored);
            Assert.Null(stored.Clip);
            Assert.Equal(live.Name, stored.Name);
        }

        Assert.Null(_fixture.Store.Get(kept.Id));
        Assert.Null(_fixture.Store.Get(keptMissing.Id));
        Assert.False(File.Exists(ready.Clip!.FilePath));
        Assert.False(File.Exists(kept.Clip!.FilePath));

        // Nothing of the others changed: Bob's clip, the bookmark without a clip.
        Assert.Equal(bobs, _fixture.Store.Get(bobs.Id));
        Assert.True(File.Exists(bobs.Clip!.FilePath));
        Assert.Equal(noClip, _fixture.Store.Get(noClip.Id));
    }

    [Fact]
    public async Task BulkDelete_LeavesAFileInUse_AFolderOffline_AndAPathOutsideTheClipFolders_AsTheyWere()
    {
        var offline = _fixture.Directory.Combine("usb");
        Directory.CreateDirectory(offline);
        await _fixture.Store.AddClipRootAsync(offline, Ct);
        var inUse = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "in use" }, Ct);
        var unplugged = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "unplugged", Folder = Path.Combine(offline, "alice") }, Ct);
        var outside = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "outside", Folder = _fixture.Directory.Combine("elsewhere") }, Ct);
        var ok = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "ok" }, Ct);
        _fileSystem.HoldOpen(inUse.Clip!.FilePath!);
        ClipFileSupport.Unplug(offline);

        var result = await Service().BulkDeleteAsync(_alice, new BookmarkClipBulkDeleteRequest { Ids = [inUse.Id, unplugged.Id, outside.Id, ok.Id] }, Ct);

        Assert.Equal([ok.Id], result.FilesDeleted);
        Assert.Equal([inUse.Id], result.InUse);
        Assert.Equal([unplugged.Id], result.FolderUnavailable);
        Assert.Equal([outside.Id], result.Failed);
        Assert.Empty(result.RecordsRemoved);
        Assert.Empty(result.NotFound);

        // The file in use was asked three times, 5 ms apart here; the offline and outside files never.
        Assert.Equal(ClipFileOps.Attempts, _fileSystem.Deletes.Count(p => string.Equals(p, inUse.Clip.FilePath, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(_fileSystem.Deletes, p => p.StartsWith(offline, StringComparison.OrdinalIgnoreCase) || p.Contains("elsewhere", StringComparison.Ordinal));
        Assert.Equal(inUse, _fixture.Store.Get(inUse.Id));
        Assert.Equal(unplugged, _fixture.Store.Get(unplugged.Id));
        Assert.Equal(outside, _fixture.Store.Get(outside.Id));
        Assert.True(File.Exists(inUse.Clip.FilePath));
        Assert.True(File.Exists(outside.Clip!.FilePath));
        Assert.True(File.Exists(Path.Combine(offline + "-unplugged", "alice", Path.GetFileName(unplugged.Clip!.FilePath)!)));

        // Released and plugged in again, they go.
        _fileSystem.Release();
        ClipFileSupport.PlugIn(offline);
        var again = await Service().BulkDeleteAsync(_alice, new BookmarkClipBulkDeleteRequest { Ids = [inUse.Id, unplugged.Id] }, Ct);
        Assert.Equal([inUse.Id, unplugged.Id], again.FilesDeleted);
        Assert.False(File.Exists(inUse.Clip.FilePath));
        Assert.False(File.Exists(unplugged.Clip.FilePath));
    }

    [Fact]
    public async Task BulkDelete_KeepsAFileAnotherRecordStillNames_AndDeletesOneOnlyItsOwnClipsName()
    {
        // Bookmarks 1.1.3's defence in depth on My Clips: two of the caller's clips naming one file (as 1.1.2 could leave
        // them) let go of it together, so it is deleted; a file Bob's record names too, or a tombstone the request doesn't
        // name, is kept for that record, with a Warning, and only the caller's clip lets go of it.
        var logger = new ListLogger();
        var both1 = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "both", FileName = "both.mp4" }, Ct);
        var both2 = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "both", FileName = "both.mp4" }, Ct);
        var mine = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "shared", FileName = "shared.mp4" }, Ct);
        var bobs = await _fixture.AddAsync(_bob.Id, new ClipSpec { Name = "shared", FileName = "shared.mp4" }, Ct);
        var live = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "kept", FileName = "kept.mp4" }, Ct);
        var tombstone = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "kept", FileName = "kept.mp4", Tombstone = true }, Ct);
        Assert.Equal(both1.Clip!.FilePath, both2.Clip!.FilePath);
        Assert.Equal(mine.Clip!.FilePath, bobs.Clip!.FilePath);
        Assert.Equal(live.Clip!.FilePath, tombstone.Clip!.FilePath);

        var result = await Service(logger: logger).BulkDeleteAsync(_alice, new BookmarkClipBulkDeleteRequest { Ids = [both1.Id, both2.Id, mine.Id, live.Id] }, Ct);

        Assert.Equal([both1.Id, both2.Id, mine.Id, live.Id], result.FilesDeleted);
        Assert.Empty(result.InUse);
        Assert.Empty(result.Failed);
        Assert.False(File.Exists(both1.Clip.FilePath));
        Assert.True(File.Exists(bobs.Clip.FilePath));
        Assert.True(File.Exists(tombstone.Clip.FilePath));
        Assert.Equal(bobs, _fixture.Store.Get(bobs.Id));
        Assert.Equal(tombstone, _fixture.Store.Get(tombstone.Id));
        Assert.Null(_fixture.Store.Get(mine.Id)!.Clip);
        Assert.Equal(2, logger.At(LogLevel.Warning).Count(e => e.Message.Contains("Kept a clip file", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task BulkDelete_LeavesAFileGoneFromAClipFolderThatIsntRecognised_AsItIs_FolderUnavailable()
    {
        // In a folder there without a marker of its own a missing file isn't gone (the page shows it offline and
        // never selects it; a selection made before is answered so); a file that is there in it is deleted.
        var stray = _fixture.Directory.Combine("stray");
        Directory.CreateDirectory(stray);
        await _fixture.Store.AddClipRootAsync(stray, Ct);
        var gone = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "gone", Folder = Path.Combine(stray, "alice"), OnDisk = false }, Ct);
        var keptGone = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "kept gone", Folder = Path.Combine(stray, "alice"), OnDisk = false, Tombstone = true }, Ct);
        var there = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "there", Folder = Path.Combine(stray, "alice") }, Ct);
        var ownGone = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "own gone", OnDisk = false }, Ct);

        var result = await Service().BulkDeleteAsync(_alice, new BookmarkClipBulkDeleteRequest { Ids = [gone.Id, keptGone.Id, there.Id, ownGone.Id] }, Ct);

        Assert.Equal([gone.Id, keptGone.Id], result.FolderUnavailable);
        Assert.Equal([there.Id, ownGone.Id], result.FilesDeleted);
        Assert.Empty(result.Failed);
        Assert.Equal(gone, _fixture.Store.Get(gone.Id));
        Assert.Equal(keptGone, _fixture.Store.Get(keptGone.Id));
        Assert.False(File.Exists(there.Clip!.FilePath));

        // The folder confirmed (its marker written and recorded): the missing files are gone now.
        await ClipFileSupport.MarkClipFolderAsync(_fixture.Store, stray);
        var again = await Service().BulkDeleteAsync(_alice, new BookmarkClipBulkDeleteRequest { Ids = [gone.Id, keptGone.Id] }, Ct);
        Assert.Equal([gone.Id], again.FilesDeleted);
        Assert.Equal([keptGone.Id], again.RecordsRemoved);
    }

    [Fact]
    public async Task BulkDelete_LeavesTheClipsBeingMade_AsTheyAre_InProgress()
    {
        // Rename or delete while the clip is being re-made: 409 "Wait for the clip to finish or cancel it first."
        // Found in testing: deleting them as the single delete does reported a running remake in FilesDeleted, its row came back
        // as a cancelled clip needing Dismiss, and a job that finished between the write and its stop recorded its new file.
        // Now a clip being made, as the store's lock sees it, is left with its job and its earlier file, in InProgress.
        var running = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "running remake", Status = BookmarkClipStatus.Running }, Ct);
        var queued = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "queued", Status = BookmarkClipStatus.Queued, HasFile = false }, Ct);
        var queuedRemake = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "queued remake", Status = BookmarkClipStatus.Queued }, Ct);
        var ready = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "ready" }, Ct);

        var result = await Service().BulkDeleteAsync(_alice, new BookmarkClipBulkDeleteRequest { Ids = [running.Id, ready.Id, queued.Id, queuedRemake.Id] }, Ct);

        Assert.Equal([running.Id, queued.Id, queuedRemake.Id], result.InProgress);
        Assert.Equal([ready.Id], result.FilesDeleted);
        Assert.Empty(result.NotFound);
        Assert.Empty(result.RecordsRemoved.Concat(result.InUse).Concat(result.FolderUnavailable).Concat(result.Failed));
        foreach (var making in new[] { running, queued, queuedRemake })
        {
            Assert.Equal(making, _fixture.Store.Get(making.Id));
        }

        Assert.True(File.Exists(running.Clip!.FilePath));
        Assert.True(File.Exists(queuedRemake.Clip!.FilePath));
        Assert.False(File.Exists(ready.Clip!.FilePath));
        Assert.DoesNotContain(_fileSystem.Deletes, p => p.Contains("remake", StringComparison.Ordinal));
        _clips.Verify(c => c.StopRunning(It.IsAny<Guid>()), Times.Never);
        _clips.Verify(c => c.CancelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        // Once its job is over (here: cancelled, the earlier file kept), the clip goes like any other.
        await _fixture.Store.UpdateAsync(running.Id, b => b with { Clip = b.Clip! with { Status = BookmarkClipStatus.Cancelled } }, Ct);
        var again = await Service().BulkDeleteAsync(_alice, new BookmarkClipBulkDeleteRequest { Ids = [running.Id] }, Ct);
        Assert.Equal([running.Id], again.FilesDeleted);
        Assert.Null(_fixture.Store.Get(running.Id)!.Clip);
        Assert.False(File.Exists(running.Clip.FilePath));
    }

    [Fact]
    public async Task BulkDelete_OfAClipWhoseFolderComesBackAfterTheFirstCheck_LeavesTheFile()
    {
        // Found in testing: the check before the first write found the clip folder unplugged, and the answer came from a second
        // look (DeleteOnce), which found the folder back and deleted the file outside the store's write, then reported the
        // clip Failed while its record still pointed at the deleted file. The answer is the first check's now.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "flaky drive" }, Ct);
        var flaky = new FolderBackAfterFirstLook();

        var result = await Service(fileSystem: flaky).BulkDeleteAsync(_alice, new BookmarkClipBulkDeleteRequest { Ids = [record.Id] }, Ct);

        Assert.Equal([record.Id], result.FolderUnavailable);
        Assert.Empty(result.FilesDeleted.Concat(result.Failed));
        Assert.True(File.Exists(record.Clip!.FilePath));
        Assert.Equal(record, _fixture.Store.Get(record.Id));
        Assert.Equal(1, flaky.FolderLooks);
        Assert.Equal(0, flaky.Deletes);
    }

    [Fact]
    public async Task BulkDelete_Refuses_NoIds_TooMany_AndReadOnlyData_BeforeTouchingAnything()
    {
        var ready = await _fixture.AddAsync(_alice.Id, new ClipSpec(), Ct);

        foreach (var (ids, message) in new (IReadOnlyList<Guid>?, string)[]
        {
            (null, ClipService.BulkDeleteNoIdsMessage),
            ([], ClipService.BulkDeleteNoIdsMessage),
            (Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToList(), ClipService.BulkDeleteTooManyMessage)
        })
        {
            var error = await Assert.ThrowsAsync<BookmarkException>(() => Service().BulkDeleteAsync(_alice, new BookmarkClipBulkDeleteRequest { Ids = ids }, Ct));
            Assert.Equal(PluginErrorKind.Invalid, error.Kind);
            Assert.Equal(message, error.Message);
        }

        // Read-only data (a newer version's file): the store's 503, with nothing touched.
        var file = _fixture.Directory.Combine("newer", "bookmarks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, StoreFixtures.Read(StoreFixtures.Schema3), Ct);
        using var readOnly = BookmarkStore.Create(file, NullLogger.Instance);
        Assert.True(readOnly.IsReadOnly);
        var refused = await Assert.ThrowsAnyAsync<PluginException>(
            () => Service(readOnly).BulkDeleteAsync(_alice, new BookmarkClipBulkDeleteRequest { Ids = [ready.Id] }, Ct));
        Assert.Equal(PluginErrorKind.Unavailable, refused.Kind);
        Assert.True(File.Exists(ready.Clip!.FilePath));
        Assert.Empty(_fileSystem.Deletes);
        _clips.Verify(c => c.StopRunning(It.IsAny<Guid>()), Times.Never);
    }

    public void Dispose() => _fixture.Dispose();

    private ClipService Service(IBookmarkStore? store = null, IClipFileSystem? fileSystem = null, ILogger? logger = null)
    {
        store ??= _fixture.Store;
        var query = ReferenceEquals(store, _fixture.Store) ? _fixture.Query : new ClipQueryService(store, new ClipStateCache(store));
        return new ClipService(
            store,
            query,
            ClipFileSupport.Ops(store, logger, fileSystem: fileSystem ?? _fileSystem, states: _fixture.States),
            _clips.Object,
            new ClipLibraryLocations(_library.Object, NullLogger.Instance, _fixture.Clock),
            _library.Object,
            new LibraryAccess(_library.Object, user => _openLibraries.Select(id => new CollectionFolder { Id = id }).ToList()),
            NullLogger<ClipService>.Instance);
    }
}
