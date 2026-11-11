using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Execution;

/// <summary>
/// The engine's tests: both paths through <see cref="FakeCore"/>'s 10.11.5 semantics, the page
/// hydrator, the count rule, the safety net, version collapse, the order and its tiebreak, the cache's scoping and
/// invalidation, the limits (candidates, timeout, cancellation, slots), three-valued facts and the page checks.
/// </summary>
public sealed class SmartSearchEngineTests
{
    // Year Eq is pushed Exact (a user's search of it takes the Fast path); Overview Contains stays in memory (Residual).
    private const string Exact = """{"Field":"Year","Op":"Eq","Value":2000}""";

    private const string InMemory = """{"Field":"Overview","Op":"Contains","Value":"keep"}""";

    private static string Q(string rules, string kinds = "\"Movie\"", string extra = "") => QueryPlannerTests.Q(rules, kinds, extra: extra);

    // A movie of 2000 with a "keep" overview that Kid may see (rated PG, so neither the limit nor "unrated" blocks it).
    private static FakeRow Movie(PlannerWorld world, string name, CollectionFolder? library = null, Action<RowSpec>? set = null)
        => world.Add(BaseItemKind.Movie, name, library ?? world.Movies, s =>
        {
            s.Year = 2000;
            s.Overview = "keep";
            s.OfficialRating = "PG";
            s.InheritedRating = 10;
            set?.Invoke(s);
        });

    // ------------------------------------------------------------------------------- user data and images on every page

    [Theory]
    [InlineData("""{"Field":"Played","Op":"Is","Value":true}""")]
    [InlineData("""{"Field":"Favorite","Op":"Is","Value":true}""")]
    [InlineData("""{"Field":"InProgress","Op":"Is","Value":true}""")]
    [InlineData("""{"Field":"PlayCount","Op":"Ge","Value":1}""")]
    [InlineData("""{"Field":"LastPlayed","Op":"After","Value":"2020-01-01"}""")]
    public void Candidates_LoadUserData_ForEveryPerUserRuleLeftInMemory_AndNeverImages(string rule)
    {
        var world = new PlannerWorld();
        var plan = world.Plan(Q("""{"Match":"Any","Children":[""" + rule + "," + InMemory + "]}"), world.Admin);

        Assert.Equal(QueryPlanPath.Residual, plan.Path);
        Assert.True(plan.DtoOptions.EnableUserData);
        Assert.False(plan.DtoOptions.EnableImages);
        Assert.False(plan.CandidateQuery!.DtoOptions.EnableImages);
        Assert.True(plan.CreateHydrationQuery([Guid.NewGuid()]).DtoOptions.EnableUserData);
    }

    [Fact]
    public void ThePerUserFields_AreTheFiveTheTheoryCovers()
        => Assert.Equal(["Favorite", "InProgress", "LastPlayed", "PlayCount", "Played"], PlanRunner.Registry.Fields.Where(f => f.PerUser).Select(f => f.Id).Order(StringComparer.Ordinal));

    [Fact]
    public void AnItemLoadedWithoutUserData_ReadsAsNeverPlayed_AndWithoutImages_HasNone()
    {
        var world = new PlannerWorld();
        var row = Movie(world, "Watched", set: s =>
        {
            s.UserData.Add(new FakeUserData(world.Admin.Id, string.Empty, true, true, 0, 3));
            s.Images.Add(new ItemImageInfo { Type = ImageType.Primary, Path = "/images/watched.jpg" });
        });
        BaseItem Load(bool userData, bool images) => world.Core.Library.Object.GetItemList(new InternalItemsQuery
        {
            ItemIds = [row.Id],
            DtoOptions = new DtoOptions(false) { EnableUserData = userData, EnableImages = images }
        }).Single();

        var bare = Load(false, false);
        var data = world.Core.UserData.Object.GetUserData(world.Admin, bare);
        Assert.NotNull(data);
        Assert.False(data.Played);
        Assert.False(data.IsFavorite);
        Assert.Equal(0, data.PlayCount);
        Assert.Empty(bare.ImageInfos);

        var full = Load(true, true);
        Assert.True(world.Core.UserData.Object.GetUserData(world.Admin, full)!.Played);
        Assert.Equal(3, world.Core.UserData.Object.GetUserData(world.Admin, full)!.PlayCount);
        Assert.Single(full.ImageInfos);
    }

    [Theory]
    [InlineData(Exact, "Fast")]
    [InlineData(InMemory, "Residual")]
    public async Task EveryPage_OnEitherPath_CarriesTheUsersDataAndThePrimaryImage(string rule, string path)
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        for (var i = 0; i < 5; i++)
        {
            Movie(world, "Film " + i, set: s =>
            {
                s.UserData.Add(new FakeUserData(world.Admin.Id, string.Empty, true, false, 0, 2));
                s.Images.Add(new ItemImageInfo { Type = ImageType.Primary, Path = "/images/" + s.Name + ".jpg" });
            });
        }

        var run = await harness.SearchAll(harness.Read(Q(rule)), world.Admin, pageSize: 2);

        Assert.Equal(path, run.Path.ToString());
        Assert.Equal(3, run.Pages.Count);
        Assert.Equal(5, run.Ids.Count);
        Assert.All(run.Pages.SelectMany(p => p.Items), dto =>
        {
            Assert.True(dto.UserData!.Played);
            Assert.Equal(2, dto.UserData.PlayCount);
            Assert.True(dto.ImageTags!.ContainsKey(ImageType.Primary));
        });

        // The candidates were loaded without either (these rules need neither); only the page's hydration asks for them.
        var withImages = world.Core.Queries.Where(q => q.DtoOptions.EnableImages).ToList();
        Assert.Equal(3, withImages.Count);
        Assert.All(withImages, q =>
        {
            Assert.True(q.DtoOptions.EnableUserData);
            Assert.Same(world.Admin, q.User);
            Assert.NotEmpty(q.ItemIds);
            Assert.False(q.GroupByPresentationUniqueKey);
        });
        Assert.All(world.Core.Queries.Except(withImages), q => Assert.False(q.DtoOptions.EnableUserData));
    }

    [Fact]
    public void ThePagesDtoOptions_AskForImagesUserDataAndTheListsFields()
    {
        var options = PageHydrator.PageOptions();
        Assert.True(options.EnableImages);
        Assert.True(options.EnableUserData);
        Assert.Equal(1, options.ImageTypeLimit);
        Assert.Equal([ImageType.Primary, ImageType.Backdrop, ImageType.Thumb], options.ImageTypes);
        Assert.Contains(ItemFields.PrimaryImageAspectRatio, options.Fields);
    }

    // ------------------------------------------------------------------------------------------------ the count rule

    [Fact]
    public async Task FastPages_DropWhatIsVisibleRefuses_AndCountAsSqlDoes()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        for (var i = 0; i < 10; i++)
        {
            Movie(world, "Film " + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
        }

        // A tag the movie inherits from a parent folder: SQL's ExcludeInheritedTags doesn't see it, IsVisible does.
        var gore = Movie(world, "Film 03b");
        gore.MemoryTags.Add("Gore");
        var query = harness.Read(Q(Exact));

        var kid = await harness.SearchAll(query, world.Kid, pageSize: 4);

        Assert.Equal(QueryPlanPath.Fast, kid.Path);
        Assert.Equal(11, kid.Total);
        Assert.Equal(PlanRunner.ItemsEndpoint(query, world.Core, world.Kid).Counted.Count, kid.Total);
        Assert.Equal(10, kid.Ids.Count);
        Assert.DoesNotContain(gore.Id, kid.Ids);
        Assert.Equal(3, kid.Pages.Count);
        Assert.Equal(1, kid.ShortPages);
        Assert.Equal([4, 3, 3], kid.Pages.Select(p => p.Items.Count));

        var admin = await harness.SearchAll(query, world.Admin, pageSize: 4);
        Assert.Equal(11, admin.Ids.Count);
        Assert.Equal(0, admin.ShortPages);
    }

    [Fact]
    public async Task BoxSetsAlone_AreCountedAfterIsVisible_AsItemsCountsThem()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        void Rated(RowSpec s)
        {
            s.OfficialRating = "PG";
            s.InheritedRating = 10;
        }

        var shown = world.Add(BaseItemKind.Movie, "Shown", world.Movies, Rated);
        var hidden = world.Add(BaseItemKind.Movie, "Private child", world.Private, Rated);
        var open = world.Add(BaseItemKind.BoxSet, "Open set", world.Collections, Rated);
        open.Children.AddRange([shown.Id, hidden.Id]);
        var closed = world.Add(BaseItemKind.BoxSet, "Closed set", world.Collections, Rated);
        closed.Children.Add(hidden.Id);
        var empty = world.Add(BaseItemKind.BoxSet, "Empty set", world.Collections, Rated);
        var query = harness.Read(Q("""{"Field":"Locked","Op":"Is","Value":false}""", "\"BoxSet\""));

        var kid = await harness.SearchAll(query, world.Kid, pageSize: 1);
        var admin = await harness.SearchAll(query, world.Admin, pageSize: 1);

        Assert.Equal(QueryPlanPath.Residual, kid.Path);
        Assert.Equal(new[] { empty.Id, open.Id }.Order(), kid.Ids.Order());
        Assert.Equal(2, kid.Total);
        Assert.Equal(PlanRunner.ItemsEndpoint(query, world.Core, world.Kid).Counted.Count, kid.Total);
        Assert.Equal(0, kid.ShortPages);
        Assert.Equal(3, admin.Total);
        Assert.Contains(closed.Id, admin.Ids);
    }

    // ------------------------------------------------------------------------------------------------ the safety net

    [Fact]
    public async Task OwnedItems_AreNeverResults_EvenWhenSqlLetsThemThrough()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        var movie = Movie(world, "Film");

        // An extra stored with its library's folder: SQL lets it through (the planner never sets HasOwnerId).
        var extra = Movie(world, "Film trailer", set: s =>
        {
            s.Owner = movie;
            s.TopParentId = world.FolderA;
        });

        var residual = await harness.SearchAll(harness.Read(Q(InMemory)), world.Admin);
        var fast = await harness.SearchAll(harness.Read(Q(Exact)), world.Admin);

        Assert.Equal([movie.Id], residual.Ids);
        Assert.Equal(QueryPlanPath.Fast, fast.Path);
        Assert.Equal([movie.Id], fast.Ids);
        Assert.Equal(1, fast.Pages[0].Dropped);
        Assert.NotEqual(Guid.Empty, extra.Item.OwnerId);
    }

    [Fact]
    public void TheHydrator_DropsOwnedItems_AndIdsThatNoLongerLoad()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        var movie = Movie(world, "Film");
        var extra = Movie(world, "Film trailer", set: s =>
        {
            s.Owner = movie;
            s.TopParentId = world.FolderA;
        });
        var gone = Guid.NewGuid();

        var page = harness.Hydrator.Hydrate([extra.Id, movie.Id, gone, movie.Id], world.Admin, [world.Movies.Id], TestContext.Current.CancellationToken);

        Assert.Equal([movie.Id], page.Items.Select(i => i.Id));
        Assert.Equal(new[] { extra.Id, gone }.Order(), page.Dropped.Order());

        // The libraries are checked again: a library the user can no longer open hydrates nothing.
        Assert.Empty(harness.Hydrator.Hydrate([movie.Id], world.Kid, [world.Private.Id], TestContext.Current.CancellationToken).Items);
    }

    // ------------------------------------------------------------------------------------------------ versions

    [Fact]
    public async Task MergedVersions_AreOneResult_ThePrimaryWhenEligible_ElseAMatchingMember()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);

        // Heat: the primary in Movies doesn't match, its cut in Movies' other folder does.
        var heat = Movie(world, "Heat", set: s => s.Overview = "plain");
        var heatCut = Movie(world, "Heat", set: s => s.TopParentId = world.FolderB);
        world.Core.Merge(heat, heatCut);

        // Ronin: the primary is in Private (Kid can't open it), the cut in Movies; both match.
        var ronin = Movie(world, "Ronin", world.Private);
        var roninCut = Movie(world, "Ronin", set: s => s.TopParentId = world.FolderB);
        world.Core.Merge(ronin, roninCut);
        var query = harness.Read(Q(InMemory));

        var admin = await harness.SearchAll(query, world.Admin);
        var kid = await harness.SearchAll(query, world.Kid);
        var none = await harness.Engine.EvaluateAllAsync(query, null, EvaluationPurpose.AutoCollection, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { heat.Id, ronin.Id }.Order(), admin.Ids.Order());
        Assert.Equal(new[] { heat.Id, roninCut.Id }.Order(), kid.Ids.Order());
        Assert.Equal(new[] { heat.Id, ronin.Id }.Order(), none.OrderedIds.Order());
        Assert.Equal(2, admin.Total);
    }

    [Fact]
    public async Task AFastPagesSecondary_IsShownAsItsPrimary_WhenThePrimaryIsEligible()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        var heat = Movie(world, "Heat", set: s => s.Year = 1995);
        var heatCut = Movie(world, "Heat", set: s =>
        {
            s.Year = 2000;
            s.TopParentId = world.FolderB;
        });
        world.Core.Merge(heat, heatCut);
        var ronin = Movie(world, "Ronin", world.Private, s => s.Year = 1998);
        var roninCut = Movie(world, "Ronin", set: s => s.TopParentId = world.FolderB);
        world.Core.Merge(ronin, roninCut);

        var admin = await harness.SearchAll(harness.Read(Q(Exact)), world.Admin);
        var kid = await harness.SearchAll(harness.Read(Q(Exact)), world.Kid);

        Assert.Equal(QueryPlanPath.Fast, admin.Path);
        Assert.Equal(new[] { heat.Id, ronin.Id }.Order(), admin.Ids.Order());
        Assert.Equal(new[] { heat.Id, roninCut.Id }.Order(), kid.Ids.Order());
        Assert.Equal(2, admin.Total);
    }

    [Fact]
    public async Task AFastRowOfMergedVersions_MatchesOnlyThroughAMemberTheUserMaySee_AsTheResidualPathDoes()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);

        // Heat: the cut matches (2000) but inherits a tag Kid blocks; the primary (1995), which Kid may see, doesn't match.
        var heat = Movie(world, "Heat", set: s => s.Year = 1995);
        var heatCut = Movie(world, "Heat", set: s => s.TopParentId = world.FolderB);
        heatCut.MemoryTags.Add("Gore");
        world.Core.Merge(heat, heatCut);

        // Ronin: both match; SQL keeps the key's first row, the primary, which Kid can't see; the cut he can.
        var ronin = Movie(world, "Ronin");
        ronin.MemoryTags.Add("Gore");
        var roninCut = Movie(world, "Ronin", set: s => s.TopParentId = world.FolderB);
        world.Core.Merge(ronin, roninCut);

        var fast = await harness.SearchAll(harness.Read(Q(Exact)), world.Kid);
        var residual = await harness.SearchAll(harness.Read(Q(Exact + "," + InMemory)), world.Kid);
        var admin = await harness.SearchAll(harness.Read(Q(Exact)), world.Admin);

        Assert.Equal(QueryPlanPath.Fast, fast.Path);
        Assert.Equal([roninCut.Id], fast.Ids);
        Assert.Equal(2, fast.Total);
        Assert.Equal(1, fast.ShortPages);
        Assert.Equal(QueryPlanPath.Residual, residual.Path);
        Assert.Equal([roninCut.Id], residual.Ids);
        Assert.Equal([heat.Id, ronin.Id], admin.Ids);
    }

    // ------------------------------------------------------------------------------------------------ a rating of 0

    // A stored community rating of 0 is a rating, as core's own filter has it: "at least 0" finds it, as
    // MinCommunityRating 0 does, and "is empty" doesn't (core's DTO leaves the 0 out, so pages show no rating).
    [Fact]
    public async Task AStoredCommunityRatingOfZero_IsARating_AsCoresMinCommunityRatingHasIt()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        Movie(world, "none", set: s => s.CommunityRating = null);
        Movie(world, "zero", set: s => s.CommunityRating = 0f);
        Movie(world, "half", set: s => s.CommunityRating = 0.5f);
        async Task<List<string>> Names(string rule)
        {
            harness.Cache.InvalidateAll();
            var run = await harness.SearchAll(harness.Read(Q(rule)), world.Admin);
            return run.Ids.Select(id => world.Core.RowById(id)!.Item.Name).Order(StringComparer.Ordinal).ToList();
        }

        var core = world.Core.Run(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Movie], MinCommunityRating = 0 })
            .Select(r => r.Item.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["half", "zero"], core);
        Assert.Equal(core, await Names("""{"Field":"CommunityRating","Op":"Ge","Value":0}"""));
        Assert.Equal(core, await Names("""{"Field":"CommunityRating","Op":"IsNotEmpty"}"""));
        Assert.Equal(["none"], await Names("""{"Field":"CommunityRating","Op":"IsEmpty"}"""));
        Assert.Equal(["zero"], await Names("""{"Field":"CommunityRating","Op":"Eq","Value":0}"""));
        Assert.Equal(["half"], await Names("""{"Field":"CommunityRating","Op":"Gt","Value":0}"""));
    }

    // ------------------------------------------------------------------------------------------------ the order

    [Fact]
    public async Task TheResidualOrder_FollowsSqlsOrder()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        async Task<List<string>> Sorted(string key, string order = "Ascending")
        {
            harness.Cache.InvalidateAll();
            var run = await harness.SearchAll(harness.Read(Q(InMemory, extra: ",\"Sort\":[{\"Key\":\"" + key + "\",\"Order\":\"" + order + "\"}]")), world.Admin);
            Assert.Equal(QueryPlanPath.Residual, run.Path);
            return run.Ids.Select(id => world.Core.RowById(id)!.Item.Name).ToList();
        }

        // ProductionYear: the stored year, a missing one first (core's comparer would take the premiere's year). PremiereDate:
        // the date, else January 1st of the year, else first. CommunityRating: a missing rating first, below 0.
        // OfficialRating: the stored inherited score, a missing one first (core's comparer reads the item's own rating).
        Movie(world, "a", set: s =>
        {
            s.Year = null;
            s.PremiereDate = new DateTime(2005, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            s.CommunityRating = null;
            s.OfficialRating = "PG-13";
            s.InheritedRating = 13;
        });
        Movie(world, "b", set: s =>
        {
            s.Year = 2004;
            s.CommunityRating = 0.5f;
            s.OfficialRating = "Unknown";
            s.InheritedRating = null;
        });
        Movie(world, "c", set: s =>
        {
            s.Year = null;
            s.CommunityRating = 0f;
            s.OfficialRating = null;
            s.InheritedRating = null;
        });
        Movie(world, "d", set: s =>
        {
            s.Year = 2005;
            s.PremiereDate = new DateTime(2005, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            s.CommunityRating = 7f;
            s.OfficialRating = "G";
            s.InheritedRating = 0;
        });

        Assert.Equal(["a", "c", "b", "d"], await Sorted("ProductionYear"));
        Assert.Equal(["c", "b", "d", "a"], await Sorted("PremiereDate"));
        Assert.Equal(["a", "c", "b", "d"], await Sorted("CommunityRating"));
        // Descending reverses the keys (a missing value last); ties still go by sort name, then id, ascending.
        Assert.Equal(["d", "b", "c", "a"], await Sorted("CommunityRating", "Descending"));
        Assert.Equal(["b", "c", "d", "a"], await Sorted("OfficialRating"));
        Assert.Equal(["a", "b", "c", "d"], await Sorted("SortName"));
        Assert.Equal(["d", "c", "b", "a"], await Sorted("SortName", "Descending"));
    }

    [Theory]
    [InlineData("SortName")]
    [InlineData("ProductionYear")]
    [InlineData("PremiereDate")]
    [InlineData("DateCreated")]
    [InlineData("CommunityRating")]
    [InlineData("CriticRating")]
    [InlineData("Runtime")]
    [InlineData("OfficialRating")]
    [InlineData("VideoBitRate")]
    [InlineData("DatePlayed")]
    [InlineData("PlayCount")]
    public async Task TheResidualOrder_IsTheFastPathsOrder_ForEveryKeyBothWays(string key)
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        var admin = world.Admin.Id;
        var day = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Added in SQL's sort-name order (code points: capitals, then '[', then small letters, then accented ones), so that
        // the rows SQL leaves tied keep the order the Residual path's tiebreak gives them. Every value is distinct or missing.
        (string Name, int? Year, DateTime? Premiere, int Created, float? Community, float? Critic, long? Runtime, int? Rating, int? Bitrate, FakeUserData? Data)[] rows =
        [
            ("Alpha", null, null, 5, null, 50, null, null, null, null),
            ("Zulu", 1999, null, 3, 0f, null, 100, 0, 1000, new FakeUserData(admin, "k", false, false, 0, 0, null)),
            ("[Rec]", 2010, day.AddYears(-12), 1, 7.1f, 80, 50, 13, 5000, new FakeUserData(admin, "k", true, false, 0, 3, day.AddDays(2))),
            ("beta", 2001, day.AddYears(-20), 4, 5f, 10, 200, 10, 200, new FakeUserData(admin, "k", true, false, 0, 1, day.AddDays(5))),
            ("delta", null, day.AddYears(-17), 2, null, 90, 70, null, null, new FakeUserData(admin, "k", true, false, 0, 2, day)),
            ("\u00e9mile", 1800, null, 6, 9.5f, 20, 10, 18, 300, null)
        ];
        foreach (var r in rows)
        {
            Movie(world, r.Name, set: s =>
            {
                s.SortName = r.Name;
                s.Year = r.Year;
                s.PremiereDate = r.Premiere;
                s.DateCreated = day.AddDays(r.Created);
                s.CommunityRating = r.Community;
                s.CriticRating = r.Critic;
                s.RunTimeTicks = r.Runtime;
                s.InheritedRating = r.Rating;
                s.Configure = item => item.TotalBitrate = r.Bitrate;
                if (r.Data is { } data)
                {
                    s.UserData.Add(data);
                }
            });
        }

        foreach (var order in new[] { "Ascending", "Descending" })
        {
            var sort = ",\"Sort\":[{\"Key\":\"" + key + "\",\"Order\":\"" + order + "\"}]";
            harness.Cache.InvalidateAll();
            var fast = await harness.SearchAll(harness.Read(Q(string.Empty, extra: sort)), world.Admin);
            harness.Cache.InvalidateAll();
            var residual = await harness.SearchAll(harness.Read(Q(InMemory, extra: sort)), world.Admin);

            Assert.Equal(QueryPlanPath.Fast, fast.Path);
            Assert.Equal(QueryPlanPath.Residual, residual.Path);
            Assert.Equal(rows.Length, fast.Ids.Count);
            Assert.Equal(fast.Ids, residual.Ids);
        }
    }

    [Fact]
    public async Task TheResidualOrder_BreaksTiesBySortNameThenId_AcrossPageBoundaries()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);

        // Ids added in descending order, so that table order and id order disagree.
        var same = Enumerable.Range(0, 25).Reverse().Select(i => Movie(world, "Same", set: s => s.Id = new Guid(i + 1, 0, 0, new byte[8]))).ToList();
        var other = Movie(world, "Another", set: s => s.Id = new Guid(100, 0, 0, new byte[8]));
        var query = harness.Read(Q(InMemory, extra: ",\"Sort\":[{\"Key\":\"ProductionYear\",\"Order\":\"Ascending\"}]"));

        var run = await harness.SearchAll(query, world.Admin, pageSize: 7);

        Assert.Equal(4, run.Pages.Count);
        Assert.Equal(26, run.Ids.Distinct().Count());
        Assert.Equal([other.Id, .. same.Select(r => r.Id).Order()], run.Ids);
    }

    [Fact]
    public void TheFastPath_AddsNoSortKeyCoreDoesnt()
    {
        // The decision is to document that Fast pages over equal keys are as stable as SQL's
        // order, not to add a tiebreak core's /Items doesn't have.
        var world = new PlannerWorld();
        Assert.Equal([(ItemSortBy.SortName, SortOrder.Ascending)], world.Plan(Q(Exact), world.Admin).CandidateQuery!.OrderBy);
        Assert.Equal(
            [(ItemSortBy.ProductionYear, SortOrder.Descending)],
            world.Plan(Q(Exact, extra: ",\"Sort\":[{\"Key\":\"ProductionYear\",\"Order\":\"Descending\"}]"), world.Admin).CandidateQuery!.OrderBy);
    }

    // ------------------------------------------------------------------------------------------------ the cache

    [Fact]
    public async Task AToken_IsOnlyEverTheUsersItWasMadeFor()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        for (var i = 0; i < 3; i++)
        {
            Movie(world, "Film " + i);
        }

        Movie(world, "Rated R", set: s =>
        {
            s.OfficialRating = "R";
            s.InheritedRating = 17;
        });
        var query = harness.Read(Q(InMemory));

        var admin = await harness.Search(query, world.Admin, 0, 10);
        var kid = await harness.Search(query, world.Kid, 0, 10, admin.ResultToken);
        var adminAgain = await harness.Search(query, world.Admin, 0, 10, admin.ResultToken);

        Assert.Equal(4, admin.Items.Count);
        Assert.Equal(3, kid.Items.Count);
        Assert.Equal([SmartSearchEngine.ReevaluatedWarning], kid.Warnings);
        Assert.NotEqual(admin.ResultToken, kid.ResultToken);
        Assert.Empty(adminAgain.Warnings);
        Assert.Equal(admin.ResultToken, adminAgain.ResultToken);
        Assert.Equal(4, adminAgain.Items.Count);
    }

    [Fact]
    public async Task AToken_OfAUserWithTheSameAccess_StillEvaluatesForTheirOwnData()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        var other = PlannerWorld.NewUser("other");
        var watched = Movie(world, "Watched", set: s => s.UserData.Add(new FakeUserData(world.Admin.Id, string.Empty, true, false, 0, 1)));
        Movie(world, "Unwatched");
        var query = harness.Read(Q("""{"Field":"Played","Op":"Is","Value":true},""" + InMemory));

        var admin = await harness.Search(query, world.Admin);
        var theirs = await harness.Search(query, other, token: admin.ResultToken);

        Assert.Equal([watched.Id], admin.Items.Select(i => i.Id));
        Assert.Empty(theirs.Items);
        Assert.Equal([SmartSearchEngine.ReevaluatedWarning], theirs.Warnings);
    }

    [Fact]
    public async Task ASearchRepeatedInTheSameMinute_ReusesItsResult()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        Movie(world, "Film");
        var query = harness.Read(Q(InMemory));

        var first = await harness.Search(query, world.Admin);
        var candidates = world.Core.Queries.Count(q => q.ItemIds.Length == 0);
        var second = await harness.Search(query, world.Admin);

        Assert.Equal(first.ResultToken, second.ResultToken);
        Assert.Equal(candidates, world.Core.Queries.Count(q => q.ItemIds.Length == 0));
    }

    // The cache is keyed by user as well as by query: two users' results of one query live side by side, and each
    // user's next search of it is their own entry again (a key without the user would hand one user the other's token,
    // which the token's own user check then refuses, so each search would evaluate again).
    [Fact]
    public async Task TwoUsersSearchingOneQuery_EachKeepTheirOwnResult()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        Movie(world, "Film");
        Movie(world, "Rated R", set: s =>
        {
            s.OfficialRating = "R";
            s.InheritedRating = 17;
        });
        var query = harness.Read(Q(InMemory));

        var admin = await harness.Search(query, world.Admin);
        var kid = await harness.Search(query, world.Kid);
        var candidates = world.Core.Queries.Count(q => q.ItemIds.Length == 0);
        var adminAgain = await harness.Search(query, world.Admin);
        var kidAgain = await harness.Search(query, world.Kid);

        Assert.Equal(2, admin.Items.Count);
        Assert.Single(kid.Items);
        Assert.NotEqual(admin.ResultToken, kid.ResultToken);
        Assert.Equal(admin.ResultToken, adminAgain.ResultToken);
        Assert.Equal(kid.ResultToken, kidAgain.ResultToken);
        Assert.Equal(2, adminAgain.Items.Count);
        Assert.Single(kidAgain.Items);
        Assert.Equal(candidates, world.Core.Queries.Count(q => q.ItemIds.Length == 0));
    }

    [Fact]
    public async Task InvalidationExpiryAndAccessChanges_EvaluateAgain_AndSaySo()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        for (var i = 0; i < 4; i++)
        {
            Movie(world, "Film " + i);
        }

        var rated = Movie(world, "Rated R", set: s =>
        {
            s.OfficialRating = "R";
            s.InheritedRating = 17;
        });
        var plain = harness.Read(Q(InMemory));
        var played = harness.Read(Q("""{"Match":"Any","Children":[{"Field":"Played","Op":"Is","Value":true},""" + InMemory + "]}"));
        async Task<IReadOnlyList<string>> Next(Jellyfin.Plugin.SmartSearch.Api.Models.SmartSearchQueryDefinition query, Action change, Jellyfin.Database.Implementations.Entities.User? user = null)
        {
            user ??= world.Admin;
            var first = await harness.Search(query, user, 0, 2);
            change();
            return (await harness.Search(query, user, 2, 2, first.ResultToken)).Warnings;
        }

        Assert.Empty(await Next(plain, () => { }));
        Assert.Equal([SmartSearchEngine.ReevaluatedWarning], await Next(plain, harness.Cache.InvalidateAll));
        Assert.Equal([SmartSearchEngine.ReevaluatedWarning], await Next(plain, () => harness.Cache.InvalidateUser(world.Admin.Id)));

        // Saved user data ends only the results that read it.
        Assert.Empty(await Next(plain, () => harness.Cache.InvalidateUserData(world.Admin.Id)));
        Assert.Equal([SmartSearchEngine.ReevaluatedWarning], await Next(played, () => harness.Cache.InvalidateUserData(world.Admin.Id)));
        Assert.Equal([SmartSearchEngine.ReevaluatedWarning], await Next(plain, () => harness.Clock.Advance(TimeSpan.FromSeconds(harness.Settings.ResultCacheSeconds))));

        // A policy change (core raises no event for it) changes the fingerprint: Kid's raised limit shows the R movie.
        var before = await harness.Search(plain, world.Kid, 0, 10);
        Assert.DoesNotContain(rated.Id, before.Items.Select(i => i.Id));
        world.Kid.MaxParentalRatingScore = 17;
        var after = await harness.Search(plain, world.Kid, 0, 10, before.ResultToken);
        Assert.Equal([SmartSearchEngine.ReevaluatedWarning], after.Warnings);
        Assert.Contains(rated.Id, after.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task AnotherQuerysToken_EvaluatesAgain_AndLeavesThatResultAlone()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        Movie(world, "Film");
        var a = await harness.Search(harness.Read(Q(InMemory)), world.Admin);
        var b = await harness.Search(harness.Read(Q(InMemory + "," + Exact)), world.Admin, token: a.ResultToken);
        var again = await harness.Search(harness.Read(Q(InMemory)), world.Admin, token: a.ResultToken);

        Assert.Equal([SmartSearchEngine.ReevaluatedWarning], b.Warnings);
        Assert.Empty(again.Warnings);
        Assert.Equal(a.ResultToken, again.ResultToken);
    }

    [Fact]
    public void TheCache_KeepsTwentyPerUser_AndTwoHundredInAll_DroppingTheLeastRecentlyUsed()
    {
        var clock = new ManualTimeProvider();
        using var cache = new ResultCache(null, null, null, clock, () => TimeSpan.FromHours(1));
        static CachedResult Entry(Guid user, int n) => new()
        {
            UserId = user,
            QueryHash = "hash" + n,
            Bucket = 0,
            Fingerprint = "f",
            Path = QueryPlanPath.Residual,
            NowUtc = DateTime.UnixEpoch,
            LibraryIds = []
        };
        string Store(Guid user, int n)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return cache.Store(Entry(user, n));
        }

        var user = Guid.NewGuid();
        var tokens = Enumerable.Range(0, ResultCache.PerUser).Select(n => Store(user, n)).ToList();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(cache.TryGet(tokens[0], user, "hash0", "f"));
        Store(user, 99);

        Assert.Equal(ResultCache.PerUser, cache.Count);
        Assert.NotNull(cache.TryGet(tokens[0], user, "hash0", "f"));
        Assert.Null(cache.TryGet(tokens[1], user, "hash1", "f"));

        var others = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        foreach (var other in others)
        {
            for (var n = 0; n < ResultCache.PerUser; n++)
            {
                Store(other, n);
            }
        }

        Assert.Equal(ResultCache.Total, cache.Count);
        Assert.Null(cache.TryGet(tokens[0], user, "hash0", "f"));
    }

    [Fact]
    public void TheCache_EndsEntriesOnCoresEvents_UntilDisposed()
    {
        var clock = new ManualTimeProvider();
        var library = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
        var userData = new Mock<MediaBrowser.Controller.Library.IUserDataManager>();
        var users = new Mock<MediaBrowser.Controller.Library.IUserManager>();
        var cache = new ResultCache(library.Object, userData.Object, users.Object, clock, () => TimeSpan.FromHours(1));
        var u = PlannerWorld.NewUser("u");
        var v = Guid.NewGuid();
        CachedResult Entry(Guid user, string hash, bool readsUserData) => new()
        {
            UserId = user,
            QueryHash = hash,
            Bucket = 0,
            Fingerprint = "f",
            Path = QueryPlanPath.Residual,
            NowUtc = DateTime.UnixEpoch,
            LibraryIds = [],
            ReadsUserData = readsUserData
        };
        var plain = cache.Store(Entry(u.Id, "plain", false));
        var played = cache.Store(Entry(u.Id, "played", true));
        var theirs = cache.Store(Entry(v, "theirs", true));

        userData.Raise(m => m.UserDataSaved += null, new MediaBrowser.Controller.Library.UserDataSaveEventArgs { UserId = u.Id });
        Assert.Null(cache.TryGet(played, u.Id, "played", "f"));
        Assert.NotNull(cache.TryGet(plain, u.Id, "plain", "f"));
        Assert.NotNull(cache.TryGet(theirs, v, "theirs", "f"));

        users.Raise(m => m.OnUserUpdated += null, users.Object, new Jellyfin.Data.Events.GenericEventArgs<Jellyfin.Database.Implementations.Entities.User>(u));
        Assert.Null(cache.TryGet(plain, u.Id, "plain", "f"));
        Assert.NotNull(cache.TryGet(theirs, v, "theirs", "f"));

        library.Raise(m => m.ItemAdded += null, library.Object, new MediaBrowser.Controller.Library.ItemChangeEventArgs());
        Assert.Null(cache.TryGet(theirs, v, "theirs", "f"));

        foreach (var raise in new Action[]
        {
            () => library.Raise(m => m.ItemUpdated += null, library.Object, new MediaBrowser.Controller.Library.ItemChangeEventArgs()),
            () => library.Raise(m => m.ItemRemoved += null, library.Object, new MediaBrowser.Controller.Library.ItemChangeEventArgs())
        })
        {
            var token = cache.Store(Entry(v, "next", false));
            raise();
            Assert.Null(cache.TryGet(token, v, "next", "f"));
        }

        cache.Dispose();
        var kept = cache.Store(Entry(v, "kept", true));
        library.Raise(m => m.ItemAdded += null, library.Object, new MediaBrowser.Controller.Library.ItemChangeEventArgs());
        userData.Raise(m => m.UserDataSaved += null, new MediaBrowser.Controller.Library.UserDataSaveEventArgs { UserId = v });
        Assert.NotNull(cache.TryGet(kept, v, "kept", "f"));
    }

    // ------------------------------------------------------------------------------------------------ limits

    [Fact]
    public async Task PastMaxCandidates_TheSearchIsRefused_NeverCutShort()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        for (var i = 0; i < 6; i++)
        {
            Movie(world, "Film " + i);
        }

        var query = harness.Read(Q(InMemory));
        harness.Settings = new EngineSettings(MaxCandidates: 6);
        Assert.Equal(6, (await harness.Search(query, world.Admin)).Items.Count);

        harness.Cache.InvalidateAll();
        harness.Settings = new EngineSettings(MaxCandidates: 5);
        var refused = await Assert.ThrowsAsync<SmartSearchRefusedException>(() => harness.Search(query, world.Admin));
        Assert.Equal(SmartSearchRefusedException.QueryTooBroadCode, refused.Code);
        Assert.Equal(6, refused.CandidateCount);
        Assert.Equal(PluginErrorKind.Invalid, refused.Kind);

        // The auto-collections' evaluation is held to the same limit.
        await Assert.ThrowsAsync<SmartSearchRefusedException>(() => harness.Engine.EvaluateAllAsync(query, null, EvaluationPurpose.AutoCollection, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ALibraryRulesItems_CountAgainstTheSameBudget()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        for (var i = 0; i < 3; i++)
        {
            Movie(world, "Film " + i, set: s => s.Overview = "other");
        }

        // Under an Any group the Library rule stays in memory: its library's three movies are loaded as a set.
        var query = harness.Read(Q("{\"Match\":\"Any\",\"Children\":[{\"Field\":\"Library\",\"Op\":\"AnyOf\",\"Values\":[\"" + world.Movies.Id + "\"]}," + InMemory + "]}"));
        harness.Settings = new EngineSettings(MaxCandidates: 6);
        Assert.Equal(3, (await harness.Search(query, world.Admin)).Items.Count);

        harness.Cache.InvalidateAll();
        harness.Settings = new EngineSettings(MaxCandidates: 5);
        var refused = await Assert.ThrowsAsync<SmartSearchRefusedException>(() => harness.Search(query, world.Admin));
        Assert.Equal(SmartSearchRefusedException.QueryTooBroadCode, refused.Code);
    }

    [Fact]
    public async Task PastTheTimeout_TheSearchIsRefused_BetweenBatches()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        for (var i = 0; i < 2500; i++)
        {
            Movie(world, "Film " + i);
        }

        harness.Settings = new EngineSettings(SearchTimeoutSeconds: 5);
        var batches = 0;
        world.Core.OnQuery = q =>
        {
            if (q.ItemIds.Length > 0 && ++batches == 2)
            {
                harness.Clock.Advance(TimeSpan.FromSeconds(6));
            }
        };

        var refused = await Assert.ThrowsAsync<SmartSearchRefusedException>(() => harness.Search(harness.Read(Q(InMemory)), world.Admin));

        Assert.Equal(SmartSearchRefusedException.TimeoutCode, refused.Code);
        Assert.Equal(PluginErrorKind.Unavailable, refused.Kind);
        Assert.Equal(2, batches);
        Assert.Equal(0, harness.Cache.Count);
        Assert.Equal(harness.Slots.Searches, harness.Slots.FreeSearchSlots);
    }

    [Fact]
    public async Task TheCallersCancellation_StopsTheSearch_AsACancellation()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        for (var i = 0; i < 1500; i++)
        {
            Movie(world, "Film " + i);
        }

        using var cancel = new CancellationTokenSource();
        var batches = 0;
        world.Core.OnQuery = q =>
        {
            if (q.ItemIds.Length > 0 && ++batches == 1)
            {
                cancel.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Search(harness.Read(Q(InMemory)), world.Admin, cancel.Token));
        Assert.Equal(1, batches);
        Assert.Equal(0, harness.Cache.Count);

        // Cancelled before it starts, it reads nothing.
        world.Core.Queries.Clear();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Search(harness.Read(Q(InMemory)), world.Admin, cancel.Token));
        Assert.Empty(world.Core.Queries);
    }

    [Fact]
    public async Task WhenEverySearchSlotIsTaken_ASearchIsRefusedBusy_WhileAutoCollectionsHaveTheirOwn()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world, slots: 1, slotWait: TimeSpan.FromMilliseconds(100));
        Movie(world, "Film");
        var query = harness.Read(Q(InMemory));
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

        var first = Task.Run(() => harness.Search(query, world.Admin), TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        var busy = await Assert.ThrowsAsync<SmartSearchRefusedException>(() => harness.Search(query, world.Kid));
        Assert.Equal(SmartSearchRefusedException.BusyCode, busy.Code);
        Assert.Equal(PluginErrorKind.Unavailable, busy.Kind);
        await Assert.ThrowsAsync<SmartSearchRefusedException>(() => harness.Engine.EvaluateAllAsync(query, world.Kid, EvaluationPurpose.Preview, TestContext.Current.CancellationToken));
        var auto = await harness.Engine.EvaluateAllAsync(query, null, EvaluationPurpose.AutoCollection, TestContext.Current.CancellationToken);
        Assert.Single(auto.OrderedIds);

        release.Set();
        Assert.Single((await first).Items);
        Assert.Equal(1, harness.Slots.FreeSearchSlots);
    }

    // ------------------------------------------------------------------------------------------------ facts

    [Fact]
    public async Task FactsRules_AreFetchedOnlyForTheUndecided_AndDecideExactly()
    {
        var world = new PlannerWorld();
        var source = new FactsSource();
        using var harness = new EngineHarness(world, TestFacts.Registry, [source]);
        var rows = Enumerable.Range(0, 1200).Select(i => Movie(world, "Film " + i, set: s => s.Overview = i % 3 == 0 ? "keep" : "other")).ToList();
        var query = harness.Read(Q("""{"Match":"Any","Children":[""" + InMemory + """,{"Field":"TestFact","Op":"Ge","Value":50}]}"""));

        var run = await harness.SearchAll(query, world.Admin, pageSize: 200);

        var expected = rows.Where(r => r.Item.Overview == "keep" || TestFacts.Of(r.Id) >= 50).Select(r => r.Id).ToHashSet();
        Assert.Equal(expected.Order(), run.Ids.Order());
        Assert.Equal(rows.Where(r => r.Item.Overview != "keep").Select(r => r.Id).Order(), source.Loaded.Order());
        Assert.All(source.Chunks, c => Assert.True(c <= ResidualEvaluator.FactsChunkSize));
        Assert.True(source.Chunks.Count >= 2);
    }

    [Fact]
    public async Task AFactsRuleNothingAnswers_IsRefused_BeforeAnyCandidateIsRead()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world, TestFacts.Registry);
        Movie(world, "Film");
        var query = harness.Read(Q("""{"Field":"TestFact","Op":"Ge","Value":50}"""));

        var refused = await Assert.ThrowsAsync<SmartSearchRefusedException>(() => harness.Search(query, world.Admin));

        Assert.Equal(SmartSearchRefusedException.FieldUnavailableCode, refused.Code);
        world.Core.Library.Verify(l => l.GetItemIds(It.IsAny<InternalItemsQuery>()), Times.Never());
        world.Core.Library.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never());
    }

    [Fact]
    public async Task AnItemASourceLeavesOut_RefusesTheSearch_InsteadOfDroppingIt()
    {
        var world = new PlannerWorld();
        var source = new FactsSource();
        using var harness = new EngineHarness(world, TestFacts.Registry, [source]);
        var left = Movie(world, "Left out");
        Movie(world, "Answered");
        source.Omit = id => id == left.Id;

        var refused = await Assert.ThrowsAsync<SmartSearchRefusedException>(() => harness.Search(harness.Read(Q("""{"Field":"TestFact","Op":"Ge","Value":0}""")), world.Admin));

        Assert.Equal(SmartSearchRefusedException.FieldUnavailableCode, refused.Code);
    }

    // ------------------------------------------------------------------------------------------------ pages

    [Fact]
    public async Task AnItemRemovedAfterEvaluation_LeavesItsPageShort_AndSaysSo()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        var rows = Enumerable.Range(0, 6).Select(i => Movie(world, "Film " + i)).ToList();
        var query = harness.Read(Q(InMemory));

        var first = await harness.Search(query, world.Admin, 0, 3);
        world.Core.Remove(rows[4]);
        var second = await harness.Search(query, world.Admin, 3, 3, first.ResultToken);

        Assert.Equal(2, second.Items.Count);
        Assert.Equal(1, second.Dropped);
        Assert.Equal([SmartSearchEngine.ItemsChangedWarning], second.Warnings);
        Assert.False(second.HasMore);
    }

    [Theory]
    [InlineData(Exact, "Fast")]
    [InlineData(InMemory, "Residual")]
    public async Task AQuerysLimit_CapsItsPagesAndCount(string rule, string path)
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        for (var i = 0; i < 12; i++)
        {
            Movie(world, "Film " + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
        }

        var run = await harness.SearchAll(harness.Read(Q(rule, extra: ",\"Limit\":5")), world.Admin, pageSize: 3);

        Assert.Equal(path, run.Path.ToString());
        Assert.Equal(5, run.Ids.Count);
        Assert.Equal(5, run.Total);
        Assert.Equal(2, run.Pages.Count);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => "Film 0" + i), run.Ids.Select(id => world.Core.RowById(id)!.Item.Name));
    }

    [Fact]
    public async Task APageWithoutTheTotal_DoesntCountIt()
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        Movie(world, "Film");
        var paged = new List<bool>();
        var counted = 0;
        world.Core.OnQuery = q =>
        {
            if (q.Limit is not null)
            {
                paged.Add(q.EnableTotalRecordCount);
            }
        };
        world.Core.OnItemsResult = (_, _) => counted++;

        var page = await harness.Engine.SearchAsync(harness.Read(Q(Exact)), world.Admin, new PageRequest(0, 10, null, false), TestContext.Current.CancellationToken);

        Assert.Null(page.TotalRecordCount);
        Assert.Single(page.Items);
        Assert.Equal([false], paged);
        Assert.Equal(0, counted);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    [InlineData(0, 201)]
    public async Task APageOutOfRange_IsRefused(int startIndex, int limit)
    {
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world);
        var refused = await Assert.ThrowsAsync<PluginException>(() => harness.Search(harness.Read(Q(Exact)), world.Admin, startIndex, limit));
        Assert.Equal(PluginErrorKind.Invalid, refused.Kind);
        Assert.Empty(world.Core.Queries);
    }

    private sealed class FactsSource : IItemFactsSource
    {
        public List<Guid> Loaded { get; } = [];

        public List<int> Chunks { get; } = [];

        public Func<Guid, bool> Omit { get; set; } = _ => false;

        public bool Answers(string fieldId) => fieldId == TestFacts.FieldId;

        public IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, object?>> Load(IReadOnlyList<BaseItem> items, IReadOnlyCollection<string> fieldIds, Guid? viewerId, CancellationToken cancellationToken)
        {
            Chunks.Add(items.Count);
            Loaded.AddRange(items.Select(i => i.Id));
            return items.Where(i => !Omit(i.Id)).ToDictionary(i => i.Id, TestFacts.For);
        }
    }
}
