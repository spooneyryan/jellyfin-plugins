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
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Services;

/// <summary>
/// Names through <see cref="IBookmarkManager"/>: create, update (replace semantics:
/// a name left out is cleared, except by Bookmarks 1.0's body) and rename (<c>POST /Bookmarks/{id}/Name</c>), the name rules on each, the <c>IfUpdatedAt</c>
/// token (409 with nothing written, checked under the store's lock), tombstones and other users' bookmarks (404), read-only
/// data (503), the "Relinked, check position" badge, and a log that never holds a name or a note.
/// </summary>
public sealed class BookmarkNamingTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly BookmarkStore _store;
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IClipExtractionService> _clips = new();
    private readonly Mock<IClipPlanner> _planner = new();
    private readonly PluginConfiguration _configuration = new();
    private readonly ListLogger<BookmarkManager> _logger = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly User _bob = TestData.CreateUser("bob");
    private readonly Movie _movie = TestData.CreateMovie();
    private readonly IBookmarkManager _manager;

    public BookmarkNamingTests()
    {
        _store = ClipFileSupport.StoreWithClipRoots(StoreFile, _directory.Path);
        _libraryManager.Setup(m => m.GetItemById<Video>(_movie.Id, It.IsAny<User?>())).Returns(_movie);
        _manager = CreateManager(_store);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StoreFile => _directory.Combine("bookmarks.json");

    [Theory]
    [InlineData("Bank job", "Bank job")]
    [InlineData("  Bank\tjob\r\n  take   2 ", "Bank job take 2")]
    [InlineData("Café scène", "Café scène")]
    [InlineData("\u0007Bank job\u0000", "Bank job")]
    [InlineData("   ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public async Task Create_StoresTheNormalisedName(string? given, string? expected)
    {
        var created = await _manager.CreateBookmarkAsync(_alice, Create(given, note: "the note"), Ct);

        Assert.Equal(expected, created.Name);
        Assert.Equal("the note", created.Note);
        Assert.Equal(expected, _store.Get(created.Id)!.Name);
    }

    [Fact]
    public async Task Create_WithANameOver100Characters_Is400_AndStoresNothing()
    {
        var hundred = await _manager.CreateBookmarkAsync(_alice, Create(new string('x', 100)), Ct);
        var before = await File.ReadAllBytesAsync(StoreFile, Ct);

        var ex = await Assert.ThrowsAsync<BookmarkException>(() => _manager.CreateBookmarkAsync(_alice, Create(new string('x', 101)), Ct));

        Assert.Equal(PluginErrorKind.Invalid, ex.Kind);
        Assert.Equal("Names can be at most 100 characters long.", ex.Message);
        Assert.Equal(new string('x', 100), hundred.Name);
        Assert.Equal([hundred.Id], _store.GetAll().Select(b => b.Id));
        Assert.Equal(before, await File.ReadAllBytesAsync(StoreFile, Ct));
    }

    [Fact]
    public async Task Update_ReplacesTheName_AndLeavingItOutClearsIt()
    {
        // Replace semantics, as for the note; "null" never means "keep".
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Create("Bank job", note: "the note"), Ct);

        var renamed = await _manager.UpdateBookmarkAsync(_alice, bookmark.Id, Update(bookmark, name: " Vault\n2 ", note: "the note"), Ct);
        var cleared = await _manager.UpdateBookmarkAsync(_alice, bookmark.Id, Update(bookmark, name: null, note: "the note"), Ct);
        var blank = await _manager.UpdateBookmarkAsync(_alice, bookmark.Id, Update(bookmark, name: "Vault", note: null), Ct);
        var spaces = await _manager.UpdateBookmarkAsync(_alice, bookmark.Id, Update(bookmark, name: " \t ", note: null), Ct);

        Assert.Equal("Vault 2", renamed.Name);
        Assert.Null(cleared.Name);
        Assert.Equal("the note", cleared.Note);
        Assert.Equal("Vault", blank.Name);
        Assert.Null(blank.Note);
        Assert.Null(spaces.Name);
        Assert.Null(_store.Get(bookmark.Id)!.Name);
    }

    [Fact]
    public async Task Update_With10sBody_KeepsTheName_AndReplacesEverythingElse()
    {
        // Bookmarks 1.0's editor sends {StartTicks, EndTicks, Note}, never Name or IfUpdatedAt. A tab still
        // running it after the upgrade shows no name, and once its list was read again not the note the upgrade made the
        // name either, so saving there cleared the bookmark's whole text. Such a body keeps the name; the rest is replaced.
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Create("Bank job", note: "the note"), Ct);

        var edited = await _manager.UpdateBookmarkAsync(_alice, bookmark.Id, new BookmarkUpdateRequest { StartTicks = 70 * TestData.OneSecond, EndTicks = null, Note = null }, Ct);

        Assert.Equal("Bank job", edited.Name);
        Assert.Null(edited.Note);
        Assert.Equal(70 * TestData.OneSecond, edited.StartTicks);
        Assert.Null(edited.EndTicks);
        Assert.True(edited.UpdatedAt > bookmark.UpdatedAt);
        Assert.Equal("Bank job", _store.Get(bookmark.Id)!.Name);
    }

    [Fact]
    public async Task Update_WithoutName_ClearsIt_WhenTheBodyHasATokenOrSaysNull()
    {
        // Only 1.0's body keeps the name. A 1.1 client's body without Name (it sends IfUpdatedAt), and an explicit null
        // (with or without a token), clear it, as replace semantics say.
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Create("Bank job", note: "the note"), Ct);

        var withToken = await _manager.UpdateBookmarkAsync(_alice, bookmark.Id, new BookmarkUpdateRequest { StartTicks = bookmark.StartTicks, Note = "the note", IfUpdatedAt = bookmark.UpdatedAt }, Ct);
        await _manager.RenameBookmarkAsync(_alice, bookmark.Id, new BookmarkRenameRequest { Name = "Bank job" }, Ct);
        var explicitNull = await _manager.UpdateBookmarkAsync(_alice, bookmark.Id, new BookmarkUpdateRequest { StartTicks = bookmark.StartTicks, Name = null, Note = "the note" }, Ct);

        Assert.Null(withToken.Name);
        Assert.Null(explicitNull.Name);
        Assert.Equal("the note", explicitNull.Note);
        Assert.Null(_store.Get(bookmark.Id)!.Name);
    }

    [Fact]
    public async Task Update_With10sBody_KeepsTheNameTheStoreHas_UnderItsLock()
    {
        // Another tab renames the bookmark between the lookup and the write: 1.0's body keeps that name, not the one looked up.
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Create("Bank job"), Ct);
        var racing = new InterferingBookmarkStore(_store);
        racing.BeforeNextWrite(b => b with { Name = "Other tab", UpdatedAt = BookmarkRecordRules.NextUpdatedAt(b.UpdatedAt, DateTime.UtcNow) });
        var manager = CreateManager(racing);

        var edited = await manager.UpdateBookmarkAsync(_alice, bookmark.Id, new BookmarkUpdateRequest { StartTicks = 0, Note = "from 1.0" }, Ct);

        Assert.Equal("Other tab", edited.Name);
        Assert.Equal("from 1.0", edited.Note);
        Assert.Equal("Other tab", _store.Get(bookmark.Id)!.Name);
    }

    [Fact]
    public async Task Update_WithANameOver100Characters_Is400_AndChangesNothing()
    {
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Create("Bank job"), Ct);
        var before = await File.ReadAllBytesAsync(StoreFile, Ct);

        var ex = await Assert.ThrowsAsync<BookmarkException>(() => _manager.UpdateBookmarkAsync(_alice, bookmark.Id, Update(bookmark, name: new string('x', 101)), Ct));

        Assert.Equal(PluginErrorKind.Invalid, ex.Kind);
        Assert.Equal(before, await File.ReadAllBytesAsync(StoreFile, Ct));
    }

    [Fact]
    public async Task Rename_ChangesOnlyTheName()
    {
        var bookmark = await AddWithClipAsync();
        var before = _store.Get(bookmark.Id)!;

        var renamed = await _manager.RenameBookmarkAsync(_alice, bookmark.Id, new BookmarkRenameRequest { Name = "  Vault  " }, Ct);

        Assert.Equal("Vault", renamed.Name);
        var after = _store.Get(bookmark.Id)!;
        Assert.Equal(before with { Name = "Vault", UpdatedAt = after.UpdatedAt }, after);
        Assert.True(after.UpdatedAt > before.UpdatedAt);
        Assert.Equal(before.CreatedAt, renamed.CreatedAt);

        // The clip file keeps the name it was made with (1.2.0 adds "Rename file"); only the next clip gets
        // the new one, and the clip isn't outdated by a rename.
        Assert.Equal("Heat (1995) - Bank job [00.01.00-00.01.30].mkv", renamed.Clip!.FileName);
        Assert.False(renamed.Clip.IsOutdated);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n ")]
    public async Task Rename_ToNothing_LeavesTheBookmarkUnnamed(string? name)
    {
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Create("Bank job", note: "the note"), Ct);

        var renamed = await _manager.RenameBookmarkAsync(_alice, bookmark.Id, new BookmarkRenameRequest { Name = name }, Ct);

        Assert.Null(renamed.Name);
        Assert.Equal("the note", renamed.Note);
    }

    [Fact]
    public async Task Rename_ToTheSameName_IsStillAChange_AndClearsTheRelinkedBadge()
    {
        // "Looks right": POST /Bookmarks/{id}/Name with the unchanged name clears the badge.
        var bookmark = TestData.CreateBookmark(_alice.Id, 60, 90, _movie.Id) with
        {
            Name = "Bank job",
            RelinkedUtc = DateTime.UtcNow.AddDays(-1),
            RelinkStrength = MatchStrength.Strong
        };
        await _store.AddAsync(bookmark, Ct);

        var confirmed = await _manager.RenameBookmarkAsync(_alice, bookmark.Id, new BookmarkRenameRequest { Name = "Bank job", IfUpdatedAt = bookmark.UpdatedAt }, Ct);

        var stored = _store.Get(bookmark.Id)!;
        Assert.Equal("Bank job", confirmed.Name);
        Assert.Null(stored.RelinkedUtc);
        Assert.Null(stored.RelinkStrength);
        Assert.True(stored.UpdatedAt > bookmark.UpdatedAt);
    }

    [Fact]
    public async Task Rename_WithANameOver100Characters_Is400_AndChangesNothing()
    {
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Create("Bank job"), Ct);
        var before = await File.ReadAllBytesAsync(StoreFile, Ct);

        var ex = await Assert.ThrowsAsync<BookmarkException>(() => _manager.RenameBookmarkAsync(_alice, bookmark.Id, new BookmarkRenameRequest { Name = new string('é', 101) }, Ct));

        Assert.Equal(PluginErrorKind.Invalid, ex.Kind);
        Assert.Equal("Names can be at most 100 characters long.", ex.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(StoreFile, Ct));
    }

    [Fact]
    public async Task RenameAndUpdate_OfAnotherUsersOrAnUnknownBookmark_Are404_AndChangeNothing()
    {
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Create("Bank job"), Ct);
        var before = await File.ReadAllBytesAsync(StoreFile, Ct);

        await AssertKindAsync(PluginErrorKind.NotFound, () => _manager.RenameBookmarkAsync(_bob, bookmark.Id, new BookmarkRenameRequest { Name = "Mine" }, Ct));
        await AssertKindAsync(PluginErrorKind.NotFound, () => _manager.UpdateBookmarkAsync(_bob, bookmark.Id, Update(bookmark, name: "Mine"), Ct));
        await AssertKindAsync(PluginErrorKind.NotFound, () => _manager.RenameBookmarkAsync(_alice, Guid.NewGuid(), new BookmarkRenameRequest { Name = "Mine" }, Ct));

        // Other users' ids can't be probed: a name that is too long is still a 404 for them, not a 400.
        await AssertKindAsync(PluginErrorKind.NotFound, () => _manager.RenameBookmarkAsync(_bob, bookmark.Id, new BookmarkRenameRequest { Name = new string('x', 101) }, Ct));
        Assert.Equal(before, await File.ReadAllBytesAsync(StoreFile, Ct));
    }

    [Fact]
    public async Task IfUpdatedAt_TheCurrentToken_IsAccepted_ByUpdateAndRename()
    {
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Create("Bank job"), Ct);

        var updated = await _manager.UpdateBookmarkAsync(_alice, bookmark.Id, Update(bookmark, name: "Vault", ifUpdatedAt: bookmark.UpdatedAt), Ct);
        var renamed = await _manager.RenameBookmarkAsync(_alice, bookmark.Id, new BookmarkRenameRequest { Name = "Vault 2", IfUpdatedAt = updated.UpdatedAt }, Ct);

        Assert.Equal("Vault 2", renamed.Name);
        Assert.True(updated.UpdatedAt > bookmark.UpdatedAt);
        Assert.True(renamed.UpdatedAt > updated.UpdatedAt);
    }

    [Fact]
    public async Task IfUpdatedAt_TwoTabsRenamingAtOnce_TheSecondIs409_AndChangesNothing()
    {
        // Two tabs rename the same bookmark: both read the same UpdatedAt; the first rename wins.
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Create("Bank job"), Ct);
        var token = bookmark.UpdatedAt;

        var first = await _manager.RenameBookmarkAsync(_alice, bookmark.Id, new BookmarkRenameRequest { Name = "Tab one", IfUpdatedAt = token }, Ct);
        var afterFirst = await File.ReadAllBytesAsync(StoreFile, Ct);
        var second = await Assert.ThrowsAsync<BookmarkException>(
            () => _manager.RenameBookmarkAsync(_alice, bookmark.Id, new BookmarkRenameRequest { Name = "Tab two", IfUpdatedAt = token }, Ct));
        var update = await Assert.ThrowsAsync<BookmarkException>(
            () => _manager.UpdateBookmarkAsync(_alice, bookmark.Id, Update(bookmark, name: "Tab two", ifUpdatedAt: token), Ct));

        Assert.Equal("Tab one", first.Name);
        Assert.Equal(PluginErrorKind.Conflict, second.Kind);
        Assert.Equal("Changed elsewhere. Reload to see the latest.", second.Message);
        Assert.Equal(PluginErrorKind.Conflict, update.Kind);
        Assert.Equal("Tab one", _store.Get(bookmark.Id)!.Name);
        Assert.Equal(afterFirst, await File.ReadAllBytesAsync(StoreFile, Ct));
    }

    [Fact]
    public async Task IfUpdatedAt_Left_Out_IsLastWriterWins_AsIn10()
    {
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Create("Bank job"), Ct);
        await _manager.RenameBookmarkAsync(_alice, bookmark.Id, new BookmarkRenameRequest { Name = "Tab one" }, Ct);

        var second = await _manager.RenameBookmarkAsync(_alice, bookmark.Id, new BookmarkRenameRequest { Name = "Tab two" }, Ct);

        Assert.Equal("Tab two", second.Name);
    }

    [Fact]
    public async Task IfUpdatedAt_AJavaScriptDate_OfTheCurrentToken_IsAccepted_ButNotOneOfAnEarlierChange()
    {
        // A client that keeps UpdatedAt in a JavaScript Date sends it back with milliseconds only.
        var bookmark = TestData.CreateBookmark(_alice.Id, 60, 90, _movie.Id) with
        {
            Name = "Bank job",
            UpdatedAt = new DateTime(2026, 9, 30, 8, 0, 0, 123, DateTimeKind.Utc).AddTicks(4567)
        };
        await _store.AddAsync(bookmark, Ct);
        var javaScript = new DateTime(2026, 9, 30, 8, 0, 0, 123, DateTimeKind.Utc);

        var renamed = await _manager.RenameBookmarkAsync(_alice, bookmark.Id, new BookmarkRenameRequest { Name = "Vault", IfUpdatedAt = javaScript }, Ct);

        Assert.Equal("Vault", renamed.Name);
        await AssertKindAsync(PluginErrorKind.Conflict, () => _manager.RenameBookmarkAsync(_alice, bookmark.Id, new BookmarkRenameRequest { Name = "Stale", IfUpdatedAt = javaScript }, Ct));
    }

    [Fact]
    public async Task IfUpdatedAt_IsCheckedUnderTheStoresLock()
    {
        // The token matched when the rename began; the other tab's rename lands between the ownership check and the write.
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Create("Bank job"), Ct);
        var racing = new InterferingBookmarkStore(_store);
        racing.BeforeNextWrite(b => b with { Name = "Other tab", UpdatedAt = BookmarkRecordRules.NextUpdatedAt(b.UpdatedAt, DateTime.UtcNow) });
        var manager = CreateManager(racing);

        var ex = await Assert.ThrowsAsync<BookmarkException>(
            () => manager.RenameBookmarkAsync(_alice, bookmark.Id, new BookmarkRenameRequest { Name = "This tab", IfUpdatedAt = bookmark.UpdatedAt }, Ct));

        Assert.Equal(PluginErrorKind.Conflict, ex.Kind);
        Assert.Equal("Other tab", _store.Get(bookmark.Id)!.Name);
        Assert.Equal(1, racing.RecordWrites);
    }

    [Fact]
    public async Task ReadOnlyData_RenameIs503_AndTheFileStaysAsItWas()
    {
        // A newer schema's file: readable, never written, whatever the token says.
        var existing = TestData.CreateBookmark(_alice.Id, itemId: _movie.Id) with { Name = "Bank job" };
        var file = _directory.Combine("newer", "bookmarks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var newer = System.Text.Json.JsonSerializer.Serialize(
            BookmarkStore.WithBookmarks(BookmarkStoreDocument.CreateEmpty() with { Version = BookmarkStoreDocument.CurrentVersion + 1 }, [existing]),
            Jellyfin.Plugin.Kit.Storage.JsonStoreFormat.Options);
        await File.WriteAllTextAsync(file, newer, Ct);
        using var store = BookmarkStore.Create(file, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var manager = CreateManager(store);

        Assert.Equal("Bank job", manager.GetBookmark(_alice, existing.Id).Name);
        await AssertKindAsync(PluginErrorKind.Unavailable, () => manager.RenameBookmarkAsync(_alice, existing.Id, new BookmarkRenameRequest { Name = "Vault", IfUpdatedAt = existing.UpdatedAt }, Ct));
        await AssertKindAsync(PluginErrorKind.Unavailable, () => manager.UpdateBookmarkAsync(_alice, existing.Id, Update(existing, name: "Vault"), Ct));
        await AssertKindAsync(PluginErrorKind.Unavailable, () => manager.CreateBookmarkAsync(_alice, Create("Vault"), Ct));
        Assert.Equal(newer, await File.ReadAllTextAsync(file, Ct));
    }

    [Fact]
    public async Task NamesAndNotes_NeverReachTheLog()
    {
        // Logs: ids and counts only, at every level.
        var bookmark = await _manager.CreateBookmarkAsync(_alice, Create("Secret name one", note: "Secret note"), Ct);
        await _manager.UpdateBookmarkAsync(_alice, bookmark.Id, Update(bookmark, name: "Secret name two", note: "Secret note"), Ct);
        await _manager.RenameBookmarkAsync(_alice, bookmark.Id, new BookmarkRenameRequest { Name = "Secret name three" }, Ct);
        await _manager.RenameBookmarkAsync(_alice, bookmark.Id, new BookmarkRenameRequest { Name = null }, Ct);
        await Assert.ThrowsAsync<BookmarkException>(() => _manager.RenameBookmarkAsync(_alice, bookmark.Id, new BookmarkRenameRequest { Name = "Secret name four", IfUpdatedAt = bookmark.UpdatedAt }, Ct));

        Assert.NotEmpty(_logger.Entries);
        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.All(_logger.Entries, e => Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, e.Level));
    }

    public void Dispose()
    {
        _store.Dispose();
        _directory.Dispose();
    }

    private static async Task AssertKindAsync(PluginErrorKind kind, Func<Task> action)
        => Assert.Equal(kind, (await Assert.ThrowsAnyAsync<PluginException>(action)).Kind);

    private static BookmarkUpdateRequest Update(BookmarkDto bookmark, string? name = null, string? note = null, DateTime? ifUpdatedAt = null)
        => new() { StartTicks = bookmark.StartTicks, EndTicks = bookmark.EndTicks, Name = name, Note = note, IfUpdatedAt = ifUpdatedAt };

    private static BookmarkUpdateRequest Update(Bookmark bookmark, string? name = null)
        => new() { StartTicks = bookmark.StartTicks, EndTicks = bookmark.EndTicks, Name = name };

    private BookmarkCreateRequest Create(string? name, string? note = null) => new()
    {
        ItemId = _movie.Id,
        StartTicks = 60 * TestData.OneSecond,
        EndTicks = 90 * TestData.OneSecond,
        Name = name,
        Note = note
    };

    private async Task<Bookmark> AddWithClipAsync()
    {
        var file = _directory.CreateFile("Heat (1995) - Bank job [00.01.00-00.01.30].mkv");
        var bookmark = TestData.CreateBookmark(_alice.Id, 60, 90, _movie.Id) with
        {
            Name = "Bank job",
            Note = "the note",
            MediaSourceId = "source",
            AudioStreamIndex = 1,
            Clip = new ClipInfo
            {
                Status = BookmarkClipStatus.Completed,
                Mode = BookmarkClipMode.Fast,
                StartTicks = 60 * TestData.OneSecond,
                EndTicks = 90 * TestData.OneSecond,
                FilePath = file,
                FileSize = 4,
                FileStartTicks = 60 * TestData.OneSecond,
                FileEndTicks = 90 * TestData.OneSecond
            }
        };
        await _store.AddAsync(bookmark, Ct);
        return bookmark;
    }

    private IBookmarkManager CreateManager(IBookmarkStore store) => new BookmarkManager(
        store,
        _libraryManager.Object,
        _clips.Object,
        _planner.Object,
        ClipFileSupport.Ops(store),
        ClipFileSupport.Guard(store),
        TestData.ConfigurationProvider(_configuration),
        _logger);
}
