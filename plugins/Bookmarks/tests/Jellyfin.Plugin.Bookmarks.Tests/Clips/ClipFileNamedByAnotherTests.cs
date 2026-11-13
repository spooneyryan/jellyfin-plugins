using System;
using System.Collections.Generic;
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
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.Users;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// Bookmarks 1.1.3's defence in depth: a clip file is never deleted while another record still names it, by a user's delete
/// (a bookmark with its clip, a clip, a tombstone's clip), a bulk delete, a remake's clean-up, the deleted-user <c>Delete</c>
/// policy or any other automatic delete. The record letting go of it does so as after a delete, the file stays for the other
/// record, and a Warning names both. In 1.1.2 a new clip could take the name of a "File missing" record's file (fixed in the
/// planner since), so two records named one file and the first one's delete or remake deleted the other's clip.
/// </summary>
public sealed class ClipFileNamedByAnotherTests : IAsyncLifetime
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    private readonly TempDirectory _directory = new();
    private readonly string _root;
    private readonly BookmarkStore _store;
    private readonly ListLogger _logger = new();
    private readonly Mock<IClipExtractionService> _clips = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly User _bob = TestData.CreateUser("bob");
    private readonly List<ClipExtractionService> _services = [];

    public ClipFileNamedByAnotherTests()
    {
        _root = _directory.Combine("clips");
        Directory.CreateDirectory(_root);

        // As the clip worker leaves a clip folder: with its marker file, so a file gone from it is gone.
        ClipFileSupport.MarkClipFolder(_root);
        _store = ClipFileSupport.StoreWithClipRoots(_directory.Combine("bookmarks.json"), _root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var service in _services)
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }

        _store.Dispose();
        _directory.Dispose();
    }

    [Fact]
    public async Task DeleteOnce_OfAFileAnotherRecordNames_KeepsIt_LetsTheRecordGo_AndWarns()
    {
        var file = Clip("shared.mp4");
        var a = await AddAsync(_alice, file);
        var b = await AddAsync(_bob, file);
        var files = ClipFileSupport.Ops(_store, _logger);

        var kept = files.DeleteOnce(file, a.Id, 1);

        Assert.Equal(ClipFileOutcome.NamedByAnotherRecord, kept.Outcome);
        Assert.True(kept.IsGone);
        Assert.True(File.Exists(file));
        var warning = Assert.Single(_logger.At(LogLevel.Warning));
        Assert.Contains(a.Id.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.Contains(b.Id.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("shared", warning.Message, StringComparison.Ordinal);

        // The records the same write removes with their files let go of it together: it is deleted.
        Assert.Equal(ClipFileOutcome.Done, files.DeleteOnce(file, a.Id, 1, new HashSet<Guid> { b.Id }).Outcome);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task DeleteOnce_NamesTheFileByItsFullPath_InAnyCase()
    {
        var file = Clip("Shared.mp4");
        var a = await AddAsync(_alice, file);
        await AddAsync(_bob, Path.Combine(_root, "alice", ".", "Shared.mp4").ToUpperInvariant());

        var result = ClipFileSupport.Ops(_store, _logger).DeleteOnce(file, a.Id, 1);

        Assert.Equal(OperatingSystem.IsWindows() ? ClipFileOutcome.NamedByAnotherRecord : ClipFileOutcome.Done, result.Outcome);
        Assert.Equal(OperatingSystem.IsWindows(), File.Exists(file));
    }

    [Fact]
    public async Task DeleteOrQueue_OfAFileARecordStillNames_KeepsIt_AndNeverListsIt()
    {
        // An automatic delete runs once its record let go of the file: any record that names it keeps it, even one in use.
        var file = Clip("kept.mp4");
        var b = await AddAsync(_bob, file);
        var fileSystem = new FakeClipFileSystem();
        fileSystem.HoldOpen(file);

        var result = await ClipFileSupport.Ops(_store, _logger, fileSystem).DeleteOrQueueAsync(file, Guid.NewGuid(), BackgroundWriter.ClipWorker, Ct);

        Assert.Equal(ClipFileOutcome.NamedByAnotherRecord, result.Outcome);
        Assert.False(result.Queued);
        Assert.Empty(fileSystem.Deletes);
        Assert.Empty(_store.PendingFileDeletes);
        Assert.True(File.Exists(file));
        Assert.Equal(file, _store.Get(b.Id)!.Clip!.FilePath);
    }

    [Fact]
    public async Task DeletingABookmarkWithItsClip_WhileAnotherBookmarkNamesTheFile_RemovesTheBookmark_AndKeepsTheFile()
    {
        var file = Clip("one.mp4");
        var a = await AddAsync(_alice, file);
        var b = await AddAsync(_alice, file);

        await Manager().DeleteBookmarkAsync(_alice, a.Id, deleteClipFile: true, Ct);

        Assert.Null(_store.Get(a.Id));
        Assert.Equal(b, _store.Get(b.Id));
        Assert.True(File.Exists(file));
        Assert.Single(_logger.At(LogLevel.Warning));
    }

    [Fact]
    public async Task DeletingAClip_OrATombstonesClip_WhileAnotherUsersRecordNamesTheFile_RemovesOnlyTheReference()
    {
        var file = Clip("two.mp4");
        var a = await AddAsync(_alice, file);
        var t = await AddAsync(_alice, file);
        await _store.UpdateAsync(t.Id, r => BookmarkRecordRules.ToTombstone(r, DateTime.UtcNow)!, Ct);
        var b = await AddAsync(_bob, file);

        var after = await Manager().DeleteClipAsync(_alice, a.Id, Ct);

        Assert.NotNull(after);
        Assert.Null(_store.Get(a.Id)!.Clip);
        Assert.True(File.Exists(file));

        Assert.Null(await Manager().DeleteClipAsync(_alice, t.Id, Ct));
        Assert.Null(_store.Get(t.Id));
        Assert.True(File.Exists(file));
        Assert.Equal(b, _store.Get(b.Id));
        Assert.Equal(2, _logger.At(LogLevel.Warning).Count());

        // The last record that names it deletes it.
        await Manager().DeleteBookmarkAsync(_bob, b.Id, deleteClipFile: true, Ct);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task ABulkDeleteWithClips_DeletesAFileOnlyItsRecordsName_AndKeepsOneAnyOtherRecordNames()
    {
        var both = Clip("both.mp4");
        var a1 = await AddAsync(_alice, both);
        var a2 = await AddAsync(_alice, both);
        var bobs = Clip("bobs.mp4");
        var a3 = await AddAsync(_alice, bobs);
        var b = await AddAsync(_bob, bobs);
        var tombstoned = Clip("tombstoned.mp4");
        var a4 = await AddAsync(_alice, tombstoned);
        var t = await AddAsync(_alice, tombstoned);
        await _store.UpdateAsync(t.Id, r => BookmarkRecordRules.ToTombstone(r, DateTime.UtcNow)!, Ct);
        var own = Clip("own.mp4");
        var a5 = await AddAsync(_alice, own);

        var result = await Manager().BulkDeleteAsync(
            _alice,
            new BookmarkBulkDeleteRequest { Ids = [a1.Id, a2.Id, a3.Id, a4.Id, t.Id, a5.Id], DeleteClips = true },
            Ct);

        Assert.Equal([a1.Id, a2.Id, a3.Id, a4.Id, a5.Id], result.Deleted);
        Assert.Equal([t.Id], result.NotFound);
        Assert.Empty(result.InUse);
        Assert.Empty(result.Failed);
        Assert.False(File.Exists(both));
        Assert.False(File.Exists(own));

        // Bob's record and Alice's tombstone (a bulk delete leaves tombstones alone) still name theirs.
        Assert.True(File.Exists(bobs));
        Assert.True(File.Exists(tombstoned));
        Assert.Equal(b, _store.Get(b.Id));
        Assert.Equal(tombstoned, _store.Get(t.Id)!.Clip!.FilePath);
        Assert.Equal(2, _logger.At(LogLevel.Warning).Count());
    }

    [Fact]
    public async Task TheDeleteUserPolicy_KeepsAFileAnotherUsersRecordNames()
    {
        var shared = Clip("shared.mp4");
        var own = Clip("own.mp4");
        await AddAsync(_alice, shared);
        await AddAsync(_alice, own);
        var b = await AddAsync(_bob, shared);
        using var snapshots = new TempDirectory();
        var owner = new BookmarkUserDataOwner(
            _store,
            new BackgroundWriteGate(_store, NullLogger<BackgroundWriteGate>.Instance),
            ClipFileSupport.Ops(_store, _logger),
            () => _clips.Object,
            snapshots.Path,
            NullLogger.Instance);

        Assert.Equal(2, await owner.HandleUserDeletedAsync(_alice.Id, DeletedUserPolicy.Delete, Ct));

        Assert.False(File.Exists(own));
        Assert.True(File.Exists(shared));
        Assert.Equal(b, Assert.Single(_store.GetAll()));
        Assert.Single(_logger.At(LogLevel.Warning));
    }

    [Fact]
    public async Task ARemake_WhosePreviousFileAnotherRecordNamesToo_KeepsThatFile()
    {
        // The 1.1.2 repro, with 1.1.2's planner rule (a file there, or its .partial): A's clip file deleted by hand, B's clip
        // took its name, so both name one file. A's remake then deleted B's clip in its clean-up.
        var planner = new FakeClipPlanner(_directory) { NamedFiles = true };
        var service = await StartWorkerAsync(planner);
        var a = await AddAsync(_alice, null);
        var b = await AddAsync(_alice, null);
        var first = (await MakeClipAsync(service, a)).Clip!.FilePath!;
        File.Delete(first);
        Assert.Equal(first, (await MakeClipAsync(service, b)).Clip!.FilePath);

        var remade = (await MakeClipAsync(service, a)).Clip!.FilePath!;

        Assert.NotEqual(first, remade);
        Assert.True(File.Exists(remade));
        Assert.True(File.Exists(first));
        Assert.Equal(first, _store.Get(b.Id)!.Clip!.FilePath);
        Assert.Empty(_store.PendingFileDeletes);
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.Contains(b.Id.ToString(), StringComparison.Ordinal));
    }

    private string Clip(string name) => _directory.CreateFile(Path.Combine("clips", "alice", name));

    private async Task<Bookmark> AddAsync(User user, string? file)
    {
        var bookmark = TestData.CreateBookmark(user.Id, 60, 90) with
        {
            Name = "Bank job",
            Clip = file is null
                ? null
                : new ClipInfo
                {
                    Status = BookmarkClipStatus.Completed,
                    Mode = BookmarkClipMode.Accurate,
                    StartTicks = 60 * TestData.OneSecond,
                    EndTicks = 90 * TestData.OneSecond,
                    RequestedAt = DateTime.UtcNow,
                    CompletedAt = DateTime.UtcNow,
                    FilePath = file,
                    FileSize = 4
                }
        };
        await _store.AddAsync(bookmark, Ct, user);
        return _store.Get(bookmark.Id)!;
    }

    private IBookmarkManager Manager() => new BookmarkManager(
        _store,
        Mock.Of<ILibraryManager>(),
        _clips.Object,
        Mock.Of<IClipPlanner>(),
        ClipFileSupport.Ops(_store, _logger),
        ClipFileSupport.Guard(_store),
        TestData.ConfigurationProvider(new PluginConfiguration()),
        NullLogger<BookmarkManager>.Instance);

    private async Task<ClipExtractionService> StartWorkerAsync(FakeClipPlanner planner)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var files = ClipFileSupport.Ops(_store, _logger);
        var service = new ClipExtractionService(
            _store,
            new BackgroundWriteGate(_store, NullLogger<BackgroundWriteGate>.Instance),
            planner,
            new FakeFfmpegRunner(),
            files,
            ClipFileSupport.Janitor(_store, files),
            ClipFileSupport.Guard(_store),
            TestData.ConfigurationProvider(new PluginConfiguration()),
            host.Object,
            NullLogger<ClipExtractionService>.Instance);
        _services.Add(service);
        await service.StartAsync(Ct);
        return service;
    }

    private async Task<Bookmark> MakeClipAsync(ClipExtractionService service, Bookmark bookmark)
    {
        var before = _store.Get(bookmark.Id)?.Clip?.RequestedAt;
        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, null, Ct);
        var deadline = DateTime.UtcNow + _timeout;
        while (_store.Get(bookmark.Id)?.Clip is not { Status: BookmarkClipStatus.Completed } clip || clip.RequestedAt == before)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the clip.");
            await Task.Delay(20, Ct);
        }

        // The remake's clean-up of the previous file runs right after the record is written.
        await Task.Delay(100, Ct);
        return _store.Get(bookmark.Id)!;
    }
}
