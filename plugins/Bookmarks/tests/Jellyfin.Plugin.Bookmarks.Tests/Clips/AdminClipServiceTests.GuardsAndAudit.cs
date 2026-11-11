using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using MediaBrowser.Controller.Entities;
using Moq;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The admins' All users scope, continued: the bulk delete's two guards pinned (a clip folder that isn't recognised, a rename
/// not finished finished first), and the audit: who acted (an API key by its name, never its token; a user that no longer
/// exists by its id, never as an API key), a delete that kept a file another clip names said as such, and a preview logged
/// once per caller and clip in <see cref="AdminClipService.PreviewLogWindow"/> while a download is logged each time.
/// </summary>
public sealed partial class AdminClipServiceTests
{
    [Fact]
    public async Task TheBulkDelete_OfSeveralUsersClips_LeavesAClipGoneFromAFolderThatIsntRecognised_AndFinishesARenameNotFinishedFirst()
    {
        // A clip folder that is there but isn't recognised as the clips' own (no marker of its own): a file gone from
        // it isn't taken for gone, so the clip is left as it is (FolderUnavailable), never its record removed.
        var stray = _fixture.Directory.Combine("stray-clips");
        Directory.CreateDirectory(stray);
        await _fixture.Store.AddClipRootAsync(stray, Ct);
        var unrecognised = await _fixture.AddAsync(_bob.Id, new ClipSpec { Name = "Stray", Folder = Path.Combine(stray, "bob"), OnDisk = false }, Ct);

        // A rename the server stopped during (the file at its new name, the record naming the old one), which can be finished:
        // the bulk delete finishes it, then deletes the renamed file (the one the clip is).
        var renaming = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var path = renaming.Clip!.FilePath!;
        var renamed = Path.Combine(Path.GetDirectoryName(path)!, "Heat (1995) - Vault [00.01.00-00.01.30].mp4");
        Files.Journal!.Add(new ClipRenameEntry
        {
            BookmarkId = renaming.Id,
            From = path,
            To = renamed,
            File = PhysicalClipFileSystem.ReadFileIdentity(path),
            UpdatedAt = renaming.UpdatedAt,
            StartedUtc = DateTime.UtcNow
        });
        File.Move(path, renamed);
        var strayBefore = _fixture.Store.Get(unrecognised.Id);

        var result = await Service().BulkDeleteAsync(Admin, new BookmarkClipBulkDeleteRequest { Ids = [unrecognised.Id, renaming.Id] }, Ct);

        Assert.Equal([unrecognised.Id], result.FolderUnavailable);
        Assert.Equal(strayBefore, _fixture.Store.Get(unrecognised.Id));
        Assert.Equal([renaming.Id], result.FilesDeleted);
        Assert.Empty(result.Failed);
        Assert.False(File.Exists(renamed));
        Assert.False(File.Exists(path));
        Assert.Null(_fixture.Store.Get(renaming.Id)!.Clip);
        Assert.Null(Files.Journal!.Get(renaming.Id));
        Assert.Equal([renamed], _fileSystem.Deletes);
    }

    [Fact]
    public async Task ANarrowAdmin_SeesAndActsOnlyOnTheClipsOfVideosTheyCanOpen_AndTheSummaryStillCountsEveryClip()
    {
        // Bookmarks 1.2.1: All users listed another user's clip of a title the admin can't open (a library
        // they have no access to, or above their parental limit), with its title and path, also when asked by its item's id.
        var narrow = TestData.CreateUser("narrow");
        narrow.SetPermission(PermissionKind.EnableAllFolders, false);
        _users.Setup(m => m.GetUserById(narrow.Id)).Returns(narrow);
        var openItem = TestData.CreateMovie("Heat");
        var hiddenItem = TestData.CreateMovie("Hidden title");
        _library.Setup(m => m.GetItemById(openItem.Id)).Returns(openItem);
        _library.Setup(m => m.GetItemById(hiddenItem.Id)).Returns(hiddenItem);
        _library.Setup(m => m.GetItemById<BaseItem>(openItem.Id, It.IsAny<User?>())).Returns(openItem);
        _library.Setup(m => m.GetItemById<BaseItem>(hiddenItem.Id, It.Is<User?>(u => u != null && u.Id != narrow.Id))).Returns(hiddenItem);
        var open = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Open", ItemId = openItem.Id }, Ct);
        var hidden = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Hidden clip", ItemName = "Hidden title", ItemId = hiddenItem.Id, Start = 120, End = 150 }, Ct);
        var detached = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Detached", Detached = ClipListFixture.T0, Start = 180, End = 210 }, Ct);
        var itemGone = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Item gone", Start = 240, End = 270 }, Ct);
        var service = Service();
        var asNarrow = Principal(narrow.Id, apiKey: false, client: "Jellyfin Web");

        // The narrow admin: only the clip of the video they can open, in the list, its count and paging, by item id too.
        var list = service.GetClips(asNarrow, new BookmarkClipQueryFilter(), null, null);
        Assert.Equal([open.Id], list.Items.Select(i => i.Id));
        Assert.Equal(1, list.TotalRecordCount);
        Assert.Empty(service.GetClips(asNarrow, new BookmarkClipQueryFilter { ItemId = hiddenItem.Id }, null, null).Items);
        Assert.Equal([open.Id], service.GetStatus(asNarrow, [open.Id, hidden.Id, detached.Id, itemGone.Id]).Select(s => s.Id));

        // Every action on the others is 404, nothing touched.
        foreach (var other in new[] { hidden, detached, itemGone })
        {
            Assert.Equal(PluginErrorKind.NotFound, Assert.Throws<BookmarkException>(() => service.GetClipFilePath(asNarrow, other.Id, download: true, logAccess: true)).Kind);
            Assert.Equal(PluginErrorKind.NotFound, (await Assert.ThrowsAsync<BookmarkException>(() => service.CancelAsync(asNarrow, other.Id, Ct))).Kind);
            Assert.Equal(PluginErrorKind.NotFound, (await Assert.ThrowsAsync<BookmarkException>(() => service.DeleteAsync(asNarrow, other.Id, onlyIfMissing: false, Ct))).Kind);
        }

        var bulk = await service.BulkDeleteAsync(asNarrow, new BookmarkClipBulkDeleteRequest { Ids = [hidden.Id, detached.Id, itemGone.Id] }, Ct);
        Assert.Equal(new[] { hidden.Id, detached.Id, itemGone.Id }.Order(), bulk.NotFound.Order());
        Assert.All(new[] { hidden, detached, itemGone }, b => Assert.True(File.Exists(_fixture.Store.Get(b.Id)!.Clip!.FilePath)));
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("Hidden", StringComparison.Ordinal));

        // Counted, not listed.
        Assert.Equal(4, service.GetSummary().Count);

        // An admin with every library (and no parental limit here) sees all four; an API key too.
        Assert.Equal(4, service.GetClips(Admin, new BookmarkClipQueryFilter(), null, null).TotalRecordCount);
        Assert.Equal(4, service.GetClips(ApiKey, new BookmarkClipQueryFilter(), null, null).TotalRecordCount);

        // The same admin with a parental limit that hides the title: three.
        _library.Setup(m => m.GetItemById<BaseItem>(hiddenItem.Id, It.IsAny<User?>())).Returns((BaseItem?)null);
        Assert.Equal(new[] { open.Id, detached.Id, itemGone.Id }.Order(), service.GetClips(Admin, new BookmarkClipQueryFilter(), null, null).Items.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task TheAudit_NamesAnApiKeyByItsName_NeverItsToken_AndAUserThatNoLongerExistsByItsId_NeverAsAnApiKey()
    {
        var bank = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var key = Principal(Guid.Empty, apiKey: true, client: "Sonarr helper");
        var goneId = Guid.NewGuid();
        var gone = Principal(goneId, apiKey: false, client: "Jellyfin Web");
        var service = Service();

        service.GetClipFilePath(key, bank.Id, download: true, logAccess: true);
        var asKey = service.GetClips(key, new BookmarkClipQueryFilter(), null, null);

        // Since 1.2.1 a user that no longer exists sees no clip (it has no libraries to open): 404, and nothing listed.
        Assert.Equal(PluginErrorKind.NotFound, Assert.Throws<BookmarkException>(() => service.GetClipFilePath(gone, bank.Id, download: true, logAccess: true)).Kind);
        Assert.Empty(service.GetClips(gone, new BookmarkClipQueryFilter(), null, null).Items);

        var information = _log.At(LogLevel.Information).Select(e => e.Message).ToList();
        Assert.Equal([$"The API key Sonarr helper downloaded the clip of bookmark {bank.Id} of user {_alice.Id}"], information);
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains(TokenValue, StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("An API key", StringComparison.Ordinal));

        // An API key opens no video.
        Assert.False(asKey.Items.Single().ItemAvailable);

        // An empty key name is said as such, never left blank.
        service.GetClipFilePath(Principal(Guid.Empty, apiKey: true, client: " "), bank.Id, download: true, logAccess: true);
        Assert.Equal($"The API key (no name) downloaded the clip of bookmark {bank.Id} of user {_alice.Id}", _log.At(LogLevel.Information).Last().Message);
    }

    [Fact]
    public async Task ADelete_ThatKeptAFileAnotherClipStillNames_IsLoggedAsLettingGoOfTheClip_NeverAsDeletingIt()
    {
        // Bookmarks 1.1.3's rule: two records naming one file (as 1.1.2 could leave them); the delete of one keeps the file for
        // the other, and the audit says the clip was let go of with its file kept.
        var alices = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var bobs = _fixture.Make(_bob.Id, new ClipSpec { Name = "Bank job", OnDisk = false, UserFolder = "bob" }) with { Clip = alices.Clip };
        await _fixture.Store.AddAsync(bobs, Ct);
        var carols = _fixture.Make(_bob.Id, new ClipSpec { Name = "Bank job", OnDisk = false, UserFolder = "bob", Start = 120, End = 150 }) with { Clip = alices.Clip };
        await _fixture.Store.AddAsync(carols, Ct);

        await Service().DeleteAsync(Admin, alices.Id, onlyIfMissing: false, Ct);
        var bulk = await Service().BulkDeleteAsync(ApiKey, new BookmarkClipBulkDeleteRequest { Ids = [bobs.Id] }, Ct);

        Assert.True(File.Exists(alices.Clip!.FilePath));
        Assert.Empty(_fileSystem.Deletes);
        Assert.Equal([bobs.Id], bulk.FilesDeleted);
        var information = _log.At(LogLevel.Information).Select(e => e.Message).ToList();
        Assert.Equal($"Admin {_admin.Id} let go of the clip of bookmark {alices.Id} of user {_alice.Id}; its file stays, as another clip still names it, and the bookmark stays", information[0]);
        Assert.StartsWith($"The API key kit-tests deleted 1 clips at once (0 of them of deleted bookmarks; 1 let go of a file another clip still names, which stays): {bobs.Id} of user {_bob.Id};", information[1], StringComparison.Ordinal);
        Assert.DoesNotContain(information, line => line.Contains("deleted the clip", StringComparison.Ordinal));

        // The last record naming it: deleted, and said so.
        await Service().DeleteAsync(Admin, carols.Id, onlyIfMissing: false, Ct);
        Assert.False(File.Exists(alices.Clip.FilePath));
        Assert.Equal($"Admin {_admin.Id} deleted the clip of bookmark {carols.Id} of user {_bob.Id}; the bookmark stays", _log.At(LogLevel.Information).Last().Message);
    }

    [Fact]
    public async Task APreview_IsLoggedOncePerCallerAndClipInTheWindow_ItsLoopsAtDebug_AndADownloadEveryTime()
    {
        var bank = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var vault = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Vault", Start = 120, End = 150 }, Ct);
        var service = Service();
        string Previewed(string who, Guid clip) => $"{who} previewed the clip of bookmark {clip} of user {_alice.Id}";
        int Count(string line) => _log.At(LogLevel.Information).Count(e => e.Message == line);

        // A looping <video> asks for the file from byte 0 again and again: one Information line in the window.
        for (var loop = 0; loop < 5; loop++)
        {
            service.GetClipFilePath(Admin, bank.Id, download: false, logAccess: true);
            _fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(1, Count(Previewed("Admin " + _admin.Id, bank.Id)));
        Assert.Equal(4, _log.At(LogLevel.Debug).Count(e => e.Message.StartsWith("Admin " + _admin.Id.ToString("N", CultureInfo.InvariantCulture) + " previewed the clip of bookmark " + bank.Id, StringComparison.Ordinal)));

        // Another clip, and another caller, are their own; a later range is never logged at Information.
        service.GetClipFilePath(Admin, vault.Id, download: false, logAccess: true);
        service.GetClipFilePath(ApiKey, bank.Id, download: false, logAccess: true);
        service.GetClipFilePath(Admin, vault.Id, download: false, logAccess: false);
        Assert.Equal(1, Count(Previewed("Admin " + _admin.Id, vault.Id)));
        Assert.Equal(1, Count(Previewed("The API key kit-tests", bank.Id)));

        // Past the window, logged again.
        _fixture.Clock.Advance(AdminClipService.PreviewLogWindow);
        service.GetClipFilePath(Admin, bank.Id, download: false, logAccess: true);
        Assert.Equal(2, Count(Previewed("Admin " + _admin.Id, bank.Id)));

        // A download is logged every time.
        service.GetClipFilePath(Admin, bank.Id, download: true, logAccess: true);
        service.GetClipFilePath(Admin, bank.Id, download: true, logAccess: true);
        Assert.Equal(2, Count($"Admin {_admin.Id} downloaded the clip of bookmark {bank.Id} of user {_alice.Id}"));
    }

    [Fact]
    public async Task ThePreviewsWindow_KeepsAtMostItsLimitOfCallersAndClips()
    {
        var bank = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var service = Service();

        // More API keys than the window keeps: each is logged once, and the first, dropped when the cache was full, again.
        for (var i = 0; i <= AdminClipService.MaxPreviewLogEntries; i++)
        {
            service.GetClipFilePath(Principal(Guid.Empty, apiKey: true, client: "key " + i.ToString(CultureInfo.InvariantCulture)), bank.Id, download: false, logAccess: true);
        }

        service.GetClipFilePath(Principal(Guid.Empty, apiKey: true, client: "key 0"), bank.Id, download: false, logAccess: true);

        Assert.Equal(AdminClipService.MaxPreviewLogEntries + 2, _log.At(LogLevel.Information).Count(e => e.Message.Contains(" previewed the clip", StringComparison.Ordinal)));
    }

    private const string TokenValue = "secret-token-of-the-request";

    // A principal as Jellyfin 10.11.5's handler makes it, with the client claim a test gives (an API key's name).
    private static ClaimsPrincipal Principal(Guid userId, bool apiKey, string client)
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Role, JellyfinAuth.AdministratorRole),
                new Claim("Jellyfin-UserId", userId.ToString("N", CultureInfo.InvariantCulture)),
                new Claim("Jellyfin-Client", client),
                new Claim("Jellyfin-Token", TokenValue),
                new Claim("Jellyfin-IsApiKey", apiKey.ToString(CultureInfo.InvariantCulture))
            ],
            JellyfinAuth.Scheme));
}
