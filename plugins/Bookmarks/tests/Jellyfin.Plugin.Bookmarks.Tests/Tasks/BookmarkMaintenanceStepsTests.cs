using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tasks;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Tasks;

/// <summary>
/// Bookmarks' own steps of "Bookmarks: Maintenance": the daily snapshot of <c>bookmarks.json</c>
/// (<c>SnapshotsToKeep</c>, 7 by default, 0 for none, none while read-only), the retry of the clip files waiting to be
/// deleted (none while read-only) and, since 1.2.0, the missing-clip sweep, the last step (its own tests are
/// <c>Clips/MissingClipSweepTests</c>).
/// </summary>
public sealed class BookmarkMaintenanceStepsTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly PluginConfiguration _configuration = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Root => _directory.Combine("bookmarks");

    private string SnapshotFolder => Path.Combine(Root, "snapshots");

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task Snapshots_KeepsSnapshotsToKeep_AndNoneWithZero()
    {
        using var store = ClipFileSupport.StoreWithClipRoots(Path.Combine(Root, "bookmarks.json"), _directory.Combine("clips"));
        await using var services = Services(store);
        _configuration.SnapshotsToKeep = 2;

        for (var i = 0; i < 3; i++)
        {
            var result = (await BookmarkMaintenanceSteps.SnapshotAsync(services, Ct))!;
            Assert.StartsWith("bookmarks-", (string)result["Snapshot"]!, StringComparison.Ordinal);
        }

        Assert.Equal(2, Directory.GetFiles(SnapshotFolder, "bookmarks-*.json").Length);

        _configuration.SnapshotsToKeep = 0;
        var none = (await BookmarkMaintenanceSteps.SnapshotAsync(services, Ct))!;
        Assert.Null(none["Snapshot"]);
        Assert.Equal("SnapshotsToKeep is 0", none["Skipped"]);
        Assert.Equal(7, new PluginConfiguration().SnapshotsToKeep);
    }

    [Fact]
    public async Task ReadOnlyData_GetsNoSnapshot_AndNoClipFileRetry()
    {
        Directory.CreateDirectory(Root);
        var original = StoreFixtures.Read(StoreFixtures.Schema3);
        await File.WriteAllBytesAsync(Path.Combine(Root, "bookmarks.json"), original, Ct);
        using var store = BookmarkStore.Create(Path.Combine(Root, "bookmarks.json"), NullLogger.Instance);
        await using var services = Services(store);

        var snapshot = (await BookmarkMaintenanceSteps.SnapshotAsync(services, Ct))!;
        var retry = (await BookmarkMaintenanceSteps.RetryClipFilesAsync(services, Ct))!;

        Assert.Equal("ReadOnly", snapshot["Skipped"]);
        Assert.False(Directory.Exists(SnapshotFolder));
        Assert.Equal(0, retry["Deleted"]);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(Root, "bookmarks.json"), Ct));
    }

    [Fact]
    public async Task ClipFiles_DeletesTheFilesWaitingToBeDeleted()
    {
        var clips = _directory.Combine("clips");
        Directory.CreateDirectory(clips);
        using var store = ClipFileSupport.StoreWithClipRoots(Path.Combine(Root, "bookmarks.json"), clips);
        var waiting = Path.Combine(clips, "old remake.mkv");
        await File.WriteAllBytesAsync(waiting, [1], Ct);
        await store.AddPendingFileDeleteAsync(waiting, Ct);
        await using var services = Services(store);

        var result = (await BookmarkMaintenanceSteps.RetryClipFilesAsync(services, Ct))!;

        Assert.Equal(1, result["Deleted"]);
        Assert.False(File.Exists(waiting));
        Assert.Empty(store.PendingFileDeletes);
    }

    [Fact]
    public async Task ClipFiles_FinishesTheRenamesTheServerStoppedDuring_BeforeTheSweepJudgesTheirClips()
    {
        // A rename the server stopped during is finished first, so the sweep never takes its clip for missing.
        var clips = _directory.Combine("clips");
        Directory.CreateDirectory(clips);
        ClipFileSupport.MarkClipFolder(clips);
        using var store = ClipFileSupport.StoreWithClipRoots(Path.Combine(Root, "bookmarks.json"), clips);
        var file = Path.Combine(clips, "Heat (1995) - Old [00.01.00-00.01.30].mkv");
        await File.WriteAllBytesAsync(file, [1], Ct);
        var record = TestData.CreateBookmark(Guid.NewGuid()) with { Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, Mode = BookmarkClipMode.Fast, FilePath = file, FileSize = 1 } };
        await store.AddAsync(record, Ct);
        var renamed = Path.Combine(clips, "Heat (1995) - New [00.01.00-00.01.30].mkv");
        new ClipRenameJournal(Root, PhysicalClipFileSystem.Instance, NullLogger.Instance).Add(new ClipRenameEntry
        {
            BookmarkId = record.Id,
            From = file,
            To = renamed,
            File = PhysicalClipFileSystem.ReadFileIdentity(file),
            UpdatedAt = store.Get(record.Id)!.UpdatedAt,
            StartedUtc = DateTime.UtcNow
        });
        File.Move(file, renamed);
        await using var services = Services(store);

        var result = (await BookmarkMaintenanceSteps.RetryClipFilesAsync(services, Ct))!;

        Assert.Equal((1, 0, 0), ((int)result["RenamesFinished"]!, (int)result["RenamesWaiting"]!, (int)result["RenamesUnreadable"]!));
        Assert.Equal(renamed, store.Get(record.Id)!.Clip!.FilePath);
        Assert.Empty(Directory.GetFiles(Root, ClipRenameJournal.FilePrefix + "*"));
    }

    [Fact]
    public void TheSteps_AreAddedInTheirPlaces_TheMissingClipSweepLast()
    {
        var services = new ServiceCollection().AddBookmarkMaintenanceSteps();

        var steps = services.Select(d => d.ImplementationInstance).OfType<KitComponent<IKitMaintenanceStep>>().ToList();
        Assert.Equal([BookmarkMaintenanceSteps.SnapshotsStep, BookmarkMaintenanceSteps.ClipFilesStep, BookmarkMaintenanceSteps.MissingClipsStep], steps.Select(s => s.Name));
        using var provider = services.BuildServiceProvider();
        Assert.Equal([KitMaintenanceOrder.Snapshots, KitMaintenanceOrder.Plugin, KitMaintenanceOrder.Plugin + 10], steps.Select(s => s.Create(provider).Order));

        // The sweep runs after every kit step (the reattach is 510) and the clip-file retries, so the day's snapshot, the first step,
        // holds the records before anything is archived.
        Assert.True(BookmarkMaintenanceSteps.MissingClipsOrder > KitMaintenanceOrder.Reattach);
    }

    [Fact]
    public async Task MissingClips_RunsTheSweep_AndAnswersItsResult()
    {
        // The step resolves the sweep and gives its result to the kit's maintenance (Diagnostics' Maintenance entry).
        // The clip folder has its marker file, as the clip worker leaves it.
        var clips = _directory.Combine("clips");
        Directory.CreateDirectory(clips);
        ClipFileSupport.MarkClipFolder(clips);
        using var store = ClipFileSupport.StoreWithClipRoots(Path.Combine(Root, "bookmarks.json"), clips);
        var record = TestData.CreateBookmark(Guid.NewGuid()) with
        {
            Clip = new ClipInfo
            {
                Status = BookmarkClipStatus.Completed,
                Mode = BookmarkClipMode.Fast,
                FilePath = Path.Combine(clips, "alice", "Heat (1995) - gone [00.01.00-00.01.30].mkv"),
                FileSize = 10
            }
        };
        await store.AddAsync(record, Ct);
        var files = ClipFileSupport.Ops(store);
        await using var services = new ServiceCollection()
            .AddSingleton(new MissingClipSweep(
                store,
                new BackgroundWriteGate(store, NullLogger<BackgroundWriteGate>.Instance),
                files,
                TestData.ConfigurationProvider(_configuration),
                () => throw new InvalidOperationException("Nothing is due."),
                () => throw new InvalidOperationException("Nothing is due."),
                NullLogger.Instance))
            .BuildServiceProvider();

        var result = (await BookmarkMaintenanceSteps.SweepMissingClipsAsync(services, Ct))!;

        Assert.Equal((1, 1, 1), ((int)result["Checked"]!, (int)result["Missing"]!, (int)result["Marked"]!));
        Assert.NotNull(store.Get(record.Id)!.Clip!.MissingSinceUtc);
    }

    private ServiceProvider Services(IBookmarkStore store)
        => new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(BookmarkPaths.ForRoot(Root))
            .AddSingleton(TestData.ConfigurationProvider(_configuration))
            .AddSingleton(ClipFileSupport.Ops(store))
            .BuildServiceProvider();
}
