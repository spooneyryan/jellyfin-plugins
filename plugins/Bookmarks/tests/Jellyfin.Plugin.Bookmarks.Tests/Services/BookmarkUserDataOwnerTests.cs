using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Jellyfin.Plugin.Kit.Users;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Services;

/// <summary>
/// Deleted users, through the kit's own consumer and reconciler over the
/// bookmark store's <see cref="BookmarkUserDataOwner"/>: the live event applies the policy (<c>KeepAsOrphan</c> keeps only
/// the clip-bearing bookmarks, as tombstones of the deleted id; <c>Anonymize</c> the same; <c>Delete</c> removes records and
/// clip files, a file in use later), after a snapshot; the reconciler deletes and changes nothing, even for a data folder
/// restored onto a rebuilt server (its guard trips), and <i>Remap by name</i> gives the bookmarks to the users of the same
/// names through <c>KnownUsers</c>.
/// </summary>
public sealed partial class BookmarkUserDataOwnerTests : IDisposable
{
    private static readonly DateTime Monday = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Created = Monday.AddDays(-30);

    private readonly TempDirectory _directory = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(Monday));
    private readonly ListLogger _logger = new();
    private readonly Mock<IClipExtractionService> _clips = new();
    private readonly User _alice = User("alice");
    private readonly User _bob = User("bob");
    private readonly string _root;
    private readonly List<IDisposable> _disposables = [];
    private DeletedUserPolicy _policy = DeletedUserPolicy.KeepAsOrphan;
    private BookmarkStore _store = null!;
    private BookmarkUserDataOwner _owner = null!;
    private UserReconciler _reconciler = null!;
    private List<User> _server = [];

    public BookmarkUserDataOwnerTests()
    {
        _root = _directory.Combine("clips");
        Directory.CreateDirectory(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StoreFile => _directory.Combine("bookmarks", "bookmarks.json");

    private string Snapshots => _directory.Combine("bookmarks", "snapshots");

    public void Dispose()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }

        _directory.Dispose();
    }

    [Theory]
    [InlineData(nameof(DeletedUserPolicy.KeepAsOrphan))]
    [InlineData(nameof(DeletedUserPolicy.Anonymize))]
    public async Task TheLiveEvent_KeepsOnlyTheClipBearingBookmarks_AsTombstonesOfTheDeletedId(string policy)
    {
        _policy = Enum.Parse<DeletedUserPolicy>(policy);
        Create(_alice, _bob);
        var plain = Bookmark(_bob);
        await _store.AddAsync(plain, Ct, _bob);
        var clipped = await AddAsync(Bookmark(_bob) with { Clip = Clip("clipped.mkv") });
        var remaking = await AddAsync(Bookmark(_bob) with { Clip = Clip("remaking.mp4") with { Status = BookmarkClipStatus.Queued, EstimatedBytes = 5 } });
        var queuedOnly = await AddAsync(Bookmark(_bob) with { Clip = new ClipInfo { Status = BookmarkClipStatus.Queued, RequestedAt = Created } });
        var tombstone = await AddAsync(BookmarkRecordRules.ToTombstone(Bookmark(_bob) with { Clip = Clip("kept.mkv") }, Created)!);
        var alices = await AddAsync(Bookmark(_alice));
        await _store.SetPreferencesAsync(_bob.Id, new BookmarkUserPreferences { AskForName = true }, Ct);
        _server.Remove(_bob);

        await DeleteUserAsync(_bob);

        Assert.Null(_store.Get(plain.Id));
        Assert.Null(_store.Get(queuedOnly.Id));
        foreach (var kept in new[] { clipped, remaking })
        {
            var now = _store.Get(kept.Id)!;
            Assert.Equal((Monday, _bob.Id), (now.DeletedUtc!.Value, now.UserId));
            Assert.Equal(BookmarkClipStatus.Completed, now.Clip!.Status);
            Assert.True(File.Exists(now.Clip.FilePath));
        }

        Assert.Same(tombstone, _store.Get(tombstone.Id));
        Assert.Same(alices, _store.Get(alices.Id));
        Assert.Null(_store.GetPreferences(_bob.Id));
        Assert.Equal("bob", _store.KnownUsers.NameOf(_bob.Id));
        Assert.True(_reconciler.IsOrphan(_bob.Id));

        // Every job of the user is stopped; nothing of alice's.
        foreach (var id in new[] { plain.Id, clipped.Id, remaking.Id, queuedOnly.Id })
        {
            _clips.Verify(c => c.StopRunning(id), Times.Once);
        }

        _clips.Verify(c => c.StopRunning(alices.Id), Times.Never);

        // The snapshot before it.
        Assert.Single(Directory.GetFiles(Path.Combine(Snapshots, "pre-op"), "bookmarks-user-deleted-*"));
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains("2 bookmarks removed, 2 kept as clip-only tombstones", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(nameof(DeletedUserPolicy.KeepAsOrphan))]
    [InlineData(nameof(DeletedUserPolicy.Delete))]
    public async Task TheLiveEvent_LeavesNoNameOrNote_InTheTombstonesOrTheStoresBackup(string policy)
    {
        // Bookmarks 1.2.1: a tombstone kept its name and note, and the deletion's write left the earlier document in the .bak.
        _policy = Enum.Parse<DeletedUserPolicy>(policy);
        Create(_alice, _bob);
        var clipped = await AddAsync(Bookmark(_bob) with { Name = "Residue-name", Note = "Residue-note", Clip = Clip("clipped.mkv") });
        var tombstone = await AddAsync(BookmarkRecordRules.ToTombstone(Bookmark(_bob) with { Name = "Residue-tomb", Note = "Residue-tomb-note", Clip = Clip("kept.mkv") }, Created)!);
        await AddAsync(Bookmark(_alice) with { Note = "Alice-note" });
        Assert.Contains("Residue-", await File.ReadAllTextAsync(StoreFile + ".bak", Ct), StringComparison.Ordinal);
        _server.Remove(_bob);

        await DeleteUserAsync(_bob);

        Assert.DoesNotContain("Residue-", await File.ReadAllTextAsync(StoreFile, Ct), StringComparison.Ordinal);
        Assert.DoesNotContain("Residue-", await File.ReadAllTextAsync(StoreFile + ".bak", Ct), StringComparison.Ordinal);
        Assert.Contains("Alice-note", await File.ReadAllTextAsync(StoreFile + ".bak", Ct), StringComparison.Ordinal);
        if (_policy == DeletedUserPolicy.KeepAsOrphan)
        {
            Assert.All(new[] { clipped.Id, tombstone.Id }.Select(id => _store.Get(id)!), b => Assert.True(BookmarkRecordRules.IsTombstone(b) && b.Name is null && b.Note is null));
        }
    }

    [Fact]
    public async Task Delete_RemovesEveryRecordAndItsClipFile_AndAFileInUseIsDeletedLater()
    {
        _policy = DeletedUserPolicy.Delete;
        Create(_alice, _bob);
        var clipped = await AddAsync(Bookmark(_bob) with { Clip = Clip("clipped.mkv") });
        var held = await AddAsync(Bookmark(_bob) with { Clip = Clip("held.mp4") });
        var tombstone = await AddAsync(BookmarkRecordRules.ToTombstone(Bookmark(_bob) with { Clip = Clip("kept.mkv") }, Created)!);
        var plain = await AddAsync(Bookmark(_bob));
        var alices = await AddAsync(Bookmark(_alice) with { Clip = Clip("alice.mkv") });
        _server.Remove(_bob);

        // Held open without delete sharing, as Jellyfin's playback or a download holds a file.
        using (new FileStream(held.Clip!.FilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await DeleteUserAsync(_bob);
        }

        Assert.Equal([alices.Id], _store.GetAll().Select(b => b.Id));
        Assert.False(File.Exists(clipped.Clip!.FilePath));
        Assert.False(File.Exists(tombstone.Clip!.FilePath));
        Assert.True(File.Exists(held.Clip!.FilePath));
        Assert.True(File.Exists(alices.Clip!.FilePath));
        Assert.Equal(held.Clip.FilePath, Assert.Single(_store.PendingFileDeletes).Path, ignoreCase: true);
        Assert.Null(_store.KnownUsers.NameOf(_bob.Id));
        Assert.Null(_store.Get(plain.Id));

        // Released: the retry (the janitor at the next start, or the daily maintenance) deletes it.
        var retry = await ClipFileSupport.Ops(_store).RetryPendingDeletesAsync(BackgroundWriter.Maintenance, Ct);
        Assert.Equal(1, retry.Deleted);
        Assert.False(File.Exists(held.Clip.FilePath));
        Assert.Empty(_store.PendingFileDeletes);
    }

    [Fact]
    public async Task ARestoredDataFolder_TheReconcilerDeletesAndChangesNothing_ItsGuardTrips_AndRemapByNameMapsThroughKnownUsers()
    {
        // A data folder restored onto a rebuilt server: every stored user id is unknown; the users have the same names.
        var oldAlex = User("alex");
        var oldViewer = User("viewer");
        Create(oldAlex, oldViewer);
        foreach (var user in new[] { oldAlex, oldAlex, oldViewer })
        {
            await _store.AddAsync(Bookmark(user) with { Clip = user == oldViewer ? Clip("viewer.mkv") : null }, Ct, user);
        }

        await _store.SetPreferencesAsync(oldAlex.Id, new BookmarkUserPreferences { PauseWhileNaming = true }, Ct);
        var newAlex = User("Alex");
        var newViewer = User("viewer");
        _server = [newAlex, newViewer];
        var bytes = await File.ReadAllBytesAsync(StoreFile, Ct);

        var run = await _reconciler.ReconcileAsync(Ct);

        Assert.True(run.GuardTripped);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.False(_reconciler.IsOrphan(oldAlex.Id));
        Assert.Equal(UserReconciler.MismatchBanner, _reconciler.Describe()["Banner"]);
        Assert.Equal(3, _store.GetAll().Count);

        var dryRun = await _reconciler.RemapByNameAsync(dryRun: true, Ct);
        Assert.Equal(2, dryRun.Mapped.Count);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(StoreFile, Ct));

        var remap = await _reconciler.RemapByNameAsync(dryRun: false, Ct);

        Assert.Equal(3, remap.MovedRecords);
        Assert.Equal(2, _store.GetAll().Count(b => b.UserId == newAlex.Id));
        Assert.Single(_store.GetAll(), b => b.UserId == newViewer.Id && b.Clip is not null);
        Assert.True(_store.GetPreferences(newAlex.Id)!.PauseWhileNaming);
        Assert.Null(_store.GetPreferences(oldAlex.Id));
        Assert.Equal("Alex", _store.KnownUsers.NameOf(newAlex.Id));
        Assert.Null(_store.KnownUsers.NameOf(oldAlex.Id));
        Assert.Single(Directory.GetFiles(Path.Combine(Snapshots, "pre-op"), "bookmarks-remap-*"));
        Assert.Null(_reconciler.Describe()["Banner"]);
    }

    [Fact]
    public async Task OneUnknownUser_IsMarkedAnOrphan_AndKeepsEveryBookmark()
    {
        // A user deleted while Bookmarks was off: found by the reconciler, never deleted by it.
        var gone = User("gone");
        Create(_alice, gone);
        for (var i = 0; i < 12; i++)
        {
            await AddAsync(Bookmark(_alice));
        }

        await _store.AddAsync(Bookmark(gone), Ct, gone);
        _server.Remove(gone);
        var records = System.Text.Json.JsonSerializer.Serialize(_store.Documents.Current.Bookmarks, Jellyfin.Plugin.Kit.Storage.JsonStoreFormat.Options);

        var run = await _reconciler.ReconcileAsync(Ct);

        // Only KnownUsers was refreshed (the server's names); no record changed.
        Assert.False(run.GuardTripped);
        Assert.Equal([gone.Id], run.NewlyOrphaned);
        Assert.True(_reconciler.IsOrphan(gone.Id));
        Assert.Equal(records, System.Text.Json.JsonSerializer.Serialize(_store.Documents.Current.Bookmarks, Jellyfin.Plugin.Kit.Storage.JsonStoreFormat.Options));
        Assert.Equal(("alice", "gone"), (_store.KnownUsers.NameOf(_alice.Id), _store.KnownUsers.NameOf(gone.Id)));
        Assert.Equal("1 records belong to 1 unknown users.", _reconciler.Describe()["Message"]);
        Assert.Equal(13, _owner.CountRecordsByUser().Values.Sum());
    }

    [Fact]
    public async Task ReadOnlyData_IsLeftAlone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
        var original = StoreFixtures.Read(StoreFixtures.Schema3);
        await File.WriteAllBytesAsync(StoreFile, original, Ct);
        Create(_alice);
        var someone = _owner.CountRecordsByUser().Keys.First();

        Assert.True(_owner.IsReadOnly);
        Assert.Equal(0, await _owner.HandleUserDeletedAsync(someone, DeletedUserPolicy.Delete, Ct));
        Assert.Equal(0, await _owner.RemapUsersAsync(new Dictionary<Guid, Guid> { [someone] = _alice.Id }, Ct));
        await _owner.RefreshKnownUsersAsync(new Dictionary<Guid, string> { [someone] = "renamed" }, Ct);
        await new KitUserDeletedConsumer(_reconciler).OnEvent(new UserDeletedEventArgs(User("x") is var x ? SetId(x, someone) : null!));
        Assert.Equal(original, await File.ReadAllBytesAsync(StoreFile, Ct));
    }

    [Fact]
    public async Task CountsAndNames_IncludeTombstones_AndAWriteRefreshesTheName()
    {
        Create(_alice, _bob);
        await _store.AddAsync(Bookmark(_bob), Ct, _bob);
        await AddAsync(BookmarkRecordRules.ToTombstone(Bookmark(_bob) with { Clip = Clip("x.mkv") }, Created)!);
        await AddAsync(Bookmark(_alice));

        Assert.Equal(2, _owner.CountRecordsByUser()[_bob.Id]);
        Assert.Equal(1, _owner.CountRecordsByUser()[_alice.Id]);
        Assert.Equal("bookmarks", _owner.Name);
        Assert.Equal("bob", _owner.KnownUsers.NameOf(_bob.Id));

        await _owner.RefreshKnownUsersAsync(new Dictionary<Guid, string> { [_bob.Id] = "robert", [_alice.Id] = "alice" }, Ct);
        Assert.Equal(("robert", "alice"), (_owner.KnownUsers.NameOf(_bob.Id), _owner.KnownUsers.NameOf(_alice.Id)));
    }

    private static User User(string name)
    {
        var user = TestData.CreateUser(name);
        user.Id = Guid.NewGuid();
        return user;
    }

    private static User SetId(User user, Guid id)
    {
        user.Id = id;
        return user;
    }

    private Bookmark Bookmark(User user)
    {
        var item = Guid.NewGuid();
        return new Bookmark
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ItemId = item,
            StartTicks = 600_000_000,
            EndTicks = 900_000_000,
            ItemName = "Heat (1995)",
            Identity = new IdentitySnapshot { ItemId = item, Kind = "Movie", Name = "Heat" },
            AttachedUtc = Created,
            CreatedAt = Created,
            UpdatedAt = Created
        };
    }

    private ClipInfo Clip(string fileName)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + "-" + fileName);
        File.WriteAllBytes(path, [1, 2, 3]);
        return new ClipInfo
        {
            Status = BookmarkClipStatus.Completed,
            Mode = ClipFileNaming.GetMode(path),
            StartTicks = 600_000_000,
            EndTicks = 900_000_000,
            RequestedAt = Created,
            CompletedAt = Created,
            FilePath = path,
            FileSize = 3,
            FileStartTicks = 600_000_000,
            FileEndTicks = 900_000_000
        };
    }

    private async Task<Bookmark> AddAsync(Bookmark bookmark)
    {
        await _store.AddAsync(bookmark, Ct);
        return _store.Get(bookmark.Id)!;
    }

    private async Task DeleteUserAsync(User user)
        => await new KitUserDeletedConsumer(_reconciler).OnEvent(new UserDeletedEventArgs(user));

    private void Create(params User[] users)
    {
        _server = [.. users];
        _store = File.Exists(StoreFile)
            ? BookmarkStore.Create(StoreFile, _logger, _clock)
            : ClipFileSupport.StoreWithClipRoots(StoreFile, _root);
        _disposables.Add(_store);
        _owner = new BookmarkUserDataOwner(
            _store,
            new BackgroundWriteGate(_store, NullLogger<BackgroundWriteGate>.Instance),
            ClipFileSupport.Ops(_store),
            () => _clips.Object,
            Snapshots,
            _logger,
            _clock);
        var marks = new KitUserMarks(_directory.Combine("bookmarks", KitUsersDocument.FileName), _logger, _clock);
        _disposables.Add(marks);
        _reconciler = new UserReconciler(() => _server.ToDictionary(u => u.Id, u => u.Username), [_owner], marks, () => _policy, _logger, _clock);
        _disposables.Add(_reconciler);
    }
}
