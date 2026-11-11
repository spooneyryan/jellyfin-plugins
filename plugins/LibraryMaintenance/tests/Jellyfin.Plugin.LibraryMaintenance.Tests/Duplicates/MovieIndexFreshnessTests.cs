using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Duplicates;

/// <summary>
/// The index's freshness with a fake clock and a fake library: a batch marks the index out of date and builds it when
/// the page was used in the last 24 hours or the warm-up after scans is on; the scan rule (a warm index is rebuilt during a
/// scan only by the post-scan build, which isn't <c>BuiltDuringScan</c>; a cold start builds once anyway, and is); a build
/// the rule put off runs when the scan ends unless the post-scan build covered it; a read waits up to
/// <c>RequestWaitSeconds</c> and answers from the old index, stale, or with none; Duplicates' consumer and the kit's
/// monitor's 60 s quiet period end to end.
/// </summary>
public sealed class MovieIndexFreshnessTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ABatch_MarksTheIndexOutOfDate_AndBuildsIt_WithTheWarmUpOn()
    {
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        using var service = Service(library);

        await service.OnLibraryChanged().WaitAsync(Wait, Ct);

        Assert.Equal(1L, service.Describe()["Version"]);
        Assert.False(service.GetStatus().Dirty);
        Assert.Equal(1, Builds(service)["Changes"]);
        Assert.Equal("Changes", service.Describe()["LastBuildReason"]);
    }

    [Fact]
    public async Task WithTheWarmUpOff_ABatch_BuildsOnlyWhileThePageWasUsedInTheLast24Hours()
    {
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        library.Configuration = new PluginConfiguration { IncludedLibraryIds = library.Configuration.IncludedLibraryIds, WarmIndexAfterScan = false };
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        using var service = Service(library, clock);

        // Nobody used the page since the server started: only marked out of date, with no index and no query.
        await service.OnLibraryChanged().WaitAsync(Wait, Ct);
        Assert.Empty(library.IdQueries);
        Assert.True((bool)service.Describe()["Dirty"]!);
        Assert.Null(service.PageUsedUtc);

        // The page asks (GET /Index counts as a use, Diagnostics doesn't); 23 hours later a batch builds.
        service.GetStatus();
        Assert.Equal(clock.GetUtcNow(), service.PageUsedUtc);
        clock.Advance(TimeSpan.FromHours(23));
        await service.OnLibraryChanged().WaitAsync(Wait, Ct);
        Assert.Equal(1L, service.Describe()["Version"]);

        // More than 24 hours after the last use: out of date again, until the next read builds it.
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await service.OnLibraryChanged().WaitAsync(Wait, Ct);
        await service.OnLibraryScanned().WaitAsync(Wait, Ct);
        Assert.Equal(1L, service.Describe()["Version"]);
        Assert.True((bool)service.Describe()["Dirty"]!);

        var read = await service.GetSnapshotAsync(Ct);
        Assert.Equal(2, read.Snapshot!.Version);
        Assert.False(read.Stale);
        Assert.False(service.GetStatus().Dirty);
        Assert.Equal(1, Builds(service)["Request"]);
    }

    [Fact]
    public async Task DuringAScan_AWarmIndexIsRebuiltOnlyByThePostScanBuild_Once_AndThatOneIsNotBuiltDuringScan()
    {
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        var clock = new ManualTimerProvider();
        using var service = Service(library, clock);
        await service.Rebuild().WaitAsync(Wait, Ct);
        var before = service.Current;

        // The scan reads the libraries: batches, a rebuild and a read are all put off, and the old index answers.
        library.Fake.IsScanRunning = true;
        Assert.Same(Task.CompletedTask, service.OnLibraryChanged());
        Assert.Same(Task.CompletedTask, service.Rebuild());
        var read = await service.GetSnapshotAsync(Ct);
        Assert.Same(before, read.Snapshot);
        Assert.True(read.Stale);
        Assert.False(read.Rebuilding);
        var status = service.GetStatus();
        Assert.True(status.Dirty);
        Assert.False(status.Rebuilding);
        Assert.True(status.ScanInProgress);
        Assert.Equal(3, service.Describe()["DeferredByScan"]);
        Assert.Same(before, service.Current);

        // The post-scan task's full pass: one build, while IsScanRunning is still true, and not marked as during the scan.
        await service.OnLibraryScanned().WaitAsync(Wait, Ct);
        Assert.Equal(2, service.Current!.Version);
        Assert.False(service.Current.BuiltDuringScan);
        Assert.False(service.GetStatus().Dirty);
        Assert.Equal(1, Builds(service)["Scan"]);

        // The scan ends: the waiter finds the index current, and builds nothing more.
        await clock.WaitForArmedTimersAsync();
        library.Fake.IsScanRunning = false;
        clock.Advance(MovieIndexService.ScanPollInterval);
        await Task.Delay(200, Ct);
        Assert.Equal(2, service.Current.Version);
        Assert.Equal(new Dictionary<string, int> { ["Rebuild"] = 1, ["Request"] = 0, ["Changes"] = 0, ["Scan"] = 1, ["Settings"] = 0 }, Builds(service));
    }

    [Fact]
    public async Task ABuildTheScanPutOff_AfterThePostScanBuildStarted_RunsWhenTheScanEnds()
    {
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        var clock = new ManualTimerProvider();
        using var service = Service(library, clock);
        await service.Rebuild().WaitAsync(Wait, Ct);
        library.Fake.IsScanRunning = true;
        await service.OnLibraryScanned().WaitAsync(Wait, Ct);

        // Jellyfin's own post-scan tasks still run (IsScanRunning is true): a rebuild asked for now waits for the scan's end.
        Assert.Same(Task.CompletedTask, service.Rebuild());
        Assert.True(service.GetStatus().Dirty);
        await clock.WaitForArmedTimersAsync();
        clock.Advance(MovieIndexService.ScanPollInterval);
        await Task.Delay(100, Ct);
        Assert.Equal(2, service.Current!.Version);

        await clock.WaitForArmedTimersAsync();
        library.Fake.IsScanRunning = false;
        clock.Advance(MovieIndexService.ScanPollInterval);
        await WaitUntil(() => service.Current!.Version == 3 && !(bool)service.Describe()["Rebuilding"]!);
        Assert.False(service.Current!.BuiltDuringScan);
        Assert.False(service.GetStatus().Dirty);
        Assert.Equal(2, Builds(service)["Rebuild"]);
    }

    [Fact]
    public async Task WhenTheScanEnds_TheBuildPutOffIsForTheStrongestReason()
    {
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        var clock = new ManualTimerProvider();
        using var service = Service(library, clock);
        await service.Rebuild().WaitAsync(Wait, Ct);

        // Changes, then a rebuild, both put off; the warm-up is turned off before the scan ends, and nobody used the page.
        library.Fake.IsScanRunning = true;
        _ = service.OnLibraryChanged();
        _ = service.Rebuild();
        library.Configuration = new PluginConfiguration { IncludedLibraryIds = library.Configuration.IncludedLibraryIds, WarmIndexAfterScan = false };
        await clock.WaitForArmedTimersAsync();
        library.Fake.IsScanRunning = false;
        clock.Advance(MovieIndexService.ScanPollInterval);

        // The rebuild was asked for: it runs whatever the warm rule says.
        await WaitUntil(() => service.Current!.Version == 2 && !(bool)service.Describe()["Rebuilding"]!);
        Assert.Equal(2, Builds(service)["Rebuild"]);
        Assert.Equal(0, Builds(service)["Changes"]);
    }

    [Fact]
    public async Task AColdStartDuringAScan_BuildsOnce_MarkedBuiltDuringScan()
    {
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        library.Fake.IsScanRunning = true;
        using var service = Service(library);

        // No index at all: the first read builds one anyway, and says the scan was running.
        var first = await service.GetSnapshotAsync(Ct);
        Assert.Equal(1, first.Snapshot!.Version);
        Assert.True(first.Snapshot.BuiltDuringScan);
        Assert.False(first.Stale);
        Assert.True(service.GetStatus().BuiltDuringScan);

        // Later reads during the scan are answered from it; a batch is put off for the post-scan build.
        Assert.Same(first.Snapshot, (await service.GetSnapshotAsync(Ct)).Snapshot);
        Assert.Same(Task.CompletedTask, service.OnLibraryChanged());
        Assert.Same(first.Snapshot, (await service.GetSnapshotAsync(Ct)).Snapshot);
        Assert.Equal(1, Builds(service)["Request"]);
        Assert.Equal(2, library.IdQueries.Count);
    }

    [Fact]
    public async Task ARead_WaitsUpToRequestWaitSeconds_ThenAnswersFromTheOldIndex_StaleAndRebuilding_OrWithNone()
    {
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        var clock = new ManualTimerProvider();
        using var service = Service(library, clock);
        using var release = new ManualResetEventSlim(false);
        library.BeforeItemIds = _ => release.Wait(Wait);

        // No index: the read starts a build, waits the default 5 s, then answers with none (the route's 202).
        var cold = service.GetSnapshotAsync(Ct);
        await clock.WaitForArmedTimersAsync();
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(cold.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        var none = await cold.WaitAsync(Wait, Ct);
        Assert.Null(none.Snapshot);
        Assert.True(none.Rebuilding);
        Assert.False(none.Stale);
        release.Set();
        await WaitUntil(() => service.Current is not null && !(bool)service.Describe()["Rebuilding"]!);

        // An out-of-date index and a build that takes longer than RequestWaitSeconds (2): the old index, stale.
        release.Reset();
        library.Configuration = new PluginConfiguration { IncludedLibraryIds = library.Configuration.IncludedLibraryIds, RequestWaitSeconds = 2 };
        var old = service.Current!;
        var queries = library.IdQueries.Count;
        _ = service.OnLibraryChanged();

        // Once the build has started (its start clears Dirty, not the index), a read still waits for it.
        await WaitUntil(() => library.IdQueries.Count > queries);
        Assert.False(service.GetStatus().Dirty);
        var stale = service.GetSnapshotAsync(Ct);
        await clock.WaitForArmedTimersAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        var answer = await stale.WaitAsync(Wait, Ct);
        Assert.Same(old, answer.Snapshot);
        Assert.True(answer.Stale);
        Assert.True(answer.Rebuilding);

        // The build ends within the wait: the new index, current.
        release.Set();
        await WaitUntil(() => service.Current!.Version == 2 && !(bool)service.Describe()["Rebuilding"]!);
        var current = await service.GetSnapshotAsync(Ct);
        Assert.Equal(2, current.Snapshot!.Version);
        Assert.False(current.Stale);
        Assert.False(current.Rebuilding);

        // RequestWaitSeconds 0 answers at once; more than 30 waits 30.
        library.Configuration = new PluginConfiguration { IncludedLibraryIds = library.Configuration.IncludedLibraryIds, RequestWaitSeconds = 0 };
        release.Reset();
        _ = service.OnLibraryChanged();
        var atOnce = await service.GetSnapshotAsync(Ct).WaitAsync(Wait, Ct);
        Assert.True(atOnce.Stale);
        library.Configuration = new PluginConfiguration { IncludedLibraryIds = library.Configuration.IncludedLibraryIds, RequestWaitSeconds = 90 };
        _ = service.OnLibraryChanged();
        var capped = service.GetSnapshotAsync(Ct);
        await clock.WaitForArmedTimersAsync();
        clock.Advance(TimeSpan.FromSeconds(MovieIndexService.MaxRequestWaitSeconds - 1));
        Assert.False(capped.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await capped.WaitAsync(Wait, Ct)).Stale);
        release.Set();
    }

    [Fact]
    public async Task ReadsDuringABuild_WaitForIt_WithNoFollowUp()
    {
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        using var started = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim(false);
        library.BeforeItemIds = q =>
        {
            if (q.TopParentIds.Contains(DuplicatesLibrary.Movies))
            {
                started.Release();
                release.Wait(Wait);
            }
        };
        using var service = Service(library);

        var first = service.GetSnapshotAsync(Ct);
        Assert.True(await started.WaitAsync(Wait, Ct));
        var second = service.GetSnapshotAsync(Ct);
        Assert.False(service.GetStatus().Dirty);
        release.Set();

        Assert.Equal(1, (await first.WaitAsync(Wait, Ct)).Snapshot!.Version);
        Assert.Equal(1, (await second.WaitAsync(Wait, Ct)).Snapshot!.Version);
        await WaitUntil(() => !(bool)service.Describe()["Rebuilding"]!);
        Assert.Equal(1, Builds(service)["Request"]);
        Assert.False(service.GetStatus().Dirty);

        // A current index is answered from memory: no query.
        var queries = library.IdQueries.Count;
        Assert.False((await service.GetSnapshotAsync(Ct)).Stale);
        Assert.Equal(queries, library.IdQueries.Count);
    }

    [Fact]
    public async Task AFailedBuild_LeavesTheIndexOutOfDate()
    {
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        using var service = Service(library);
        await service.Rebuild().WaitAsync(Wait, Ct);

        library.BeforeItemIds = _ => throw new InvalidOperationException(@"The database C:\media\Movies\Heat (1995) is locked.");
        await service.OnLibraryChanged().WaitAsync(Wait, Ct);

        Assert.True(service.GetStatus().Dirty);
        Assert.Equal(1, service.Current!.Version);

        // A fixed sentence: the exception's message (a path, a title) stays in the log.
        Assert.Equal("The movie index couldn't be built (InvalidOperationException); the server's log has the details.", service.GetStatus().LastError);
    }

    [Fact]
    public async Task AFailedFirstBuild_IsAnError_ReadsBackOff_AndARebuildRetriesAtOnce()
    {
        // With no index and a build that fails, a read is an error (the route's 503), never "pending"
        // for ever; for 30 s after a failure a read doesn't start another build; a rebuild does.
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        using var service = Service(library, clock);
        library.BeforeItemIds = _ => throw new InvalidOperationException("The database is locked.");

        var first = await service.GetSnapshotAsync(Ct);
        Assert.True(first.Failed);
        Assert.Null(first.Snapshot);
        Assert.False(first.Rebuilding);
        Assert.StartsWith("The movie index couldn't be built", first.LastError, StringComparison.Ordinal);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, first.LastErrorUtc);
        var queries = library.IdQueries.Count;

        // Polling: no build for 30 s.
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.True((await service.GetSnapshotAsync(Ct)).Failed);
        }

        Assert.Equal(queries, library.IdQueries.Count);
        Assert.Equal(5, service.Describe()["ReadsBackedOff"]);
        Assert.Equal(1, service.Describe()["FailedBuilds"]);

        // A rebuild retries at once (and fails again); 30 s after that failure a read builds again.
        await service.Rebuild().WaitAsync(Wait, Ct);
        Assert.Equal(2, service.Describe()["FailedBuilds"]);
        Assert.True(library.IdQueries.Count > queries);
        clock.Advance(MovieIndexService.FailureBackoff);
        queries = library.IdQueries.Count;
        Assert.True((await service.GetSnapshotAsync(Ct)).Failed);
        Assert.True(library.IdQueries.Count > queries);

        // Fixed: the rebuild clears the error, and a read answers from the index.
        library.BeforeItemIds = null;
        await service.Rebuild().WaitAsync(Wait, Ct);
        var read = await service.GetSnapshotAsync(Ct);
        Assert.False(read.Failed);
        Assert.Equal(1, read.Snapshot!.Stats.Movies);
        Assert.Null(read.LastError);
        Assert.Null(service.GetStatus().LastError);
    }

    [Fact]
    public async Task ChangesWhileDuplicatesIsOff_MarkTheIndexOutOfDate_SoTurningItOnNeverServesTheOldIndex()
    {
        // Duplicates turned off in place (no save event), a movie added, the batch seen while off.
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        using var service = Service(library);
        var consumer = new DuplicatesChangeConsumer(service);
        await service.Rebuild().WaitAsync(Wait, Ct);
        Assert.False((await service.GetSnapshotAsync(Ct)).Stale);

        library.Configuration.DuplicatesEnabled = false;
        var added = library.Add("Heat", 1995, DuplicatesLibrary.MoreMovies);
        await consumer.HandleAsync(Batch(added: [Change(added.Id)]), Ct);
        await consumer.HandleAsync(Batch(fullPass: true), Ct);
        Assert.Equal(1, service.Current!.Version);
        Assert.True(service.GetStatus().Dirty);

        // Turned on again: the first read builds (and waits for it), so it sees the movie added while off.
        library.Configuration.DuplicatesEnabled = true;
        var read = await service.GetSnapshotAsync(Ct);
        Assert.Equal(2, read.Snapshot!.Version);
        Assert.Equal(2, read.Snapshot.Stats.Movies);
        Assert.False(read.Stale);
    }

    [Fact]
    public async Task TurningDuplicatesOn_InTheSettings_MarksTheIndexOutOfDate_AndBuildsIt()
    {
        // The same, the save's side: whatever the consumer saw, a save that turns Duplicates on rebuilds.
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        using var service = Service(library);
        await service.Rebuild().WaitAsync(Wait, Ct);
        var included = library.Configuration.IncludedLibraryIds;

        // Turning it off changes nothing the index reads; the movie added meanwhile goes unseen (no batch reaches it here).
        library.Configuration = new PluginConfiguration { IncludedLibraryIds = included, DuplicatesEnabled = false };
        Assert.False(service.GetStatus().Dirty);
        library.Add("Heat", 1995, DuplicatesLibrary.MoreMovies);
        library.Configuration = new PluginConfiguration { IncludedLibraryIds = included };
        await WaitUntil(() => service.Current?.Version == 2 && !(bool)service.Describe()["Rebuilding"]!);
        Assert.Equal(2, service.Current!.Stats.Movies);
        Assert.Equal(1, Builds(service)["Settings"]);
        Assert.Equal("Settings", service.Describe()["LastBuildReason"]);
    }

    [Fact]
    public async Task ASettingTheIndexReads_MarksItOutOfDate_AndOthersDont()
    {
        // Narrowing the included libraries rebuilds (warm), so More Movies' movie is gone at once.
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        library.Add("Heat", 1995, DuplicatesLibrary.MoreMovies);
        using var service = Service(library);
        await service.Rebuild().WaitAsync(Wait, Ct);
        Assert.Equal(2, service.Current!.Stats.Movies);

        var movies = library.Libraries[0].Id;
        library.Configuration = new PluginConfiguration { IncludedLibraryIds = [movies] };
        await WaitUntil(() => service.Current?.Version == 2 && !(bool)service.Describe()["Rebuilding"]!);
        Assert.Equal([movies], service.Current!.Libraries.Select(l => l.Id));
        Assert.Equal(1, service.Current.Stats.Movies);

        // The same libraries in another order, or a title rule, the mode, the wait: no build (the group results are keyed
        // by the title rules and the mode).
        library.Configuration = new PluginConfiguration { IncludedLibraryIds = [movies, movies], ArticleLanguages = ["En", "De"], DefaultMatchMode = "Title", RequestWaitSeconds = 1 };
        await Task.Delay(100, Ct);
        Assert.Equal(2, service.Current.Version);
        Assert.False(service.GetStatus().Dirty);

        // An excluded folder does; with the warm-up off and the page unused, only marked (the warm service stops listening).
        service.Dispose();
        library.Configuration = new PluginConfiguration { IncludedLibraryIds = [movies], WarmIndexAfterScan = false };
        using var cold = Service(library);
        await cold.Rebuild().WaitAsync(Wait, Ct);
        var queries = library.IdQueries.Count;
        library.Configuration = new PluginConfiguration { IncludedLibraryIds = [movies], WarmIndexAfterScan = false, ExcludedPathPrefixes = [@"C:\media\Movies\Heat (1995)"] };
        Assert.True((bool)cold.Describe()["Dirty"]!);
        await Task.Delay(100, Ct);
        Assert.Equal(1, cold.Current!.Version);
        Assert.Equal(queries, library.IdQueries.Count);
        Assert.Equal(0, (await cold.GetSnapshotAsync(Ct)).Snapshot!.Stats.Movies);
    }

    [Fact]
    public void TheConsumer_TakesMoviesAndVideos_WithTheKitsDefaults()
    {
        var library = new DuplicatesLibrary();
        using var service = Service(library);
        var filter = new DuplicatesChangeConsumer(service).Filter;

        Assert.Equal([BaseItemKind.Movie, BaseItemKind.Video], filter.Kinds!.Order());
        Assert.Equal((TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10)), filter.EffectiveDelays());
        Assert.True(filter.LibraryItemsOnly);
        Assert.False(filter.CaptureIdentity);
        Assert.False(filter.IncludeUserData);
        Assert.Null(filter.Ignore);
    }

    [Fact]
    public async Task TheConsumer_BuildsOnABatchWithChanges_AndOnEveryFullPass_ButNotWithDuplicatesOff()
    {
        var library = new DuplicatesLibrary();
        var movie = library.Add("Heat", 1995);
        using var service = Service(library);
        var consumer = new DuplicatesChangeConsumer(service);

        await consumer.HandleAsync(Batch(), Ct);
        Assert.Null(service.Current);

        await consumer.HandleAsync(Batch(added: [Change(movie.Id)]), Ct);
        await WaitUntil(() => service.Current?.Version == 1 && !(bool)service.Describe()["Rebuilding"]!);
        Assert.Equal(1, Builds(service)["Changes"]);

        // A removed folder takes its movies with it, with no event of their own.
        await consumer.HandleAsync(Batch(removedFolders: [Change(Guid.NewGuid(), BaseItemKind.Folder)]), Ct);
        await WaitUntil(() => service.Current?.Version == 2 && !(bool)service.Describe()["Rebuilding"]!);

        // A full pass, empty or not, is the build after the scan.
        await consumer.HandleAsync(Batch(fullPass: true), Ct);
        await WaitUntil(() => service.Current?.Version == 3 && !(bool)service.Describe()["Rebuilding"]!);
        Assert.Equal(1, Builds(service)["Scan"]);

        // Turned off (in place: no save): nothing is built, but the index is out of date.
        library.Configuration.DuplicatesEnabled = false;
        await consumer.HandleAsync(Batch(added: [Change(movie.Id)]), Ct);
        await consumer.HandleAsync(Batch(fullPass: true), Ct);
        await Task.Delay(100, Ct);
        Assert.Equal(3, service.Current!.Version);
        Assert.True(service.GetStatus().Dirty);
    }

    [Fact]
    public async Task WithTheKitsMonitor_AnAddedMovieBuildsTheIndex_AfterTheQuietPeriod_AndAScanOnceAfterItsFullPass()
    {
        var library = new DuplicatesLibrary();
        var heat = library.Add("Heat", 1995);
        var clock = new ManualTimerProvider();
        using var service = Service(library, clock);
        var consumer = new DuplicatesChangeConsumer(service);
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        using var monitor = new LibraryChangeMonitor(host.Object, library.Fake.Object, null, () => [consumer], LibraryMaintenanceKit.Options, NullLoggerFactory.Instance, clock);
        monitor.Subscribe();

        // A movie file added: nothing for 59 s, then the batch, then the build.
        library.Fake.RaiseAdded(heat);
        clock.Advance(TimeSpan.FromSeconds(59));
        await monitor.DispatchDueAsync(Ct);
        Assert.Null(service.Current);
        clock.Advance(TimeSpan.FromSeconds(1));
        await monitor.DispatchDueAsync(Ct);
        await WaitUntil(() => service.Current?.Version == 1 && !(bool)service.Describe()["Rebuilding"]!);
        Assert.Equal(1, Builds(service)["Changes"]);

        // A scan: its changes wait for its full pass, which builds the index once.
        library.Fake.IsScanRunning = true;
        var blade = library.Add("Blade Runner", 1982);
        library.Fake.RaiseAdded(blade);
        library.Fake.RaiseUpdated(heat);
        clock.Advance(TimeSpan.FromMinutes(11));
        await monitor.DispatchDueAsync(Ct);
        Assert.Equal(1, service.Current!.Version);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        await WaitUntil(() => service.Current?.Version == 2 && !(bool)service.Describe()["Rebuilding"]!);
        Assert.Equal(2, service.Current!.Stats.Movies);
        Assert.False(service.Current.BuiltDuringScan);
        Assert.Equal(new Dictionary<string, int> { ["Rebuild"] = 0, ["Request"] = 0, ["Changes"] = 1, ["Scan"] = 1, ["Settings"] = 0 }, Builds(service));
    }

    private static MovieIndexService Service(DuplicatesLibrary library, TimeProvider? time = null)
        => new(library.Builder(), library.Fake.Object, library.ConfigurationProvider, NullLogger<MovieIndexService>.Instance, time);

    private static Dictionary<string, int> Builds(MovieIndexService service)
        => Assert.IsType<Dictionary<string, int>>(service.Describe()["Builds"]);

    private static ItemChange Change(Guid id, BaseItemKind kind = BaseItemKind.Movie)
        => new() { Kind = LibraryChangeKind.Added, ItemId = id, ItemKind = kind, IsFolder = kind == BaseItemKind.Folder, Utc = DateTimeOffset.UtcNow };

    private static LibraryChangeBatch Batch(IReadOnlyList<ItemChange>? added = null, IReadOnlyList<ItemChange>? removedFolders = null, bool fullPass = false)
        => new(added ?? [], [], [], removedFolders ?? [], [], fullPass, fullPass);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10, Ct);
        }
    }
}
