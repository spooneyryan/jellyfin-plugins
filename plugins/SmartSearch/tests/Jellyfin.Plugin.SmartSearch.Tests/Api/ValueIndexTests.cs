using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using Jellyfin.Plugin.SmartSearch.Search;
using Jellyfin.Plugin.SmartSearch.Tests.Execution;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Api;

/// <summary>
/// What the value index costs, and when: one build per (folder, profile) at a time while other keys build alongside,
/// waits that end with the caller's token, a slot and a timeout that answer <c>Busy</c>, a key that backs off after a
/// timeout, item events that don't touch what it holds ignored, a burst of events rebuilding once after it settles while
/// the stale index is still served, the series picker served from it, and a search that never waits for a build.
/// </summary>
public sealed class ValueIndexTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PlannerWorld World()
    {
        var world = new PlannerWorld();
        world.Core.Library.SetupGet(l => l.IsScanRunning).Returns(false);
        world.Add(BaseItemKind.Movie, "Open", world.Movies, s => { s.Genres = ["Drama"]; s.OfficialRating = "PG"; s.InheritedRating = 10; });
        world.Add(BaseItemKind.Movie, "Other folder", world.Movies, s => { s.Genres = ["Comedy"]; s.TopParentId = world.FolderB; s.OfficialRating = "PG"; s.InheritedRating = 10; });
        var show = world.Add(BaseItemKind.Series, "Heroes", world.Shows, s => { s.Genres = ["Drama"]; s.OfficialRating = "PG"; s.InheritedRating = 10; });
        world.Add(BaseItemKind.Episode, "Pilot", world.Shows, s => { s.Series = show; s.OfficialRating = "PG"; s.InheritedRating = 10; });
        return world;
    }

    private static Task<System.Collections.Generic.IReadOnlyDictionary<string, int>> Genres(ValueIndexService index, PlannerWorld world, CollectionFolder library, CancellationToken? token = null)
        => index.ValuesAsync(world.Kid, "Genres", [library.Id], QueryLimits.ItemKindOrder, token ?? Ct);

    // Holds every build of a folder inside its first query until released.
    private static (ManualResetEventSlim Entered, ManualResetEventSlim Release) Hold(PlannerWorld world, Guid folder)
    {
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        world.Core.OnQuery = q =>
        {
            if (q.TopParentIds.SequenceEqual([folder]) && q.ItemIds.Length == 0)
            {
                entered.Set();
                release.Wait(Wait);
            }
        };
        return (entered, release);
    }

    private static async Task WaitForBuilds(ValueIndexService index, int builds)
    {
        for (var i = 0; i < 1000 && index.Builds < builds; i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal(builds, index.Builds);
    }

    // Waits until no build runs, those nobody waits for included.
    private static async Task WaitForNoRunningBuilds(ValueIndexService index)
    {
        for (var i = 0; i < 1000 && index.RunningBuilds > 0; i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal(0, index.RunningBuilds);
    }

    // Moves the clock on until the task ends (its timer may not exist yet when the test first moves it).
    private static async Task AdvanceUntil(ManualTimeProvider clock, Task task, TimeSpan step)
    {
        for (var i = 0; i < 200 && !task.IsCompleted; i++)
        {
            clock.Advance(step);
            await Task.WhenAny(task, Task.Delay(20, Ct));
        }
    }

    [Fact]
    public async Task OneKeysBuild_DoesntHoldAnothersBack_AndItsSecondCallerWaitsForTheSameBuild()
    {
        var world = World();
        using var index = new ValueIndexService(world.Core.Library.Object, world.Core.Access(), new ManualTimeProvider());
        var (entered, release) = Hold(world, world.FolderA);

        var movies = Genres(index, world, world.Movies);
        Assert.True(entered.Wait(Wait, Ct));
        var again = Genres(index, world, world.Movies);

        // Shows builds while Movies' first folder is held.
        Assert.Equal(1, (await Genres(index, world, world.Shows).WaitAsync(Wait, Ct))["Drama"]);
        Assert.False(movies.IsCompleted);

        release.Set();
        Assert.Equal((await movies.WaitAsync(Wait, Ct)).OrderBy(v => v.Key), (await again.WaitAsync(Wait, Ct)).OrderBy(v => v.Key));
        Assert.Equal(3, index.Builds); // Movies' two folders once each, and Shows'.
    }

    [Fact]
    public async Task ACallerWhoGivesUp_StopsWaiting_AndTheBuildIsKeptForTheNext()
    {
        var world = World();
        using var index = new ValueIndexService(world.Core.Library.Object, world.Core.Access(), new ManualTimeProvider());
        var (entered, release) = Hold(world, world.FolderA);
        using var gone = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var waiting = Genres(index, world, world.Movies, gone.Token);
        Assert.True(entered.Wait(Wait, Ct));
        await gone.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Wait, Ct));

        release.Set();
        world.Core.OnQuery = null;
        for (var i = 0; i < 500 && index.Builds < 2; i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal(2, index.Builds);
        Assert.Equal(2, (await Genres(index, world, world.Movies)).Count);
        Assert.Equal(2, index.Builds);
    }

    [Fact]
    public async Task ABuildThatWaitsTooLongForASlot_IsBusy()
    {
        var world = World();
        var clock = new ManualTimeProvider();
        using var index = new ValueIndexService(world.Core.Library.Object, world.Core.Access(), clock, buildSlots: 1, slotWait: TimeSpan.FromSeconds(10), buildTimeout: TimeSpan.FromHours(1));
        var (entered, release) = Hold(world, world.Shows.PhysicalFolderIds[0]);

        var shows = Genres(index, world, world.Shows);
        Assert.True(entered.Wait(Wait, Ct));
        var home = Genres(index, world, world.Home);
        await AdvanceUntil(clock, home, TimeSpan.FromSeconds(5));

        var busy = await Assert.ThrowsAsync<SmartSearchRefusedException>(() => home);
        Assert.Equal(SmartSearchRefusedException.BusyCode, busy.Code);

        release.Set();
        Assert.Equal(1, (await shows.WaitAsync(Wait, Ct))["Drama"]);
        Assert.Empty(await Genres(index, world, world.Home));
    }

    [Fact]
    public async Task ABuildPastItsTimeout_IsBusy_On503_AndACallAfterTheBackOffBuildsAgain()
    {
        var world = World();
        await using var host = await new SearchApiHost(world).StartAsync();
        var clock = host.Clock;
        world.Core.OnQuery = q =>
        {
            if (q.ItemIds.Length == 0 && q.IncludeItemTypes.Length == ValueIndexService.Kinds.Count)
            {
                clock.Advance(ValueIndexService.DefaultBuildTimeout);
            }
        };

        var (status, problem, text) = await host.SendAsync("GET", "/SmartSearch/Values/Genres", host.Kid);
        Assert.True(status == HttpStatusCode.ServiceUnavailable, text);
        Assert.Equal(SmartSearchRefusedException.BusyCode, problem.GetProperty("code").GetString());
        Assert.Equal(SmartSearchRefusedException.IndexBusyMessage, problem.GetProperty("detail").GetString());
        Assert.True((await host.SendAsync("GET", "/SmartSearch/Series?prefix=he", host.Kid)).Status == HttpStatusCode.ServiceUnavailable);

        // The picker's 503 can leave a build behind: one of a library's folders backs off, so it answers Busy at once,
        // after it started the build of another whose slot wait had timed out. That build still moves the clock and backs
        // its key off from then; let it end before the back-off is waited out.
        await WaitForNoRunningBuilds(host.Values);
        world.Core.OnQuery = null;
        clock.Advance(ValueIndexService.BuildBackOff);
        Assert.Equal(2, (await host.OkAsync("GET", "/SmartSearch/Values/Genres", host.Kid)).GetArrayLength());
    }

    [Fact]
    public async Task AfterABuildRanPastItsTimeout_ItsKeyIsntBuiltAgainForTheBackOff()
    {
        var world = World();
        var clock = new ManualTimeProvider();
        using var index = new ValueIndexService(world.Core.Library.Object, world.Core.Access(), clock, buildSlots: 4);
        var folder = world.Shows.PhysicalFolderIds[0];
        int BuildQueries() => world.Core.Queries.Count(q => q.TopParentIds.SequenceEqual([folder]) && q.IncludeItemTypes.Length == ValueIndexService.Kinds.Count);
        world.Core.OnQuery = q =>
        {
            if (q.TopParentIds.SequenceEqual([folder]) && q.ItemIds.Length == 0)
            {
                clock.Advance(ValueIndexService.DefaultBuildTimeout);
            }
        };

        var busy = await Assert.ThrowsAsync<SmartSearchRefusedException>(() => Genres(index, world, world.Shows));
        Assert.Equal(SmartSearchRefusedException.BusyCode, busy.Code);
        world.Core.OnQuery = null;
        var queries = BuildQueries();

        // Within the back-off: Busy at once, nothing read.
        clock.Advance(ValueIndexService.BuildBackOff - TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<SmartSearchRefusedException>(() => Genres(index, world, world.Shows));
        Assert.Equal(queries, BuildQueries());
        Assert.Equal(0, index.Builds);

        // After it: built again.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, (await Genres(index, world, world.Shows))["Drama"]);
        Assert.Equal(1, index.Builds);
    }

    [Fact]
    public async Task AStaleIndex_IsServedWhileItsOneRebuildRuns()
    {
        var world = World();
        var clock = new ManualTimeProvider();
        using var index = new ValueIndexService(world.Core.Library.Object, world.Core.Access(), clock);
        Assert.Equal(1, (await Genres(index, world, world.Shows))["Drama"]);
        Assert.Equal(1, index.Builds);

        // An episode changes, the debounce passes: the next callers get the stale index at once while one rebuild runs.
        var (entered, release) = Hold(world, world.Shows.PhysicalFolderIds[0]);
        int ShowsBuildQueries() => world.Core.Queries.Count(q => q.TopParentIds.SequenceEqual(world.Shows.PhysicalFolderIds) && q.ItemIds.Length == 0);
        var before = ShowsBuildQueries();
        world.Core.Library.Raise(l => l.ItemUpdated += null, world.Core.Library.Object, new ItemChangeEventArgs { Item = new MediaBrowser.Controller.Entities.TV.Episode(), UpdateReason = ItemUpdateType.MetadataEdit });
        clock.Advance(ValueIndexService.Debounce);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(1, (await Genres(index, world, world.Shows).WaitAsync(Wait, Ct))["Drama"]);
        }

        Assert.True(entered.Wait(Wait, Ct));
        Assert.Equal(before + 1, ShowsBuildQueries());
        release.Set();
        await WaitForBuilds(index, 2);
    }

    [Fact]
    public async Task ASeriesNameSearch_DuringAnIndexBuild_AnswersFromItsOwnReadWithoutWaiting()
    {
        var world = new PlannerWorld();
        world.Core.Library.SetupGet(l => l.IsScanRunning).Returns(false);
        var open = world.Add(BaseItemKind.Series, "Blue Skies", world.Shows, s => { s.OfficialRating = "PG"; s.InheritedRating = 10; });
        var sunny = world.Add(BaseItemKind.Episode, "Sunny", world.Shows, s => { s.Series = open; s.SeriesName = "Blue Skies"; s.OfficialRating = "PG"; s.InheritedRating = 10; });
        using var release = new ManualResetEventSlim();
        var held = 0;
        world.Core.OnQuery = q =>
        {
            if (q.ItemIds.Length == 0 && q.IncludeItemTypes.Length == ValueIndexService.Kinds.Count)
            {
                Interlocked.Increment(ref held);
                release.Wait(Wait);
            }
        };
        await using var host = await new SearchApiHost(world).StartAsync();
        var query = QueryPlannerTests.Q("{\"Field\":\"SeriesName\",\"Op\":\"Contains\",\"Value\":\"blue\"}", "\"Episode\"");

        // The search starts the index builds in the background and plans from its own read of the episodes.
        var ids = await host.SearchAllAsync(query, host.Kid).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal([sunny.Id], ids);
        for (var i = 0; i < 500 && Volatile.Read(ref held) == 0; i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(Volatile.Read(ref held) > 0);
        Assert.Equal(0, host.Values.Builds);

        // Once built, the next search plans from the index.
        release.Set();
        for (var i = 0; i < 500 && host.Values.Builds == 0; i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(host.Values.Builds > 0);
        Assert.Equal([sunny.Id], await host.SearchAllAsync(query, host.Kid));
    }

    [Fact]
    public void ItemEvents_ThatCantChangeTheIndex_AreIgnored()
    {
        static ItemChangeEventArgs Event(BaseItem? item, ItemUpdateType reason = ItemUpdateType.MetadataEdit) => new() { Item = item!, UpdateReason = reason };

        Assert.True(ValueIndexService.Matters(Event(new Movie()), update: true));
        Assert.True(ValueIndexService.Matters(Event(new Episode(), ItemUpdateType.ImageUpdate | ItemUpdateType.MetadataImport), update: true));
        Assert.True(ValueIndexService.Matters(Event(new Series()), update: false));
        Assert.True(ValueIndexService.Matters(Event(null), update: false));

        // People, seasons, folders and box sets; an update of images alone, which is also how chapter images are saved.
        Assert.False(ValueIndexService.Matters(Event(new Person()), update: true));
        Assert.False(ValueIndexService.Matters(Event(new Person()), update: false));
        Assert.False(ValueIndexService.Matters(Event(new Season()), update: true));
        Assert.False(ValueIndexService.Matters(Event(new Folder()), update: false));
        Assert.False(ValueIndexService.Matters(Event(new BoxSet()), update: true));
        Assert.False(ValueIndexService.Matters(Event(new Movie(), ItemUpdateType.ImageUpdate), update: true));
    }

    [Fact]
    public async Task IgnoredEvents_RebuildNothing_AndABurstRebuildsOnceAfterItSettles()
    {
        var world = World();
        var clock = new ManualTimeProvider();
        using var index = new ValueIndexService(world.Core.Library.Object, world.Core.Access(), clock);
        void Raise(BaseItem item, ItemUpdateType reason = ItemUpdateType.MetadataEdit)
            => world.Core.Library.Raise(l => l.ItemUpdated += null, world.Core.Library.Object, new ItemChangeEventArgs { Item = item, UpdateReason = reason });

        await Genres(index, world, world.Shows);
        Assert.Equal(1, index.Builds);

        Raise(new Person());
        Raise(new Season());
        Raise(new Movie(), ItemUpdateType.ImageUpdate);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Genres(index, world, world.Shows);
        Assert.Equal(1, index.Builds);

        // A burst: an event a second keeps the last index served (the events are still coming)...
        for (var i = 0; i < 5; i++)
        {
            Raise(new Episode());
            clock.Advance(TimeSpan.FromSeconds(1));
            await Genres(index, world, world.Shows);
            Assert.Equal(1, index.Builds);
        }

        // ...until none came for the debounce: one rebuild (the stale index served meanwhile), then the new one is served.
        clock.Advance(ValueIndexService.Debounce);
        await Genres(index, world, world.Shows);
        await WaitForBuilds(index, 2);
        await Genres(index, world, world.Shows);
        Assert.Equal(2, index.Builds);

        // A burst that never settles is held back at most MaxDebounce.
        for (var elapsed = TimeSpan.Zero; elapsed < ValueIndexService.MaxDebounce; elapsed += TimeSpan.FromSeconds(1))
        {
            Raise(new Episode());
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Raise(new Episode());
        await Genres(index, world, world.Shows);
        await WaitForBuilds(index, 3);
    }

    [Fact]
    public async Task TheSeriesPicker_ReadsNoLibraryPerKeystroke()
    {
        var world = World();
        await using var host = await new SearchApiHost(world).StartAsync();

        Assert.Single((await host.OkAsync("GET", "/SmartSearch/Series?prefix=he", host.Kid)).EnumerateArray());
        var builds = host.Values.Builds;
        var queries = world.Core.Queries.Count;

        foreach (var text in new[] { "her", "hero", "heroe", "heroes" })
        {
            Assert.Single((await host.OkAsync("GET", "/SmartSearch/Series?prefix=" + text, host.Kid)).EnumerateArray());
        }

        Assert.Equal(builds, host.Values.Builds);
        Assert.Equal(queries, world.Core.Queries.Count);

        // The twin shares Kid's profile: only folders Kid can't open are new to it.
        await host.OkAsync("GET", "/SmartSearch/Series?prefix=he", host.Twin);
        Assert.Equal(builds + 1, host.Values.Builds); // Private's folder; Hidden and Hidden Shows are over Kid's folders.
    }
}
