using System;
using System.Collections.Generic;
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
/// The server context of a whole evaluation (the auto-collections): the libraries it may read, the evaluation user's
/// access, the ratings viewer the facts sources are asked with, the pass's time, and one result per version set.
/// </summary>
public sealed class EvaluationContextTests
{
    private const string InMemory = """{"Field":"Overview","Op":"Contains","Value":"keep"}""";

    [Fact]
    public async Task OnlyTheAllowedLibraries_AreRead()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        var movie = Movie(world, "In Movies");
        Movie(world, "In Private", world.Private);
        var show = world.Add(BaseItemKind.Series, "A show", world.Shows, s => s.Overview = "keep");

        var result = await EvaluateAsync(harness, Q(InMemory, "\"Movie\",\"Series\""), new EvaluationContext(EvaluationPurpose.AutoCollection, AllowedLibraries: [world.Movies.Id, world.Shows.Id]));
        var none = await EvaluateAsync(harness, Q(InMemory), new EvaluationContext(EvaluationPurpose.AutoCollection, AllowedLibraries: []));

        Assert.Equal(new[] { movie.Id, show.Id }.Order(), result.OrderedIds.Order());
        Assert.Empty(none.OrderedIds);
        Assert.Equal(QueryPlanPath.Empty, none.Plan.Path);
    }

    [Fact]
    public async Task TheEvaluationUsersAccess_StillApplies_InsideTheAllowedLibraries()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        var movie = Movie(world, "In Movies");
        Movie(world, "In Private", world.Private);

        // Kid can't open Private: allowing it doesn't widen what Kid's evaluation reads.
        var result = await EvaluateAsync(harness, Q(InMemory), new EvaluationContext(EvaluationPurpose.AutoCollection, world.Kid, [world.Movies.Id, world.Private.Id]));

        Assert.Equal([movie.Id], result.OrderedIds);
    }

    [Fact]
    public async Task TheRatingsViewer_IsWhoTheFactsSourcesAreAskedFor()
    {
        var world = new PlannerWorld();
        var source = new RecordingFacts();
        using var harness = new EngineHarness(world, TestFacts.Registry, [source]);
        Movie(world, "Film");
        var query = Q("""{"Field":"TestFact","Op":"Ge","Value":0}""");
        var creator = Guid.NewGuid();

        await EvaluateAsync(harness, query, new EvaluationContext(EvaluationPurpose.AutoCollection, RatingsViewerId: creator));
        await EvaluateAsync(harness, query, new EvaluationContext(EvaluationPurpose.AutoCollection, world.Kid));
        await EvaluateAsync(harness, query, new EvaluationContext(EvaluationPurpose.AutoCollection, world.Kid, RatingsViewerId: creator));
        await harness.SearchAll(harness.Read(query), world.Admin);

        Assert.Equal([creator, world.Kid.Id, creator, world.Admin.Id], source.Viewers);
    }

    [Fact]
    public async Task AChainedSet_IsOneResult_EvenWithAnEvaluationUser()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        var a = Movie(world, "Heat");
        var b = Movie(world, "Heat cut");
        var c = Movie(world, "Heat director");
        world.Core.MergeVersions(b, c);
        world.Core.MergeVersions(a, b);
        var query = harness.Read(Q(InMemory));

        var search = await harness.Engine.EvaluateAllAsync(query, world.Admin, EvaluationPurpose.Preview, TestContext.Current.CancellationToken);
        var collection = await harness.Engine.EvaluateAllAsync(query, new EvaluationContext(EvaluationPurpose.AutoCollection, world.Admin), TestContext.Current.CancellationToken);

        Assert.Equal([a.Id, c.Id], search.OrderedIds);
        Assert.Equal([a.Id], collection.OrderedIds);
    }

    [Fact]
    public async Task RelativeDates_AreMeasuredFromThePassTime()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        var added = Movie(world, "Added", set: s => s.DateCreated = PlannerWorld.Now.AddDays(-10));
        var query = Q("""{"Field":"DateAdded","Op":"InLast","Amount":7,"Unit":"Days"}""");

        var now = await EvaluateAsync(harness, query, new EvaluationContext(EvaluationPurpose.AutoCollection));
        var earlier = await EvaluateAsync(harness, query, new EvaluationContext(EvaluationPurpose.AutoCollection, NowUtc: PlannerWorld.Now.AddDays(-5)));

        Assert.Empty(now.OrderedIds);
        Assert.Equal([added.Id], earlier.OrderedIds);
    }

    private static string Q(string rules, string kinds = "\"Movie\"") => QueryPlannerTests.Q(rules, kinds);

    private static Task<EvaluationResult> EvaluateAsync(EngineHarness harness, string json, EvaluationContext context)
        => harness.Engine.EvaluateAllAsync(harness.Read(json), context, TestContext.Current.CancellationToken);

    private static FakeRow Movie(PlannerWorld world, string name, CollectionFolder? library = null, Action<RowSpec>? set = null)
        => world.Add(BaseItemKind.Movie, name, library ?? world.Movies, s =>
        {
            s.Year = 2000;
            s.Overview = "keep";
            s.OfficialRating = "PG";
            s.InheritedRating = 10;
            set?.Invoke(s);
        });

    private sealed class RecordingFacts : IItemFactsSource
    {
        public List<Guid?> Viewers { get; } = [];

        public bool Answers(string fieldId) => fieldId == TestFacts.FieldId;

        public IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, object?>> Load(IReadOnlyList<BaseItem> items, IReadOnlyCollection<string> fieldIds, Guid? viewerId, CancellationToken cancellationToken)
        {
            Viewers.Add(viewerId);
            return items.ToDictionary(i => i.Id, TestFacts.For);
        }
    }
}
