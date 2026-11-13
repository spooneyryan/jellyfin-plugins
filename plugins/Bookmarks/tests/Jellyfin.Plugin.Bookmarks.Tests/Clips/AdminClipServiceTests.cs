using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Jellyfin.Plugin.Kit.TestSupport;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The admins' "All users" scope over real files and the real store:
/// every user's clips with their owners (a deleted user's "Deleted user (id)"), names and paths, never a note, neither in an
/// answer nor in a search or an order; with <c>AdminsCanManageAllClips</c> off every gated member 403 and the summary still
/// answering; the per-user totals; and the file actions on other users' clips as the owner's own: the delete file first (a
/// live bookmark stays, a tombstone goes), refused with nothing touched for a clip being made, a file in use, a folder offline
/// or not recognised, a path outside the clip folders (409), a rename not finished that can't be (finished first when it can),
/// never deleting a file another record names; Forget never deleting a file that is there; the bulk delete's outcomes; the
/// cancel; the file served; and each action logged with the admin, the clip and its owner, never a name or a path. An admin's
/// rename of another user's clip doesn't exist: <see cref="IBookmarkAdminClips"/> has no rename, and the user's
/// rename of another user's clip is 404 (<c>ClipRenameTests</c>).
/// </summary>
public sealed partial class AdminClipServiceTests : IDisposable
{
    private readonly ClipListFixture _fixture = new();
    private readonly FakeClipFileSystem _fileSystem = new();
    private readonly Mock<IClipExtractionService> _clips = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly PluginConfiguration _configuration = new();
    private readonly ListLogger<AdminClipService> _log = new();
    private readonly User _admin = TestData.CreateUser("admin");
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly User _bob = TestData.CreateUser("bob");
    private ClipFileOps? _files;

    public AdminClipServiceTests()
    {
        _library.Setup(m => m.GetItemById<Video>(It.IsAny<Guid>(), It.IsAny<User?>())).Returns((Video?)null);
        _library.Setup(m => m.GetVirtualFolders()).Returns([]);

        // Bob was deleted on this server: his clips are kept (KeepAsOrphan) under an id no user has.
        _users.SetupUsers([_admin, _alice]);
        _users.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => new[] { _admin, _alice }.FirstOrDefault(u => u.Id.Equals(id)));
        _fileSystem.RenameJournalFolder = _fixture.Directory.Combine("data");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ClipFileOps Files => _files ??= ClipFileSupport.Ops(_fixture.Store, fileSystem: _fileSystem, states: _fixture.States);

    private string StoreFile => _fixture.Directory.Combine("data", "bookmarks.json");

    // The request's principal as Jellyfin 10.11.5's authentication handler makes it (JellyfinAuth): the admin's token, and an
    // API key (its name in the client claim, as AuthorizationContext sets it).
    private ClaimsPrincipal Admin => JellyfinAuth.CreatePrincipal(_admin.Id, isAdministrator: true);

    private static ClaimsPrincipal ApiKey => JellyfinAuth.CreatePrincipal(Guid.Empty, isAdministrator: false, isApiKey: true);

    [Fact]
    public async Task TheList_IsEveryUsersClips_WithTheirOwnersNamesAndPaths_NeverANote()
    {
        var bank = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job", Note = "alice's secret note" }, Ct);
        var kept = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Kept", Note = "kept secret", Tombstone = true, Start = 120, End = 150 }, Ct);
        var bobs = await _fixture.AddAsync(_bob.Id, new ClipSpec { Name = "Bob's", Note = "bob's private words", UserFolder = "bob" }, Ct);
        var own = await _fixture.AddAsync(_admin.Id, new ClipSpec { Name = "Mine", UserFolder = "admin" }, Ct);

        var page = Service().GetClips(Admin, new BookmarkClipQueryFilter(), null, null);

        Assert.Equal(4, page.TotalRecordCount);
        var rows = page.Items.ToDictionary(r => r.Id);
        Assert.Equal(new[] { bank.Id, kept.Id, bobs.Id, own.Id }.Order(), rows.Keys.Order());
        Assert.Equal(("alice", false, "Bank job"), (rows[bank.Id].UserName, rows[bank.Id].UserDeleted, rows[bank.Id].Name));
        Assert.Equal(_alice.Id, rows[bank.Id].UserId);
        Assert.True(rows[kept.Id].BookmarkDeleted);
        Assert.Equal(("Deleted user (" + _bob.Id.ToString("N") + ")", true), (rows[bobs.Id].UserName, rows[bobs.Id].UserDeleted));
        Assert.Equal("admin", rows[own.Id].UserName);
        Assert.Equal(bank.Clip!.FilePath, rows[bank.Id].FilePath);
        Assert.True(ClipRootPaths.TryNormalize(_fixture.Root, out var root));
        Assert.Equal(ClipRootPaths.RootId(root), rows[bank.Id].RootId);
        Assert.Equal(BookmarkClipState.Ready, rows[bank.Id].Clip.State);
        Assert.Equal(Path.GetFileName(bank.Clip.FilePath), rows[bank.Id].Clip.FileName);

        // No note in the answer, in any form.
        var json = JsonSerializer.Serialize(page.Items);
        foreach (var note in new[] { "alice's secret note", "kept secret", "bob's private words" })
        {
            Assert.DoesNotContain(JsonSerializer.Serialize(note).Trim('"'), json, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("\"Note\"", json, StringComparison.Ordinal);

        // The owner filter.
        var alices = Service().GetClips(Admin, new BookmarkClipQueryFilter(), _alice.Id, null);
        Assert.Equal(new[] { bank.Id, kept.Id }.Order(), alices.Items.Select(r => r.Id).Order());
        Assert.Empty(Service().GetClips(Admin, new BookmarkClipQueryFilter(), Guid.NewGuid(), null).Items);
    }

    [Fact]
    public async Task TheSearch_NeverReadsANote_NorDoesTheNameOrder_ButTheOwnersOwnListDoes()
    {
        // A note's word that is in no name, title or file name: the owner's own list finds it, the admins' never.
        var noted = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job", Note = "zebra crossing" }, Ct);
        Assert.Single(_fixture.Query.Query(_fixture.Query.Rows(_alice.Id), new BookmarkClipQueryFilter { SearchTerm = "zebra" }, null).Rows);
        Assert.Empty(Service().GetClips(Admin, new BookmarkClipQueryFilter { SearchTerm = "zebra" }, null, null).Items);
        Assert.Single(Service().GetClips(Admin, new BookmarkClipQueryFilter { SearchTerm = "bank" }, null, null).Items);

        // Name A–Z: a clip without a name sorts by its note's first line in its owner's list, by its file's name in the admins'.
        var unnamed = await _fixture.AddAsync(_alice.Id, new ClipSpec { Note = "Alpha", FileName = "Zulu clip [00.02.00-00.02.30].mp4", Start = 120, End = 150 }, Ct);
        Assert.Equal([unnamed.Id, noted.Id], ClipListFixture.Ids(_fixture.Query.Query(_fixture.Query.Rows(_alice.Id), null, BookmarkClipSortBy.Name)));
        Assert.Equal([noted.Id, unnamed.Id], Service().GetClips(Admin, null, null, BookmarkClipSortBy.Name).Items.Select(r => r.Id));
    }

    [Fact]
    public async Task WithTheSettingOff_EveryGatedMemberIs403_NothingChanges_AndTheSummaryStillAnswers()
    {
        var clip = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        _configuration.AdminsCanManageAllClips = false;
        var before = await File.ReadAllBytesAsync(StoreFile, Ct);
        var service = Service();

        foreach (var call in new Func<Task>[]
        {
            () => Task.FromResult(service.GetClips(Admin, null, null, null)),
            () => Task.FromResult(service.GetStatus(Admin, [clip.Id])),
            () => Task.FromResult(service.GetClipFilePath(Admin, clip.Id, download: true, logAccess: true)),
            () => service.CancelAsync(Admin, clip.Id, Ct),
            () => service.DeleteAsync(Admin, clip.Id, onlyIfMissing: false, Ct),
            () => service.DeleteAsync(ApiKey, clip.Id, onlyIfMissing: true, Ct),
            () => service.BulkDeleteAsync(Admin, new BookmarkClipBulkDeleteRequest { Ids = [clip.Id] }, Ct)
        })
        {
            var error = await Assert.ThrowsAnyAsync<PluginException>(call);
            Assert.Equal((PluginErrorKind.Forbidden, AdminClipService.TurnedOffMessage), (error.Kind, error.Message));
        }

        Assert.True(File.Exists(clip.Clip!.FilePath));
        Assert.Equal(before, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.Empty(_fileSystem.Deletes);
        _clips.Verify(c => c.CancelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        var summary = service.GetSummary();
        Assert.Equal((1, 1), (summary.Count, summary.Users.Count));
    }

    [Fact]
    public async Task TheSummary_CountsEachUsersClips_DeletedUsersLast_AndNamesNoClip()
    {
        await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job", Note = "a note", Completed = ClipListFixture.T0.AddDays(1) }, Ct);
        await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Kept", Tombstone = true, Start = 120, End = 150, Completed = ClipListFixture.T0.AddDays(3) }, Ct);
        await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Gone", OnDisk = false, Start = 180, End = 210, Completed = ClipListFixture.T0.AddDays(2) }, Ct);
        await _fixture.AddAsync(_bob.Id, new ClipSpec { Name = "Bob's", UserFolder = "bob", Size = 500 }, Ct);
        await _fixture.AddAsync(_admin.Id, new ClipSpec { Name = "Mine", UserFolder = "admin", Size = 7 }, Ct);

        var summary = Service().GetSummary();

        Assert.Equal(["admin", "alice", "Deleted user (" + _bob.Id.ToString("N") + ")"], summary.Users.Select(u => u.UserName));
        var alice = summary.Users[1];
        Assert.Equal((_alice.Id, 3, 2000L, false), (alice.UserId, alice.Count, alice.TotalBytes, alice.UserDeleted));
        Assert.Equal((ClipListFixture.T0.AddDays(1), ClipListFixture.T0.AddDays(3)), (alice.OldestUtc, alice.NewestUtc));
        Assert.True(summary.Users[2].UserDeleted);
        Assert.Equal((5, 2507L), (summary.Count, summary.TotalBytes));
        var json = JsonSerializer.Serialize(summary);
        Assert.DoesNotContain("Bank job", json, StringComparison.Ordinal);
        Assert.DoesNotContain("a note", json, StringComparison.Ordinal);
        Assert.DoesNotContain(_fixture.Root.Replace("\\", "\\\\", StringComparison.Ordinal), json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ADelete_OfAnotherUsersClip_IsTheOwnersDelete_TheFileFirst_ALiveBookmarkStays_ATombstoneGoes()
    {
        var live = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var kept = await _fixture.AddAsync(_bob.Id, new ClipSpec { Name = "Kept", Tombstone = true, UserFolder = "bob" }, Ct);

        await Service().DeleteAsync(Admin, live.Id, onlyIfMissing: false, Ct);
        await Service().DeleteAsync(ApiKey, kept.Id, onlyIfMissing: false, Ct);

        Assert.False(File.Exists(live.Clip!.FilePath));
        Assert.False(File.Exists(kept.Clip!.FilePath));
        var stays = _fixture.Store.Get(live.Id)!;
        Assert.Null(stays.Clip);
        Assert.Equal(("Bank job", _alice.Id), (stays.Name, stays.UserId));
        Assert.Null(_fixture.Store.Get(kept.Id));
        Assert.Equal([live.Clip.FilePath!, kept.Clip.FilePath!], _fileSystem.Deletes);

        // Gone now: 404.
        var again = await Assert.ThrowsAnyAsync<PluginException>(() => Service().DeleteAsync(Admin, live.Id, onlyIfMissing: false, Ct));
        Assert.Equal((PluginErrorKind.NotFound, AdminClipService.NotFoundMessage), (again.Kind, again.Message));
        await Assert.ThrowsAnyAsync<PluginException>(() => Service().DeleteAsync(Admin, Guid.NewGuid(), onlyIfMissing: false, Ct));
    }

    [Fact]
    public async Task ADelete_IsRefusedWithNothingTouched_ForAClipBeingMade_AFileInUse_AFolderOfflineOrNotRecognised_AndAPathOutsideTheClipFolders()
    {
        var making = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Making", Status = BookmarkClipStatus.Queued, HasFile = false }, Ct);
        var remaking = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Remaking", Status = BookmarkClipStatus.Running, Start = 120, End = 150 }, Ct);
        var held = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Held", Start = 180, End = 210 }, Ct);
        _fileSystem.HoldOpen(held.Clip!.FilePath!);

        var usb = _fixture.Directory.Combine("usb-clips");
        await _fixture.Store.AddClipRootAsync(usb, Ct);
        var offline = await _fixture.AddAsync(_bob.Id, new ClipSpec { Name = "On the stick", Folder = Path.Combine(usb, "bob"), OnDisk = false }, Ct);
        var stray = _fixture.Directory.Combine("stray-clips");
        Directory.CreateDirectory(stray);
        await _fixture.Store.AddClipRootAsync(stray, Ct);
        var unrecognised = await _fixture.AddAsync(_bob.Id, new ClipSpec { Name = "Stray", Folder = Path.Combine(stray, "bob"), OnDisk = false }, Ct);
        var outsidePath = _fixture.Directory.CreateFile(Path.Combine("elsewhere", "Heat (1995) - Outside [00.01.00-00.01.30].mp4"));
        var outside = OutsideTheClipFolders(_alice.Id, outsidePath);
        await _fixture.Store.AddAsync(outside, Ct);
        var before = await File.ReadAllBytesAsync(StoreFile, Ct);

        foreach (var (id, kind, message) in new[]
        {
            (making.Id, PluginErrorKind.Conflict, ClipFileRenamer.InProgressMessage),
            (remaking.Id, PluginErrorKind.Conflict, ClipFileRenamer.InProgressMessage),
            (held.Id, PluginErrorKind.Conflict, ClipFileOps.InUseMessage),
            (offline.Id, PluginErrorKind.Unavailable, ClipFileOps.FolderUnavailableMessage),
            (unrecognised.Id, PluginErrorKind.Unavailable, ClipFileOps.FolderUnrecognisedMessage),
            (outside.Id, PluginErrorKind.Conflict, ClipFileOps.OutsideClipFoldersMessage)
        })
        {
            var error = await Assert.ThrowsAnyAsync<PluginException>(() => Service().DeleteAsync(Admin, id, onlyIfMissing: false, Ct));
            Assert.Equal((kind, message), (error.Kind, error.Message));
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.True(File.Exists(held.Clip.FilePath));
        Assert.True(File.Exists(remaking.Clip!.FilePath));
        Assert.True(File.Exists(outsidePath));
        Assert.Equal(ClipFileOps.Attempts, _fileSystem.Deletes.Count(d => d == held.Clip.FilePath));
        Assert.DoesNotContain(_fileSystem.Deletes, d => d != held.Clip.FilePath);
        _clips.Verify(c => c.CancelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(_fixture.Store.PendingFileDeletes);
    }

    [Fact]
    public async Task ADelete_FinishesARenameNotFinished_First_AndIsRefusedWithNothingTouchedWhileItCantBe()
    {
        // A rename the server stopped during: the file at its new name, the record naming the old one. Its clip folder away,
        // the rename can't be finished, and the delete is 503 with nothing touched; back, the delete finishes it, then deletes
        // the renamed file (the one the clip is).
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var path = record.Clip!.FilePath!;
        var renamed = Path.Combine(Path.GetDirectoryName(path)!, "Heat (1995) - Vault [00.01.00-00.01.30].mp4");
        Files.Journal!.Add(new ClipRenameEntry
        {
            BookmarkId = record.Id,
            From = path,
            To = renamed,
            File = PhysicalClipFileSystem.ReadFileIdentity(path),
            UpdatedAt = record.UpdatedAt,
            StartedUtc = DateTime.UtcNow
        });
        File.Move(path, renamed);
        ClipFileSupport.Unplug(_fixture.Root);
        var before = await File.ReadAllBytesAsync(StoreFile, Ct);
        try
        {
            var error = await Assert.ThrowsAnyAsync<PluginException>(() => Service().DeleteAsync(Admin, record.Id, onlyIfMissing: false, Ct));
            Assert.Equal(PluginErrorKind.Unavailable, error.Kind);
            Assert.Equal(before, await File.ReadAllBytesAsync(StoreFile, Ct));

            var bulk = await Service().BulkDeleteAsync(Admin, new BookmarkClipBulkDeleteRequest { Ids = [record.Id] }, Ct);
            Assert.Equal([record.Id], bulk.Failed.Concat(bulk.FolderUnavailable));
            Assert.Equal(before, await File.ReadAllBytesAsync(StoreFile, Ct));
        }
        finally
        {
            ClipFileSupport.PlugIn(_fixture.Root);
        }

        Assert.True(File.Exists(renamed));
        Assert.Empty(_fileSystem.Deletes);

        await Service().DeleteAsync(Admin, record.Id, onlyIfMissing: false, Ct);

        Assert.False(File.Exists(renamed));
        Assert.False(File.Exists(path));
        Assert.Null(_fixture.Store.Get(record.Id)!.Clip);
        Assert.Null(Files.Journal!.Get(record.Id));
    }

    [Fact]
    public async Task ADelete_KeepsAFileAnotherRecordStillNames_AndOnlyTheClipLetsGoOfIt()
    {
        // Bookmarks 1.1.3's rule, for an admin too: two records naming one file (as 1.1.2 could leave them).
        var alices = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var bobs = _fixture.Make(_bob.Id, new ClipSpec { Name = "Bank job", OnDisk = false, UserFolder = "bob" }) with { Clip = alices.Clip };
        await _fixture.Store.AddAsync(bobs, Ct);

        await Service().DeleteAsync(Admin, alices.Id, onlyIfMissing: false, Ct);

        Assert.True(File.Exists(alices.Clip!.FilePath));
        Assert.Empty(_fileSystem.Deletes);
        Assert.Null(_fixture.Store.Get(alices.Id)!.Clip);
        Assert.Equal(alices.Clip.FilePath, _fixture.Store.Get(bobs.Id)!.Clip!.FilePath);

        // The last record naming it: deleted.
        await Service().BulkDeleteAsync(Admin, new BookmarkClipBulkDeleteRequest { Ids = [bobs.Id] }, Ct);
        Assert.False(File.Exists(alices.Clip.FilePath));
    }

    [Fact]
    public async Task Forget_NeverDeletesAFileThatIsThereAfterAll_AndForgetsOneThatIsGone()
    {
        var there = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "There after all" }, Ct);
        var gone = await _fixture.AddAsync(_bob.Id, new ClipSpec { Name = "Gone", OnDisk = false, Tombstone = true, UserFolder = "bob" }, Ct);

        var error = await Assert.ThrowsAnyAsync<PluginException>(() => Service().DeleteAsync(Admin, there.Id, onlyIfMissing: true, Ct));

        Assert.Equal((PluginErrorKind.Conflict, BookmarkManager.ClipFileIsThereMessage), (error.Kind, error.Message));
        Assert.True(File.Exists(there.Clip!.FilePath));
        Assert.NotNull(_fixture.Store.Get(there.Id)!.Clip);

        await Service().DeleteAsync(Admin, gone.Id, onlyIfMissing: true, Ct);
        Assert.Null(_fixture.Store.Get(gone.Id));
        Assert.Empty(_fileSystem.Deletes);
    }

    [Fact]
    public async Task TheBulkDelete_OfSeveralUsersClips_ReportsEachOutcome_AndLeavesTheOnesThatCantGoAsTheyWere()
    {
        var alices = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var bobsKept = await _fixture.AddAsync(_bob.Id, new ClipSpec { Name = "Kept", Tombstone = true, UserFolder = "bob" }, Ct);
        var making = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Making", Status = BookmarkClipStatus.Queued, HasFile = false, Start = 120, End = 150 }, Ct);
        var held = await _fixture.AddAsync(_bob.Id, new ClipSpec { Name = "Held", UserFolder = "bob", Start = 180, End = 210 }, Ct);
        _fileSystem.HoldOpen(held.Clip!.FilePath!);
        var usb = _fixture.Directory.Combine("usb-clips");
        await _fixture.Store.AddClipRootAsync(usb, Ct);
        var offline = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "On the stick", Folder = Path.Combine(usb, "alice"), OnDisk = false }, Ct);
        var unknown = Guid.NewGuid();

        var result = await Service().BulkDeleteAsync(Admin, new BookmarkClipBulkDeleteRequest { Ids = [alices.Id, bobsKept.Id, making.Id, held.Id, offline.Id, unknown, alices.Id] }, Ct);

        Assert.Equal([alices.Id], result.FilesDeleted);
        Assert.Equal([bobsKept.Id], result.RecordsRemoved);
        Assert.Equal([making.Id], result.InProgress);
        Assert.Equal([held.Id], result.InUse);
        Assert.Equal([offline.Id], result.FolderUnavailable);
        Assert.Equal([unknown], result.NotFound);
        Assert.Empty(result.Failed);
        Assert.Null(_fixture.Store.Get(alices.Id)!.Clip);
        Assert.Null(_fixture.Store.Get(bobsKept.Id));
        Assert.Equal(BookmarkClipStatus.Queued, _fixture.Store.Get(making.Id)!.Clip!.Status);
        Assert.True(File.Exists(held.Clip.FilePath));
        Assert.Equal(held.Clip.FilePath, _fixture.Store.Get(held.Id)!.Clip!.FilePath);
        Assert.NotNull(_fixture.Store.Get(offline.Id)!.Clip);
        Assert.Empty(_fixture.Store.PendingFileDeletes);

        foreach (var (ids, message) in new (List<Guid>, string)[]
        {
            ([], ClipService.BulkDeleteNoIdsMessage),
            (Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToList(), ClipService.BulkDeleteTooManyMessage)
        })
        {
            var error = await Assert.ThrowsAnyAsync<PluginException>(() => Service().BulkDeleteAsync(Admin, new BookmarkClipBulkDeleteRequest { Ids = ids }, Ct));
            Assert.Equal((PluginErrorKind.Invalid, message), (error.Kind, error.Message));
        }
    }

    [Fact]
    public async Task Cancel_EndsAnotherUsersClipJob_AndAnswersTheClip_AClipThatIsntBeingMadeAsItIs()
    {
        var making = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Making", Status = BookmarkClipStatus.Queued, HasFile = false }, Ct);
        var ready = await _fixture.AddAsync(_bob.Id, new ClipSpec { Name = "Ready", UserFolder = "bob" }, Ct);

        var cancelled = await Service().CancelAsync(Admin, making.Id, Ct);
        var asItIs = await Service().CancelAsync(Admin, ready.Id, Ct);

        _clips.Verify(c => c.CancelAsync(making.Id, It.IsAny<CancellationToken>()), Times.Once);
        _clips.Verify(c => c.CancelAsync(ready.Id, It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal((making.Id, "alice"), (cancelled.Id, cancelled.UserName));
        Assert.Equal(BookmarkClipState.Ready, asItIs.Clip.State);
        var error = await Assert.ThrowsAnyAsync<PluginException>(() => Service().CancelAsync(Admin, Guid.NewGuid(), Ct));
        Assert.Equal(PluginErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task TheFile_OfAnotherUsersClip_IsServedFromTheClipFolders_AMissingOrOutsideFileIs404_AndTheStatusSeesEveryUser()
    {
        var bobs = await _fixture.AddAsync(_bob.Id, new ClipSpec { Name = "Bob's", UserFolder = "bob", Tombstone = true }, Ct);
        var gone = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Gone", OnDisk = false }, Ct);
        var outsidePath = _fixture.Directory.CreateFile(Path.Combine("elsewhere", "Heat (1995) - Outside [00.01.00-00.01.30].mp4"));
        var outside = OutsideTheClipFolders(_alice.Id, outsidePath);
        await _fixture.Store.AddAsync(outside, Ct);

        Assert.Equal(bobs.Clip!.FilePath, Service().GetClipFilePath(Admin, bobs.Id, download: true, logAccess: true));
        foreach (var id in new[] { gone.Id, outside.Id })
        {
            var error = Assert.ThrowsAny<PluginException>(() => Service().GetClipFilePath(Admin, id, download: false, logAccess: true));
            Assert.Equal((PluginErrorKind.NotFound, BookmarkManager.NoClipFileMessage), (error.Kind, error.Message));
        }

        Assert.Equal(PluginErrorKind.NotFound, Assert.ThrowsAny<PluginException>(() => Service().GetClipFilePath(Admin, Guid.NewGuid(), download: true, logAccess: true)).Kind);
        Assert.Equal([bobs.Id, gone.Id], Service().GetStatus(Admin, [bobs.Id, Guid.NewGuid(), gone.Id]).Select(s => s.Id));
        Assert.Empty(_fileSystem.Deletes);
    }

    [Fact]
    public async Task EveryAction_IsLoggedWithTheAdminTheClipAndItsOwner_NeverANameOrAPath()
    {
        var bank = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var making = await _fixture.AddAsync(_bob.Id, new ClipSpec { Name = "Making", Status = BookmarkClipStatus.Queued, HasFile = false, UserFolder = "bob" }, Ct);
        var kept = await _fixture.AddAsync(_bob.Id, new ClipSpec { Name = "Kept", Tombstone = true, UserFolder = "bob", Start = 120, End = 150 }, Ct);
        var service = Service();

        service.GetClipFilePath(Admin, bank.Id, download: true, logAccess: true);
        service.GetClipFilePath(Admin, bank.Id, download: false, logAccess: false);
        await service.CancelAsync(Admin, making.Id, Ct);
        await service.DeleteAsync(Admin, bank.Id, onlyIfMissing: false, Ct);
        await service.BulkDeleteAsync(ApiKey, new BookmarkClipBulkDeleteRequest { Ids = [kept.Id] }, Ct);

        var information = _log.At(LogLevel.Information).Select(e => e.Message).ToList();
        Assert.Equal(4, information.Count);
        Assert.Contains($"Admin {_admin.Id} downloaded the clip of bookmark {bank.Id} of user {_alice.Id}", information);
        Assert.Contains($"Admin {_admin.Id} cancelled the clip being made of bookmark {making.Id} of user {_bob.Id}", information);
        Assert.Contains($"Admin {_admin.Id} deleted the clip of bookmark {bank.Id} of user {_alice.Id}; the bookmark stays", information);
        Assert.StartsWith($"The API key kit-tests deleted 1 clips at once (1 of them of deleted bookmarks; 0 let go of a file another clip still names, which stays): {kept.Id} of user {_bob.Id};", information[3], StringComparison.Ordinal);
        foreach (var line in _log.Entries.Where(e => e.Level >= LogLevel.Information))
        {
            Assert.DoesNotContain("Bank job", line.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Kept", line.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_fixture.Root, line.Message, StringComparison.OrdinalIgnoreCase);
        }

        // The paths at Debug only.
        Assert.Contains(_log.At(LogLevel.Debug), e => e.Message.Contains(bank.Clip!.FilePath!, StringComparison.Ordinal));
    }

    public void Dispose() => _fixture.Dispose();

    // A record whose clip path is outside every recorded clip folder (an import from another server).
    private Bookmark OutsideTheClipFolders(Guid userId, string path)
    {
        var record = _fixture.Make(userId, new ClipSpec { Name = "Outside", OnDisk = false, Start = 240, End = 270 });
        return record with { Clip = record.Clip! with { FilePath = path } };
    }

    private AdminClipService Service() => new(
        _fixture.Store,
        _fixture.Query,
        Files,
        _clips.Object,
        new ClipLibraryLocations(_library.Object, NullLogger.Instance, _fixture.Clock),
        _library.Object,
        new LibraryAccess(_library.Object, _ => []),
        _users.Object,
        TestData.ConfigurationProvider(_configuration),
        _log,
        _fixture.Clock);
}
