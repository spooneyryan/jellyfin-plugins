using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Duplicates;

/// <summary>
/// The index service: the status before and after a build, single-flight with one follow-up, a
/// failed build that keeps the index before it, the Diagnostics entry (the build's counts and cost) and the settings' switch.
/// </summary>
public sealed class MovieIndexServiceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    [Fact]
    public void BeforeTheFirstBuild_TheStatusIsEmpty()
    {
        var library = new DuplicatesLibrary();
        using var service = Service(library);

        var status = service.GetStatus();

        Assert.Equal(0, status.Version);
        Assert.Null(status.GeneratedUtc);
        Assert.Null(status.BuildMs);
        Assert.False(status.Rebuilding);
        Assert.False(status.Dirty);
        Assert.Null(status.Progress);
        Assert.Empty(status.Libraries);
        Assert.Null(service.Current);
        Assert.Empty(library.IdQueries);
    }

    [Fact]
    public async Task ARebuild_GivesTheIndexAndItsCounts()
    {
        var library = new DuplicatesLibrary();
        var a = library.Add("Blade Runner", 1982);
        var b = library.Add("Blade Runner", 1982, file: "b.mkv");
        library.Add("Heat", 1995, DuplicatesLibrary.MoreMovies);
        DuplicatesLibrary.MergeVersions(a, b);
        library.Fake.IsScanRunning = true;
        using var service = Service(library);

        await service.Rebuild().WaitAsync(Wait, TestContext.Current.CancellationToken);
        var status = service.GetStatus();

        Assert.Equal(1, status.Version);
        Assert.NotNull(status.GeneratedUtc);
        Assert.NotNull(status.BuildMs);
        Assert.True(status.BuiltDuringScan);
        Assert.True(status.ScanInProgress);
        Assert.Equal(3, status.Movies);
        Assert.Equal(2, status.Entries);
        Assert.Equal(1, status.MergedEntries);
        Assert.Equal(0, status.BrokenEntries);
        Assert.Equal(new[] { ("Movies", "movies", 2), ("More Movies", "mixed", 1) }, status.Libraries.Select(l => (l.Name, l.Kind, l.Movies)));
        Assert.False(status.Rebuilding);
        Assert.Null(status.Progress);
        Assert.Null(status.LastError);

        // Every member id finds its entry.
        var snapshot = service.Current!;
        Assert.Same(snapshot.ById[a.Id], snapshot.ById[b.Id]);
        Assert.Equal(2, snapshot.Metrics.LibraryQueries);
        Assert.Equal(1, snapshot.Metrics.ItemQueries);
        Assert.True(snapshot.Metrics.AllocatedBytes > 0);
        Assert.True(snapshot.Metrics.PeakWorkingSetBytes >= snapshot.Metrics.WorkingSetBytes);
    }

    [Fact]
    public async Task Rebuilds_AreSingleFlight_WithOneFollowUp()
    {
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        using var started = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim(false);
        var builds = 0;
        library.BeforeItemIds = q =>
        {
            if (q.TopParentIds.Contains(DuplicatesLibrary.Movies))
            {
                Interlocked.Increment(ref builds);
                started.Release();
                release.Wait(Wait);
            }
        };
        using var service = Service(library);

        var first = service.Rebuild();
        Assert.True(await started.WaitAsync(Wait, TestContext.Current.CancellationToken));
        var status = service.GetStatus();
        Assert.True(status.Rebuilding);
        Assert.False(status.Dirty);
        Assert.NotNull(status.Progress);

        // Three more requests while it runs: the same task, one follow-up.
        Assert.Same(first, service.Rebuild());
        Assert.True(service.RequestRebuild().Dirty);
        Assert.Same(first, service.Rebuild());

        release.Set();
        await first.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal(2, builds);
        Assert.Equal(2, service.GetStatus().Version);
        Assert.False(service.GetStatus().Dirty);
        Assert.False(service.GetStatus().Rebuilding);

        // Idle again: the next request starts a new build.
        var next = service.Rebuild();
        Assert.NotSame(first, next);
        await next.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal(3, service.GetStatus().Version);
    }

    [Fact]
    public async Task AFailedBuild_KeepsTheIndexBeforeIt_AndSaysWhy()
    {
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        using var service = Service(library);
        await service.Rebuild().WaitAsync(Wait, TestContext.Current.CancellationToken);
        var before = service.Current;

        library.BeforeItemIds = _ => throw new InvalidOperationException("The database is locked.");
        await service.Rebuild().WaitAsync(Wait, TestContext.Current.CancellationToken);

        var status = service.GetStatus();
        Assert.Same(before, service.Current);
        Assert.Equal(1, status.Version);
        Assert.Equal("The movie index couldn't be built (InvalidOperationException); the server's log has the details.", status.LastError);
        Assert.NotNull(status.LastErrorUtc);
        Assert.False(status.Rebuilding);
        Assert.Equal(1, service.Describe()["FailedBuilds"]);

        library.BeforeItemIds = null;
        await service.Rebuild().WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal(2, service.GetStatus().Version);
        Assert.Null(service.GetStatus().LastError);
    }

    [Fact]
    public async Task Diagnostics_ShowTheLastBuild()
    {
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        using var service = Service(library);

        Assert.Equal(0L, service.Describe()["Version"]);
        Assert.Null(service.Describe()["BuildMs"]);

        await service.Rebuild().WaitAsync(Wait, TestContext.Current.CancellationToken);
        var described = service.Describe();

        Assert.Equal(1L, described["Version"]);
        Assert.Equal(1, described["Movies"]);
        Assert.Equal(1, described["Entries"]);
        Assert.Equal(2, described["LibraryQueries"]);
        Assert.Equal(1, described["ItemQueries"]);
        Assert.IsType<double>(described["AllocatedMB"]);
        Assert.IsType<double>(described["PeakWorkingSetMB"]);
        Assert.NotNull(described["BuildMs"]);
        Assert.Equal(0, described["FailedBuilds"]);
    }

    [Fact]
    public void IsEnabled_FollowsTheSettings()
    {
        var library = new DuplicatesLibrary();
        using var service = Service(library);

        Assert.True(service.IsEnabled);
        library.Configuration = new PluginConfiguration { DuplicatesEnabled = false };
        Assert.False(service.IsEnabled);
    }

    [Fact]
    public async Task AfterDispose_NoBuildStarts()
    {
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        var service = Service(library);
        service.Dispose();

        await service.Rebuild().WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Null(service.Current);
        Assert.Empty(library.IdQueries);
    }

    private static MovieIndexService Service(DuplicatesLibrary library)
        => new(library.Builder(), library.Fake.Object, library.ConfigurationProvider, NullLogger<MovieIndexService>.Instance);
}
