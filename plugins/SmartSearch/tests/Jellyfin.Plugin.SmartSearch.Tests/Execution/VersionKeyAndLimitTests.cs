using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Execution;

/// <summary>
/// Merged versions whose members carry more than one presentation key (core's chains, items a primary lists), and the
/// limits around an evaluation: the cache's generations and id budget, a user's one slot, a <c>Library</c> rule's set and
/// a <c>Series</c> rule's ids in the caller's scope, and <c>Explain</c>.
/// </summary>
public sealed class VersionKeyAndLimitTests
{
    private const string Exact = """{"Field":"Year","Op":"Eq","Value":2000}""";
    private const string InMemory = """{"Field":"Overview","Op":"Contains","Value":"keep"}""";

    private static string Q(string rules, string kinds = "\"Movie\"", string extra = "") => QueryPlannerTests.Q(rules, kinds, extra: extra);

    private static FakeRow Movie(PlannerWorld world, string name, CollectionFolder? library = null, Action<RowSpec>? set = null)
        => world.Add(BaseItemKind.Movie, name, library ?? world.Movies, s =>
        {
            s.Year = 2000;
            s.Overview = "keep";
            s.OfficialRating = "PG";
            s.InheritedRating = 10;
            set?.Invoke(s);
        });

    // ------------------------------------------------------------------------------------------- presentation keys

    // core's "Merge versions" of B and C, then of A and B: C still points at B and is keyed by it, so /Items shows two rows
    // (A's key, and C). Both paths show those two, once each, whatever the page size; the auto-collections (no user) keep
    // the whole set as one result.
    [Fact]
    public async Task AChainedSet_ShowsEachPresentationKeyOnce_OnBothPaths()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        var a = Movie(world, "Heat");
        var b = Movie(world, "Heat cut");
        var c = Movie(world, "Heat director");
        world.Core.MergeVersions(b, c);
        world.Core.MergeVersions(a, b);
        Assert.Equal(b.Id.ToString("N"), c.PresentationUniqueKey);

        foreach (var user in new[] { world.Admin, world.Kid })
        {
            foreach (var (rules, path) in new[] { (Exact, QueryPlanPath.Fast), (Exact + "," + InMemory, QueryPlanPath.Residual) })
            {
                foreach (var pageSize in new[] { 1, 50 })
                {
                    harness.Cache.InvalidateAll();
                    var run = await harness.SearchAll(harness.Read(Q(rules)), user, pageSize);
                    Assert.Equal(path, run.Path);
                    Assert.Equal(2, run.Total);
                    Assert.Equal([a.Id, c.Id], run.Ids);
                }
            }
        }

        var whole = await harness.Engine.EvaluateAllAsync(harness.Read(Q(InMemory)), null, EvaluationPurpose.AutoCollection, TestContext.Current.CancellationToken);
        Assert.Equal([a.Id], whole.OrderedIds);
    }

    // A standalone item a primary lists joins its set but keeps its own key: /Items shows it as its own row.
    [Fact]
    public async Task AnItemAPrimaryLists_KeepsItsOwnResult_WhateverThePageSize()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        var a = Movie(world, "Heat");
        var t = Movie(world, "Heat extended");
        world.Core.ListVersion(a, t);

        foreach (var (rules, path) in new[] { (Exact, QueryPlanPath.Fast), (InMemory, QueryPlanPath.Residual) })
        {
            foreach (var pageSize in new[] { 1, 50 })
            {
                harness.Cache.InvalidateAll();
                var run = await harness.SearchAll(harness.Read(Q(rules)), world.Admin, pageSize);
                Assert.Equal(path, run.Path);
                Assert.Equal(2, run.Total);
                Assert.Equal([a.Id, t.Id], run.Ids);
            }
        }
    }

    // A primary Kid can't see (in Private) points one item at itself and lists another: Kid's /Items shows both, and so
    // does the search, on both paths.
    [Fact]
    public async Task APrimaryTheCallerCantSee_DoesNotMergeTwoItemsTheyCanSee()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        var hidden = Movie(world, "Ronin", world.Private);
        var x = Movie(world, "Ronin A");
        var y = Movie(world, "Ronin B");
        world.Core.MergeVersions(hidden, x);
        world.Core.ListVersion(hidden, y);

        foreach (var (rules, path) in new[] { (Exact, QueryPlanPath.Fast), (InMemory, QueryPlanPath.Residual) })
        {
            harness.Cache.InvalidateAll();
            var run = await harness.SearchAll(harness.Read(Q(rules)), world.Kid, pageSize: 10);
            Assert.Equal(path, run.Path);
            Assert.Equal(2, run.Total);
            Assert.Equal([x.Id, y.Id], run.Ids);
        }
    }

    // A merged pair as core's "Merge versions" makes it from a 1080p film with a 720p local alternate (an owned row) and a
    // 4K film: the film with more than one source becomes the primary. /Items for a user keeps whichever row of the key SQL
    // meets first, here the 4K secondary, so its row needn't be the primary. The search shows the primary for the pair
    // whichever version matched, one row on both paths (Resolution is re-checked, so Residual; Year is Fast), and the
    // auto-collections (whole sets, with or without an evaluation user) hold the primary too; the local alternate never
    // matches.
    [Fact]
    public async Task AMergedPair_ShowsItsPrimary_WhicheverVersionMatches_WhereItemsMayShowTheSecondary()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        var uhd = Movie(world, "Merged Four UHD", set: s => { s.Width = 3840; s.Year = 2001; });
        var film = Movie(world, "Merged Four", set: s => s.Width = 1920);
        world.Add(BaseItemKind.Video, "Merged Four - 720p", world.Movies, s => { s.Owner = film; s.Width = 1280; s.Year = 2002; s.Overview = "keep"; });
        world.Core.MergeVersions(film, uhd);

        Assert.Equal([uhd.Id], world.Core.Run(new InternalItemsQuery(world.Admin) { IncludeItemTypes = [BaseItemKind.Movie] }).Select(r => r.Id));

        static string Resolution(string value) => $$"""{"Field":"Resolution","Op":"AnyOf","Values":["{{value}}"]}""";
        static string Year(int value) => $$"""{"Field":"Year","Op":"Eq","Value":{{value}}}""";
        var cases = new[]
        {
            (Resolution("2160p"), QueryPlanPath.Residual, new[] { film.Id }),
            (Resolution("1080p"), QueryPlanPath.Residual, new[] { film.Id }),
            (Resolution("720p"), QueryPlanPath.Residual, Array.Empty<Guid>()),
            (Year(2001), QueryPlanPath.Fast, new[] { film.Id }),
            (Year(2000), QueryPlanPath.Fast, new[] { film.Id }),
            (Year(2002), QueryPlanPath.Fast, Array.Empty<Guid>()),
            (Year(2001) + "," + InMemory, QueryPlanPath.Residual, new[] { film.Id }),
        };
        foreach (var (rules, path, expected) in cases)
        {
            foreach (var user in new[] { world.Admin, world.Kid })
            {
                harness.Cache.InvalidateAll();
                var run = await harness.SearchAll(harness.Read(Q(rules)), user, pageSize: 10);
                Assert.Equal(path, run.Path);
                Assert.Equal(expected, run.Ids);
            }

            foreach (var evaluationUser in new[] { null, world.Admin })
            {
                var collection = await harness.Engine.EvaluateAllAsync(harness.Read(Q(rules)), new EvaluationContext(EvaluationPurpose.AutoCollection, evaluationUser), TestContext.Current.CancellationToken);
                Assert.Equal(expected, collection.OrderedIds);
            }
        }
    }

    // ------------------------------------------------------------------------------------------- the result cache

    // An item added while a search is evaluated: the entry carries the generation read before the evaluation, so the
    // event ends it and the same search evaluates again.
    [Fact]
    public async Task AnItemAddedDuringTheEvaluation_EndsTheResultEvaluatedBeforeIt()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        Movie(world, "Film 1");
        var query = harness.Read(Q(InMemory));
        FakeRow? added = null;
        world.Core.OnQuery = q =>
        {
            if (q.ItemIds.Length > 0 && added is null)
            {
                added = Movie(world, "Film 2");
                harness.Cache.InvalidateAll(); // what the ItemAdded event does
            }
        };

        var first = await harness.Search(query, world.Admin);
        world.Core.OnQuery = null;
        var again = await harness.Search(query, world.Admin);
        var next = await harness.Search(query, world.Admin, token: first.ResultToken);

        Assert.Single(first.Items);
        Assert.Equal(2, again.Items.Count);
        Assert.NotEqual(first.ResultToken, again.ResultToken);
        Assert.Equal([SmartSearchEngine.ReevaluatedWarning], next.Warnings);
    }

    // The ids all entries hold stay within the budget (the least recently used go first), and expired entries go on the
    // timer even when nothing reads the cache.
    [Fact]
    public void TheCache_KeepsItsIdsWithinTheBudget_AndSweepsExpiredEntries()
    {
        var clock = new ManualTimeProvider();
        using var cache = new ResultCache(null, null, null, clock, () => TimeSpan.FromSeconds(10), () => 250_000);
        static CachedResult Entry(Guid user, string hash, int ids) => new()
        {
            UserId = user,
            QueryHash = hash,
            Bucket = 0,
            Fingerprint = "f",
            Path = QueryPlanPath.Residual,
            NowUtc = DateTime.UnixEpoch,
            LibraryIds = [],
            OrderedIds = new Guid[ids],
            Count = ids
        };

        var u = Guid.NewGuid();
        var v = Guid.NewGuid();
        var first = cache.Store(Entry(u, "first", 100_000));
        var second = cache.Store(Entry(v, "second", 100_000));
        Assert.NotNull(cache.TryGet(first, u, "first", "f"));
        cache.Store(Entry(Guid.NewGuid(), "third", 100_000));

        Assert.Equal(2, cache.Count);
        Assert.Equal(200_000, cache.IdCount);
        Assert.NotNull(cache.TryGet(first, u, "first", "f"));
        Assert.Null(cache.TryGet(second, v, "second", "f"));

        clock.Advance(ResultCache.SweepInterval + TimeSpan.FromSeconds(1));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.IdCount);

        // The timer goes on: an entry stored later is swept on the next pass.
        cache.Store(Entry(u, "fourth", 10));
        clock.Advance(ResultCache.SweepInterval + TimeSpan.FromSeconds(1));
        Assert.Equal(0, cache.Count);
    }

    // ------------------------------------------------------------------------------------------- limits

    // A Library rule's set is loaded with the caller's access: items above Kid's parental limit neither count against
    // MaxCandidates nor appear in a refusal's count.
    [Fact]
    public async Task ALibraryRulesSet_CountsOnlyWhatTheCallerCanSee()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        Movie(world, "Visible 1", set: s => s.Overview = "other");
        Movie(world, "Visible 2", set: s => s.Overview = "other");
        for (var i = 0; i < 4; i++)
        {
            Movie(world, "R " + i, set: s =>
            {
                s.Overview = "other";
                s.OfficialRating = "R";
                s.InheritedRating = 17;
            });
        }

        var query = harness.Read(Q("{\"Match\":\"Any\",\"Children\":[{\"Field\":\"Library\",\"Op\":\"AnyOf\",\"Values\":[\"" + world.Movies.Id + "\"]}," + InMemory + "]}"));
        harness.Settings = new EngineSettings(MaxCandidates: 5);
        Assert.Equal(2, (await harness.Search(query, world.Kid)).Items.Count);

        harness.Cache.InvalidateAll();
        harness.Settings = new EngineSettings(MaxCandidates: 3);
        var refused = await Assert.ThrowsAsync<SmartSearchRefusedException>(() => harness.Search(query, world.Kid));
        Assert.Equal(SmartSearchRefusedException.QueryTooBroadCode, refused.Code);
        Assert.Equal(4, refused.CandidateCount); // the two Kid can see, as the set and as candidates
    }

    // A user runs one search at a time: their second waits for their own slot and is refused Busy, while another user
    // still gets the free slot.
    [Fact]
    public async Task OneUser_HoldsOneSlot_AnotherUserStillSearches()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world, slots: 2, slotWait: TimeSpan.FromMilliseconds(100));
        Movie(world, "Film");
        var slow = harness.Read(Q(InMemory));
        var quick = harness.Read(Q(InMemory, extra: ",\"Limit\":5"));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var block = 1;
        world.Core.OnQuery = q =>
        {
            if (Interlocked.Exchange(ref block, 0) == 1)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        };

        var first = Task.Run(() => harness.Search(slow, world.Admin), TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        var busy = await Assert.ThrowsAsync<SmartSearchRefusedException>(() => harness.Search(quick, world.Admin));
        Assert.Equal(SmartSearchRefusedException.BusyCode, busy.Code);
        Assert.Equal(1, harness.Slots.FreeSearchSlots);
        Assert.Single((await harness.Search(slow, world.Kid)).Items);

        release.Set();
        Assert.Single((await first).Items);
        Assert.Equal(2, harness.Slots.FreeSearchSlots);
        Assert.Equal(0, harness.Slots.UsersWithSlots);
        Assert.Single((await harness.Search(quick, world.Admin)).Items);
    }

    // A Series rule's ids are resolved in the caller's scope: an item in a library Kid can't open and an id of nothing are
    // treated alike (the rule stays in memory), so the plan, the candidate count and a refusal tell nothing apart.
    [Fact]
    public async Task ASeriesRule_TreatsAHiddenItemAndAnUnknownId_Alike()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        var secret = Movie(world, "Secret", world.Private);
        for (var i = 0; i < 3; i++)
        {
            world.Add(BaseItemKind.Episode, "Ep " + i, world.Shows, s => s.Year = 2000);
        }

        string Rule(Guid id) => "{\"Field\":\"Series\",\"Op\":\"AnyOf\",\"Values\":[{\"Id\":\"" + id.ToString("N") + "\",\"Name\":\"x\"}]}";
        var hidden = await harness.Search(harness.Read(Q(Rule(secret.Id), "\"Episode\"")), world.Kid);
        var none = await harness.Search(harness.Read(Q(Rule(Guid.NewGuid()), "\"Episode\"")), world.Kid);

        Assert.Equal(QueryPlanPath.Residual, hidden.Path);
        Assert.Equal(QueryPlanPath.Residual, none.Path);
        Assert.Equal(3, hidden.CandidateCount);
        Assert.Equal(3, none.CandidateCount);
        Assert.Empty(hidden.Items);
        Assert.Empty(none.Items);

        harness.Settings = new EngineSettings(MaxCandidates: 2);
        harness.Cache.InvalidateAll();
        foreach (var id in new[] { secret.Id, Guid.NewGuid() })
        {
            var refused = await Assert.ThrowsAsync<SmartSearchRefusedException>(() => harness.Search(harness.Read(Q(Rule(id), "\"Episode\"")), world.Kid));
            Assert.Equal(SmartSearchRefusedException.QueryTooBroadCode, refused.Code);
            Assert.Equal(3, refused.CandidateCount);
        }
    }

    // Explain plans, which reads the database: it takes a slot, the candidate budget and the timeout.
    [Fact]
    public async Task Explain_TakesASlot_TheBudget_AndTheTimeout()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world, slots: 1, slotWait: TimeSpan.FromMilliseconds(100));
        for (var i = 0; i < 3; i++)
        {
            Movie(world, "Film " + i);
        }

        // Two Library rules on other libraries: two sets, each loaded after a check of the token.
        string LibraryRule(Guid id) => "{\"Field\":\"Library\",\"Op\":\"AnyOf\",\"Values\":[\"" + id + "\"]}";
        var library = harness.Read(Q("{\"Match\":\"Any\",\"Children\":[" + LibraryRule(world.Movies.Id) + "," + LibraryRule(world.Private.Id) + "," + InMemory + "]}"));
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(QueryPlanPath.Residual, (await harness.Engine.ExplainAsync(library, world.Admin, PlanPurpose.Search, ct)).Path);

        harness.Settings = new EngineSettings(MaxCandidates: 2);
        var tooBroad = await Assert.ThrowsAsync<SmartSearchRefusedException>(() => harness.Engine.ExplainAsync(library, world.Admin, PlanPurpose.Search, ct));
        Assert.Equal(SmartSearchRefusedException.QueryTooBroadCode, tooBroad.Code);

        harness.Settings = new EngineSettings(SearchTimeoutSeconds: 5);
        world.Core.OnQuery = _ => harness.Clock.Advance(TimeSpan.FromSeconds(6));
        var timeout = await Assert.ThrowsAsync<SmartSearchRefusedException>(() => harness.Engine.ExplainAsync(library, world.Admin, PlanPurpose.Search, ct));
        Assert.Equal(SmartSearchRefusedException.TimeoutCode, timeout.Code);
        world.Core.OnQuery = null;

        using var held = await harness.Slots.EnterAsync(EvaluationPurpose.Search, null, ct);
        var busy = await Assert.ThrowsAsync<SmartSearchRefusedException>(() => harness.Engine.ExplainAsync(library, world.Kid, PlanPurpose.Search, ct));
        Assert.Equal(SmartSearchRefusedException.BusyCode, busy.Code);
    }
}
