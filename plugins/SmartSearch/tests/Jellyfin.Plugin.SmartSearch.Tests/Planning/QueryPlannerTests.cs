using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Planning;

/// <summary>
/// The planner's tests: mandatory detection, each mapper's property and flag, the "property
/// already set" rule, explicit <c>TopParentIds</c>, the empty short-circuit, <c>DtoOptions</c>, the path, and the
/// invariants on every query the planner builds. Where a test runs a plan it also checks the result against the full
/// in-memory evaluation over <see cref="FakeCore"/> (<see cref="PlannerWorld.RunAndCheck"/>).
/// </summary>
public sealed class QueryPlannerTests
{
    private const string AllKinds = "\"Movie\",\"Series\",\"Episode\",\"MusicVideo\",\"Video\",\"BoxSet\"";

    // ------------------------------------------------------------------------------------------------ structure

    [Fact]
    public void Mandatory_IsTheAllChainFromTheRoot_NeverUnderAnyOrNegate()
    {
        var world = new PlannerWorld();
        var plan = world.Plan(Q("""
            {"Field":"Year","Op":"Eq","Value":2000},
            {"Match":"All","Children":[{"Field":"Locked","Op":"Is","Value":true}]},
            {"Match":"Any","Children":[{"Field":"Genres","Op":"AnyOf","Values":["Drama"]},{"Field":"Tags","Op":"AnyOf","Values":["x"]}]},
            {"Negate":true,"Children":[{"Field":"OfficialRating","Op":"AnyOf","Values":["R"]}]}
            """));

        var steps = plan.Steps.ToDictionary(s => s.Path);
        Assert.True(steps["Where.Children[0]"].Mandatory);
        Assert.True(steps["Where.Children[0]"].LeftResidual);
        Assert.True(steps["Where.Children[1].Children[0]"].Mandatory);
        Assert.True(steps["Where.Children[1].Children[0]"].LeftResidual);
        Assert.False(steps["Where.Children[2].Children[0]"].Mandatory);
        Assert.False(steps["Where.Children[2].Children[1]"].Mandatory);
        Assert.False(steps["Where.Children[3].Children[0]"].Mandatory);
        Assert.Equal(PushdownExactness.Declined, steps["Where.Children[3].Children[0]"].Exactness);
        Assert.Equal("under a negated group", steps["Where.Children[3].Children[0]"].Reason);

        var q = plan.CandidateQuery!;
        Assert.Equal([2000], q.Years);
        Assert.True(q.IsLocked);
        Assert.Empty(q.Genres);
        Assert.Empty(q.Tags);
        Assert.Empty(q.OfficialRatings);
        Assert.Equal(QueryPlanPath.Residual, plan.Path);

        // The implied rules and the All group they leave empty are gone; the Any and the negated group stay.
        Assert.Equal(2, plan.Residual.Children!.Count);
        Assert.Equal(Jellyfin.Plugin.SmartSearch.Api.Models.SmartSearchMatchMode.Any, plan.Residual.Children[0].Match);
        Assert.True(plan.Residual.Children[1].Negate);
    }

    [Fact]
    public void ARule_LeavesTheResidual_OnlyWhenTheFinalQueryImpliesIt()
    {
        var world = new PlannerWorld();
        world.Add(BaseItemKind.Movie, "A", world.Movies, s => { s.Year = 2005; s.IsLocked = true; });
        world.Add(BaseItemKind.Movie, "B", world.Movies, s => s.Year = 2005);

        // Two Year rules intersect, and the intersection implies both.
        var years = world.Plan(Q("""{"Field":"Year","Op":"Ge","Value":2000},{"Field":"Year","Op":"Le","Value":2010}"""), world.Admin);
        Assert.Equal(Enumerable.Range(2000, 11), years.CandidateQuery!.Years);
        Assert.All(years.Steps, s => Assert.True(s.LeftResidual));
        Assert.Equal(QueryPlanPath.Fast, years.Path);

        // Two values of one property: the first is pushed and leaves, the second stays and decides in memory.
        var locked = world.Plan(Q("""{"Field":"Locked","Op":"Is","Value":true},{"Field":"Locked","Op":"Is","Value":false}"""));
        Assert.True(locked.CandidateQuery!.IsLocked);
        Assert.True(locked.Steps[0].LeftResidual);
        Assert.False(locked.Steps[1].LeftResidual);
        Assert.Empty(world.RunAndCheck(Q("""{"Field":"Locked","Op":"Is","Value":true},{"Field":"Locked","Op":"Is","Value":false}""")));

        // The base query already sets the kinds and the folders: an ItemType or Library rule the base implies leaves.
        var kinds = world.Plan(Q($$"""{"Field":"ItemType","Op":"AnyOf","Values":["Movie","Series"]},{"Field":"Library","Op":"AnyOf","Values":["{{world.Movies.Id}}"]}"""));
        Assert.Equal([BaseItemKind.Movie], kinds.CandidateQuery!.IncludeItemTypes);
        Assert.Equal(new HashSet<Guid> { world.FolderA, world.FolderB }, kinds.CandidateQuery.TopParentIds.ToHashSet());
        Assert.All(kinds.Steps, s => Assert.True(s.LeftResidual));
    }

    [Fact]
    public void ScopeOnly_PushesNoRule_TakesTheResidualPath_AndFindsTheSame()
    {
        var world = new PlannerWorld();
        world.Add(BaseItemKind.Movie, "A", world.Movies, s => { s.Year = 2005; s.IsLocked = true; });
        world.Add(BaseItemKind.Movie, "B", world.Movies, s => s.Year = 2005);
        world.Add(BaseItemKind.Movie, "C", world.Private, s => s.Year = 2004);
        var json = Q("""{"Field":"Year","Op":"Ge","Value":2005},{"Match":"Any","Children":[{"Field":"Locked","Op":"Is","Value":true},{"Field":"Genres","Op":"IsEmpty"}]}""");
        var query = PlanRunner.Read(json);
        foreach (var user in new[] { world.Admin, world.Kid })
        {
            var usual = world.Planner.Plan(new PlanRequest(query, user), world.Environment);
            var scoped = world.Planner.Plan(new PlanRequest(query, user, PlanPurpose.Search, ScopeOnly: true), world.Environment);

            Assert.Equal(QueryPlanPath.Residual, scoped.Path);
            Assert.All(scoped.Steps, s => Assert.Equal(PushdownExactness.Declined, s.Exactness));
            Assert.All(scoped.Steps, s => Assert.False(s.LeftResidual));
            Assert.Empty(scoped.CandidateQuery!.Years);
            Assert.Equal(usual.CandidateQuery!.IncludeItemTypes, scoped.CandidateQuery.IncludeItemTypes);
            Assert.Equal(usual.CandidateQuery.TopParentIds.ToHashSet(), scoped.CandidateQuery.TopParentIds.ToHashSet());
            Assert.Equal(usual.CandidateQuery.IsVirtualItem, scoped.CandidateQuery.IsVirtualItem);
            Assert.Equal(PlanRunner.Truth(query, world.Core, user, world.Environment), PlanRunner.Run(scoped, world.Core, user));
            Assert.Equal(PlanRunner.Run(usual, world.Core, user), PlanRunner.Run(scoped, world.Core, user));
        }
    }

    [Fact]
    public void TopParentIds_AreThePhysicalFolders_NeverALibrarysOwnId()
    {
        var world = new PlannerWorld();
        var admin = world.Plan(Q("""{"Field":"Year","Op":"Eq","Value":2000}""", AllKinds), world.Admin).CandidateQuery!;
        var expected = new[] { world.Movies, world.Shows, world.Home, world.Collections, world.Private, world.Hidden }.SelectMany(l => l.PhysicalFolderIds).ToHashSet();
        Assert.Equal(expected, admin.TopParentIds.ToHashSet());
        Assert.Equal(admin.TopParentIds.Length, admin.TopParentIds.Distinct().Count());
        Assert.DoesNotContain(admin.TopParentIds, id => world.Core.Libraries.Any(l => l.Id == id));

        // Kid can't open Private or Hidden; Hidden's folder is Movies' first, which Kid sees through Movies.
        var kid = world.Plan(Q("""{"Field":"Year","Op":"Eq","Value":2000}"""), world.Kid).CandidateQuery!;
        Assert.Equal(new HashSet<Guid> { world.FolderA, world.FolderB, world.Shows.PhysicalFolderIds[0], world.Home.PhysicalFolderIds[0] }, kid.TopParentIds.ToHashSet());

        // A boxsets library only when BoxSet is a kind.
        Assert.DoesNotContain(world.Collections.PhysicalFolderIds[0], kid.TopParentIds);
    }

    [Theory]
    [InlineData("private")]
    [InlineData("collections")]
    [InlineData("impossible")]
    [InlineData("hiddenRule")]
    public void AnEmptyPlan_RunsNoQuery(string which)
    {
        var world = new PlannerWorld();
        world.Add(BaseItemKind.Movie, "A", world.Movies, s => s.Year = 2000);
        var json = which switch
        {
            "private" => Q("""{"Field":"Year","Op":"Eq","Value":2000}""", libraries: [world.Private.Id]),
            "collections" => Q("""{"Field":"Year","Op":"Eq","Value":2000}""", libraries: [world.Collections.Id]),
            "impossible" => Q("""{"Field":"Year","Op":"Eq","Value":2000},{"Field":"Year","Op":"Eq","Value":2001}"""),
            _ => Q($$"""{"Field":"Library","Op":"AnyOf","Values":["{{world.Hidden.Id}}"]}""")
        };

        var plan = world.Plan(json, world.Kid);
        Assert.Equal(QueryPlanPath.Empty, plan.Path);
        Assert.Null(plan.CandidateQuery);
        Assert.Empty(PlanRunner.Run(plan, world.Core, world.Kid));
        Assert.Empty(world.Core.Queries);
        Assert.Throws<InvalidOperationException>(() => plan.CreateHydrationQuery([Guid.NewGuid()]));
        Assert.Empty(PlanRunner.Truth(PlanRunner.Read(json), world.Core, world.Kid, world.Environment));
    }

    [Fact]
    public void DtoOptions_AskForUserDataAndProviderIds_OnlyWhenTheResidualNeedsThem()
    {
        var world = new PlannerWorld();
        var plain = world.Plan(Q("""{"Field":"Year","Op":"Eq","Value":2000}"""), world.Admin);
        Assert.False(plain.DtoOptions.EnableImages);
        Assert.False(plain.NeedsUserData);
        Assert.False(plain.NeedsProviderIds);

        Assert.True(world.Plan(Q("""{"Field":"Played","Op":"Is","Value":false}"""), world.Admin).NeedsUserData);
        Assert.True(world.Plan(Q("""{"Field":"PlayCount","Op":"Ge","Value":2}"""), world.Admin).NeedsUserData);
        Assert.True(world.Plan(Q("""{"Field":"ProviderId","Op":"Eq","Value":"tt1","Provider":"Imdb"}"""), world.Admin).NeedsProviderIds);
        Assert.True(world.Plan(Q("""{"Field":"HasProviderId","Op":"Is","Value":false,"Provider":"Imdb"}"""), world.Admin).NeedsProviderIds);

        // A per-user sort key asks for user data when the residual path sorts in memory, not when core sorts (Fast).
        Assert.True(world.Plan(Q("""{"Field":"Overview","Op":"Contains","Value":"x"}""", extra: ""","Sort":[{"Key":"DatePlayed","Order":"Descending"}]"""), world.Admin).NeedsUserData);
        var fast = world.Plan(Q("""{"Field":"Year","Op":"Eq","Value":2000}""", extra: ""","Sort":[{"Key":"DatePlayed","Order":"Descending"}]"""), world.Admin);
        Assert.Equal(QueryPlanPath.Fast, fast.Path);
        Assert.False(fast.NeedsUserData);
        Assert.Equal([(ItemSortBy.DatePlayed, SortOrder.Descending)], fast.OrderBy);
    }

    [Fact]
    public void Path_IsFastOnlyForAWholePushdownCoreSortsAndOnePageWindow_AndNeverForEvaluateAll()
    {
        var world = new PlannerWorld();
        const string Exact = """{"Field":"Year","Op":"Eq","Value":2000}""";
        var fast = world.Plan(Q(Exact, extra: ""","Limit":200"""), world.Admin);
        Assert.Equal(QueryPlanPath.Fast, fast.Path);
        Assert.True(fast.CandidateQuery!.GroupByPresentationUniqueKey);
        Assert.Equal([(ItemSortBy.SortName, SortOrder.Ascending)], fast.OrderBy);
        Assert.True(fast.ResidualIsEmpty);

        Assert.Equal(QueryPlanPath.Residual, world.Plan(Q(Exact, extra: ""","Limit":201"""), world.Admin).Path);
        Assert.Equal(QueryPlanPath.Residual, world.Plan(Q(Exact, extra: ""","Sort":[{"Key":"Random","Order":"Ascending"}]"""), world.Admin).Path);
        Assert.Equal(QueryPlanPath.Residual, world.Plan(Q(Exact, extra: ""","Sort":[{"Key":"FileSize","Order":"Ascending"}]"""), world.Admin).Path);
        Assert.Equal(QueryPlanPath.Residual, world.Plan(Q("""{"Field":"Overview","Op":"Contains","Value":"x"}"""), world.Admin).Path);

        var all = world.Plan(Q(Exact), world.Admin, PlanPurpose.EvaluateAll);
        Assert.Equal(QueryPlanPath.Residual, all.Path);
        Assert.False(all.CandidateQuery!.GroupByPresentationUniqueKey);

        // Core orders by DatePlayed or PlayCount through query.User!.Id (OrderMapper.cs:33-34), which throws without a
        // user (found by the live check): with no user every plan takes the Residual path, which sorts in memory.
        foreach (var key in new[] { "DatePlayed", "PlayCount" })
        {
            var sorted = Q(Exact, extra: $$""","Sort":[{"Key":"SortName","Order":"Ascending"},{"Key":"{{key}}","Order":"Descending"}]""");
            Assert.Equal(QueryPlanPath.Fast, world.Plan(sorted, world.Admin).Path);
            var anonymous = world.Plan(sorted);
            Assert.Equal(QueryPlanPath.Residual, anonymous.Path);
            Assert.Empty(anonymous.OrderBy);
            world.RunAndCheck(sorted);
        }
    }

    [Fact]
    public void Path_IsNeverFast_WithoutAUser_OrWhenTheFinalKindsIncludeBoxSet()
    {
        var world = new PlannerWorld();
        const string Exact = """{"Field":"Year","Op":"Eq","Value":2000}""";

        // Without a user core doesn't group merged versions (BaseItemRepository.cs:1527): a Fast page would list each one.
        Assert.Equal(QueryPlanPath.Fast, world.Plan(Q(Exact), world.Kid).Path);
        var anonymous = world.Plan(Q(Exact));
        Assert.Equal(QueryPlanPath.Residual, anonymous.Path);
        Assert.False(anonymous.CandidateQuery!.GroupByPresentationUniqueKey);

        // BoxSet's IsVisible checks the user's libraries against its children's, which SQL doesn't.
        foreach (var user in new[] { world.Kid, world.Admin })
        {
            Assert.Equal(QueryPlanPath.Residual, world.Plan(Q("""{"Field":"Locked","Op":"Is","Value":true}""", "\"BoxSet\""), user).Path);
            Assert.Equal(QueryPlanPath.Residual, world.Plan(Q("""{"Field":"Title","Op":"Eq","Value":"Box"}""", "\"BoxSet\""), user).Path);
            Assert.Equal(QueryPlanPath.Residual, world.Plan(Q("""{"Field":"ItemType","Op":"AnyOf","Values":["BoxSet"]}""", "\"Movie\",\"BoxSet\""), user).Path);
            Assert.Equal(QueryPlanPath.Residual, world.Plan(Q("""{"Field":"ItemType","Op":"NoneOf","Values":["Series"]}""", "\"Movie\",\"BoxSet\""), user).Path);

            // A mandatory ItemType rule that removes BoxSet makes the plan Fast again.
            Assert.Equal(QueryPlanPath.Fast, world.Plan(Q("""{"Field":"ItemType","Op":"NoneOf","Values":["BoxSet"]}""", "\"Movie\",\"BoxSet\""), user).Path);
            Assert.Equal(QueryPlanPath.Fast, world.Plan(Q("""{"Field":"ItemType","Op":"AnyOf","Values":["Movie"]}""", "\"Movie\",\"BoxSet\""), user).Path);
        }
    }

    [Fact]
    public void TheFake_ThrowsLikeCore_ForAPerUserOrderWithoutAUser()
    {
        var world = new PlannerWorld();
        var query = new InternalItemsQuery { OrderBy = [(ItemSortBy.PlayCount, SortOrder.Ascending)] };
        Assert.Throws<InvalidOperationException>(() => world.Core.Library.Object.GetItemList(query));
        query.User = world.Admin;
        Assert.Empty(world.Core.Library.Object.GetItemList(query));
    }

    [Fact]
    public void EveryQuery_HasTopParentIds_NoOwnerFilter_AndGroupsOnlyOnTheFastPath()
    {
        var world = new PlannerWorld();
        Populate(world);
        foreach (var user in new[] { world.Admin, world.Kid, null })
        {
            foreach (var json in Battery(world))
            {
                world.RunAndCheck(json, user);
                world.RunAndCheck(json, user, PlanPurpose.EvaluateAll);
            }
        }

        var fastQueries = world.Plans.Where(p => p.Path == QueryPlanPath.Fast).Select(p => p.CandidateQuery!).ToHashSet(ReferenceEqualityComparer.Instance);
        Assert.NotEmpty(world.Core.Queries);
        Assert.Contains(world.Plans, p => p.Path == QueryPlanPath.Residual);
        Assert.Contains(world.Plans, p => p.Path == QueryPlanPath.Empty);
        Assert.NotEmpty(fastQueries);
        foreach (var q in world.Core.Queries)
        {
            Assert.Null(q.HasOwnerId);
            Assert.NotEmpty(q.TopParentIds);
            Assert.DoesNotContain(Guid.Empty, q.TopParentIds);
            Assert.Equal(fastQueries.Contains(q), q.GroupByPresentationUniqueKey);
            Assert.False(q.DtoOptions.EnableImages);
        }
    }

    // ------------------------------------------------------------------------------------------------ mappers

    [Theory]
    [InlineData("""{"Field":"Year","Op":"Ge","Value":2090}""", 2090, 2100)]
    [InlineData("""{"Field":"Year","Op":"Gt","Value":2099}""", 2100, 2100)]
    [InlineData("""{"Field":"Year","Op":"Le","Value":1810}""", 1800, 1810)]
    [InlineData("""{"Field":"Year","Op":"Lt","Value":1801}""", 1800, 1800)]
    [InlineData("""{"Field":"Year","Op":"IsNotEmpty"}""", 1800, 2100)]
    [InlineData("""{"Field":"Year","Op":"Between","From":1990,"To":1999}""", 1990, 1999)]
    public void Year_OpenEnds_EnumerateExactly1800To2100(string rule, int from, int to)
    {
        var world = new PlannerWorld();
        foreach (var year in new[] { 0, 1799, 1800, 1810, 1995, 2090, 2100, 2101, 9999 })
        {
            world.Add(BaseItemKind.Movie, "Y" + year, world.Movies, s => s.Year = year);
        }

        world.Add(BaseItemKind.Movie, "None", world.Movies);
        var plan = world.Plan(Q(rule), world.Admin);
        Assert.Equal(Enumerable.Range(from, to - from + 1), plan.CandidateQuery!.Years);
        Assert.Equal(PushdownExactness.Exact, plan.Steps[0].Exactness);
        Assert.Equal(QueryPlanPath.Fast, plan.Path);
        world.RunAndCheck(Q(rule));
        world.RunAndCheck(Q(rule), world.Admin);
    }

    [Theory]
    [InlineData("""{"Field":"Title","Op":"Contains","Value":"AMÉLIE"}""", "amelie")]
    [InlineData("""{"Field":"Title","Op":"Contains","Value":"50%"}""", "50%", "50")]
    [InlineData("""{"Field":"Title","Op":"Contains","Value":"[x]"}""", "[x]", "x")]
    [InlineData("""{"Field":"Title","Op":"Contains","Value":"Brød"}""", "brod")]
    [InlineData("""{"Field":"Title","Op":"Contains","Value":"%_"}""", null)]
    [InlineData("""{"Field":"Title","Op":"Contains","Value":"amelie","IncludeOriginalTitle":true}""", null)]
    [InlineData("""{"Field":"Title","Op":"StartsWith","Value":"amelie"}""", null)]
    [InlineData("""{"Field":"Title","Op":"NotContains","Value":"amelie"}""", null)]
    public void Title_Contains_PushesTheCleanTerm(string rule, string? expected, string? expectedOn12 = null)
    {
        // Jellyfin 12's clean form turns punctuation into spaces and trims ("[x]" is "x").
        expected = JellyfinLine.Is12 && expectedOn12 is not null ? expectedOn12 : expected;
        var world = new PlannerWorld();
        foreach (var (name, original) in new[] { ("Amélie", "Le Fabuleux Destin"), ("AMELIE 2", null), ("50% Off", null), ("5000 Miles", "50 Prozent"), ("[x] Files", null), ("x Files", "ÉMILIE"), ("Brød", null), ("Brod", null), ("Ærø", null), ("_", null) })
        {
            world.Add(BaseItemKind.Movie, name, world.Movies, s => s.OriginalTitle = original);
        }

        var plan = world.Plan(Q(rule));
        Assert.Equal(expected, plan.CandidateQuery!.NameContains);
        Assert.Null(plan.CandidateQuery.Name);
        Assert.NotEqual(PushdownExactness.Exact, plan.Steps[0].Exactness);
        Assert.False(plan.Steps[0].LeftResidual);
        world.RunAndCheck(Q(rule));
    }

    [Fact]
    public void Title_Eq_PushesName_Exact()
    {
        var world = new PlannerWorld();
        world.Add(BaseItemKind.Movie, "Amélie", world.Movies);
        world.Add(BaseItemKind.Movie, "Amelie (2001)", world.Movies);
        var plan = world.Plan(Q("""{"Field":"Title","Op":"Eq","Value":" AMÉLIE "}"""), world.Kid);
        Assert.Equal("AMÉLIE", plan.CandidateQuery!.Name);
        Assert.Equal(PushdownExactness.Exact, plan.Steps[0].Exactness);
        Assert.Equal(QueryPlanPath.Fast, plan.Path);
        Assert.Single(world.RunAndCheck(Q("""{"Field":"Title","Op":"Eq","Value":" AMÉLIE "}""")));
    }

    [Theory]
    [InlineData("""{"Field":"PremiereDate","Op":"After","Value":"2026-01-31"}""", "2026-01-31T00:00:00Z", null)]
    [InlineData("""{"Field":"PremiereDate","Op":"Before","Value":"2026-02-01"}""", null, "2026-02-02T00:00:00Z")]
    [InlineData("""{"Field":"PremiereDate","Op":"Between","From":"2026-01-01","To":"2026-01-31"}""", "2025-12-31T00:00:00Z", "2026-02-02T00:00:00Z")]
    [InlineData("""{"Field":"PremiereDate","Op":"After","Value":"2026-01-31T12:00:00Z"}""", "2026-01-30T12:00:00Z", null)]
    [InlineData("""{"Field":"PremiereDate","Op":"Before","Value":"2026-02-01T00:00:00+10:00"}""", null, "2026-02-01T14:00:00Z")]
    [InlineData("""{"Field":"PremiereDate","Op":"InLast","Amount":30,"Unit":"Days"}""", "2026-10-02T12:00:00Z", null)]
    public void PremiereDate_IsWidenedADayEachWay_AsUtc(string rule, string? min, string? max)
    {
        foreach (var zone in new[] { PlannerWorld.West, TimeZoneInfo.Utc, PlannerWorld.East })
        {
            var world = new PlannerWorld(zone);
            AddDatedRows(world);
            var q = world.Plan(Q(rule, AllKinds)).CandidateQuery!;
            AssertUtc(min, q.MinPremiereDate);
            AssertUtc(max, q.MaxPremiereDate);
            world.RunAndCheck(Q(rule, AllKinds));
            world.RunAndCheck(Q(rule, "\"Movie\",\"Episode\""), world.Admin);
        }
    }

    [Theory]
    [InlineData("""{"Field":"DateAdded","Op":"After","Value":"2026-01-31"}""", "2026-01-31T00:00:00Z")]
    [InlineData("""{"Field":"DateAdded","Op":"Between","From":"2026-01-01","To":"2026-01-31"}""", "2025-12-31T00:00:00Z")]
    [InlineData("""{"Field":"DateAdded","Op":"InLast","Amount":1,"Unit":"Months"}""", "2026-10-01T12:00:00Z")]
    [InlineData("""{"Field":"DateAdded","Op":"Before","Value":"2026-02-01"}""", null)]
    public void DateAdded_PushesOnlyALowerBound(string rule, string? min)
    {
        foreach (var zone in new[] { PlannerWorld.West, TimeZoneInfo.Utc, PlannerWorld.East })
        {
            var world = new PlannerWorld(zone);
            AddDatedRows(world);
            AssertUtc(min, world.Plan(Q(rule, AllKinds)).CandidateQuery!.MinDateCreated);
            world.RunAndCheck(Q(rule, AllKinds));
        }
    }

    [Theory]
    [InlineData("""{"Field":"CommunityRating","Op":"Ge","Value":7.1}""", 7.1)]
    [InlineData("""{"Field":"CommunityRating","Op":"Gt","Value":7.1}""", 7.1)]
    [InlineData("""{"Field":"CommunityRating","Op":"Eq","Value":7.1}""", 7.1)]
    [InlineData("""{"Field":"CommunityRating","Op":"Between","From":6.9,"To":7.1}""", 6.9)]
    [InlineData("""{"Field":"CommunityRating","Op":"Le","Value":7.1}""", null)]
    public void CommunityRating_IsAFloatStepLower(string rule, double? value)
    {
        var world = new PlannerWorld();
        foreach (var rating in new[] { 7.1f, MathF.BitDecrement(7.1f), MathF.BitIncrement(7.1f), 6.9f, MathF.BitDecrement(6.9f), 7f, 10f, 0f })
        {
            world.Add(BaseItemKind.Movie, "R" + rating.ToString("R", System.Globalization.CultureInfo.InvariantCulture), world.Movies, s => s.CommunityRating = rating);
        }

        world.Add(BaseItemKind.Movie, "Unrated", world.Movies);
        var plan = world.Plan(Q(rule));
        Assert.Equal(value is { } v ? (double)MathF.BitDecrement((float)v) : (double?)null, plan.CandidateQuery!.MinCommunityRating);
        Assert.NotEqual(PushdownExactness.Exact, plan.Steps[0].Exactness);
        world.RunAndCheck(Q(rule));
    }

    [Theory]
    [InlineData("""{"Field":"Resolution","Op":"AnyOf","Values":["720p","2160p"]}""", 1200, null)]
    [InlineData("""{"Field":"Resolution","Op":"AnyOf","Values":["SD","720p"]}""", 1, 1899)]
    [InlineData("""{"Field":"Resolution","Op":"AnyOf","Values":["1080p"]}""", 1900, 2499)]
    [InlineData("""{"Field":"Resolution","Op":"AnyOf","Values":["Unknown","SD"]}""", null, null)]
    [InlineData("""{"Field":"Resolution","Op":"NoneOf","Values":["SD"]}""", null, null)]
    public void Resolution_PushesTheWidthHull(string rule, int? min, int? max)
    {
        var world = new PlannerWorld();
        foreach (var width in new[] { 0, 1, 720, 1199, 1200, 1280, 1899, 1900, 1920, 2499, 2560, 3799, 3800, 3840, 7680 })
        {
            world.Add(BaseItemKind.Movie, "W" + width, world.Movies, s => s.Width = width);
        }

        var plan = world.Plan(Q(rule));
        Assert.Equal(min, plan.CandidateQuery!.MinWidth);

        // Jellyfin 12 drops an item from MaxWidth when any of its versions is wider: only the lower bound is pushed.
        Assert.Equal(JellyfinLine.Is12 ? null : max, plan.CandidateQuery.MaxWidth);
        Assert.Null(plan.CandidateQuery.IsHD);
        Assert.Null(plan.CandidateQuery.Is4K);
        world.RunAndCheck(Q(rule));
    }

    [Fact]
    public void Choices_ItemTypeOfficialRatingLocked_AreExact()
    {
        var world = new PlannerWorld();
        Populate(world);
        var noneOf = world.Plan(Q("""{"Field":"ItemType","Op":"NoneOf","Values":["Movie","BoxSet"]}""", AllKinds)).CandidateQuery!;
        Assert.Equal([BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video], noneOf.IncludeItemTypes);

        var rating = world.Plan(Q("""{"Field":"OfficialRating","Op":"AnyOf","Values":["PG-13","R"]}"""));
        Assert.Equal(["PG-13", "R"], rating.CandidateQuery!.OfficialRatings);
        Assert.Equal(PushdownExactness.Exact, rating.Steps[0].Exactness);
        Assert.Empty(world.Plan(Q("""{"Field":"OfficialRating","Op":"Le","Value":"PG-13"}""")).CandidateQuery!.OfficialRatings);

        Assert.False(world.Plan(Q("""{"Field":"Locked","Op":"Is","Value":false}""")).CandidateQuery!.IsLocked);
        foreach (var json in new[]
        {
            Q("""{"Field":"ItemType","Op":"NoneOf","Values":["Movie","BoxSet"]}""", AllKinds),
            Q("""{"Field":"ItemType","Op":"AnyOf","Values":["Episode"]}""", AllKinds),
            Q("""{"Field":"OfficialRating","Op":"AnyOf","Values":["PG-13","R"]}""", AllKinds),
            Q("""{"Field":"Locked","Op":"Is","Value":false}""", AllKinds)
        })
        {
            world.RunAndCheck(json);
            world.RunAndCheck(json, world.Kid);
        }
    }

    [Fact]
    public void Lists_GenresTagsStudios_FollowTheirValueRows()
    {
        var world = new PlannerWorld();
        var a24 = world.Core.AddStudio("A24");
        world.Add(BaseItemKind.Movie, "Drama", world.Movies, s => { s.Genres = ["Drama", "Crime"]; s.Tags = ["Keep"]; s.Studios = ["A24"]; });
        world.Add(BaseItemKind.Movie, "Comedy", world.Movies, s => { s.Genres = ["Comedy"]; s.Tags = ["Drop", "keep"]; s.Studios = ["Neon"]; });
        world.Add(BaseItemKind.Movie, "Bare", world.Movies);

        var anyOf = world.Plan(Q("""{"Field":"Genres","Op":"AnyOf","Values":["Drama","Comedy"]}"""));
        Assert.Equal(["Drama", "Comedy"], anyOf.CandidateQuery!.Genres);
        Assert.Equal(PushdownExactness.Exact, anyOf.Steps[0].Exactness);

        var allOf = world.Plan(Q("""{"Field":"Genres","Op":"AllOf","Values":["Drama","Crime"]}"""));
        Assert.Equal(["Drama"], allOf.CandidateQuery!.Genres);
        Assert.Equal(PushdownExactness.Superset, allOf.Steps[0].Exactness);

        var noneOf = world.Plan(Q("""{"Field":"Tags","Op":"NoneOf","Values":["drop"]}"""));
        Assert.Equal(["drop"], noneOf.CandidateQuery!.ExcludeTags);
        Assert.Empty(world.Plan(Q("""{"Field":"Genres","Op":"NoneOf","Values":["Drama"]}""")).CandidateQuery!.Genres);

        var studios = world.Plan(Q("""{"Field":"Studios","Op":"AnyOf","Values":["a24"]}"""));
        Assert.Equal([a24.Id], studios.CandidateQuery!.StudioIds);
        Assert.Equal(PushdownExactness.Exact, studios.Steps[0].Exactness);

        foreach (var rule in new[]
        {
            """{"Field":"Genres","Op":"AnyOf","Values":["Drama","Comedy"]}""", """{"Field":"Genres","Op":"AllOf","Values":["Drama","Crime"]}""",
            """{"Field":"Genres","Op":"NoneOf","Values":["Drama"]}""", """{"Field":"Tags","Op":"NoneOf","Values":["drop"]}""",
            """{"Field":"Tags","Op":"AnyOf","Values":["KEEP"]}""", """{"Field":"Studios","Op":"AnyOf","Values":["a24"]}""",
            """{"Field":"Genres","Op":"IsEmpty"}"""
        })
        {
            world.RunAndCheck(Q(rule));
        }
    }

    [Fact]
    public void Studios_ArePushedOnlyWhenEveryNameHasAStudioItem_AndGetStudioIsNeverCalled()
    {
        var world = new PlannerWorld();
        world.Core.AddStudio("A24");
        world.Core.AddStudio("Ghibli", storedCleanName: "ghibli (old)");
        world.Add(BaseItemKind.Movie, "One", world.Movies, s => s.Studios = ["A24"]);
        world.Add(BaseItemKind.Movie, "Two", world.Movies, s => s.Studios = ["Neon"]);
        world.Add(BaseItemKind.Movie, "Three", world.Movies, s => s.Studios = ["Ghibli"]);

        var unresolved = world.Plan(Q("""{"Field":"Studios","Op":"AnyOf","Values":["A24","Neon"]}"""));
        Assert.Empty(unresolved.CandidateQuery!.StudioIds);
        Assert.Equal(PushdownExactness.Declined, unresolved.Steps[0].Exactness);
        Assert.Equal(QueryPlanPath.Residual, unresolved.Path);
        Assert.Equal(2, world.RunAndCheck(Q("""{"Field":"Studios","Op":"AnyOf","Values":["A24","Neon"]}""")).Count);

        var stale = world.Plan(Q("""{"Field":"Studios","Op":"AnyOf","Values":["Ghibli"]}"""));
        Assert.Empty(stale.CandidateQuery!.StudioIds);
        Assert.Single(world.RunAndCheck(Q("""{"Field":"Studios","Op":"AnyOf","Values":["Ghibli"]}""")));

        world.Core.Library.Verify(l => l.GetStudioId("Neon"), Times.AtLeastOnce());

        // The mock is Strict: GetStudio (which creates a Studio item) or any other member would have thrown.
        Assert.DoesNotContain(world.Core.Library.Invocations, i => i.Method.Name == "GetStudio");
    }

    [Fact]
    public void Studios_AreNotPushed_WhenTheNamesIdIsAnotherStudiosItem()
    {
        // Core's studio id is its path's (Studio.GetPath: a character a file name can't hold becomes a space), so "AC/DC"
        // has the id of the Studio item "AC DC"; StudioIds would then match "AC DC" and lose "AC/DC".
        var world = new PlannerWorld();
        var shared = world.Core.AddStudio("AC DC");
        world.Core.StudioIds = name => name == "AC/DC" ? shared.Id : null;
        var slash = world.Add(BaseItemKind.Movie, "Slash", world.Movies, s => s.Studios = ["AC/DC"]);
        var space = world.Add(BaseItemKind.Movie, "Space", world.Movies, s => s.Studios = ["AC DC"]);

        var plan = world.Plan(Q("""{"Field":"Studios","Op":"AnyOf","Values":["AC/DC"]}"""));
#if JELLYFIN_12
        // Jellyfin 12's clean form makes "AC/DC" and "AC DC" one name, for core and the evaluator alike: pushed, both match.
        Assert.Equal([shared.Id], plan.CandidateQuery!.StudioIds);
        Assert.Equal(new[] { slash.Item.Id, space.Item.Id }.Order(), world.RunAndCheck(Q("""{"Field":"Studios","Op":"AnyOf","Values":["AC/DC"]}""")).Order());
#else
        Assert.Empty(plan.CandidateQuery!.StudioIds);
        Assert.Equal(PushdownExactness.Declined, plan.Steps[0].Exactness);
        Assert.Equal(slash.Item.Id, Assert.Single(world.RunAndCheck(Q("""{"Field":"Studios","Op":"AnyOf","Values":["AC/DC"]}"""))));
#endif

        // The Studio item's own name is pushed.
        Assert.Equal([shared.Id], world.Plan(Q("""{"Field":"Studios","Op":"AnyOf","Values":["AC DC"]}""")).CandidateQuery!.StudioIds);
        Assert.Equal(
            JellyfinLine.Is12 ? new[] { slash.Item.Id, space.Item.Id }.Order() : [space.Item.Id],
            world.RunAndCheck(Q("""{"Field":"Studios","Op":"AnyOf","Values":["AC DC"]}""")).Order());
    }

    [Fact]
    public void UserData_IsTrueIsPushed_IsFalseNever()
    {
        var world = new PlannerWorld();
        var admin = world.Admin;
        world.Add(BaseItemKind.Movie, "Fresh", world.Movies);
        world.Add(BaseItemKind.Movie, "Loved", world.Movies, s => s.UserData.Add(new FakeUserData(admin.Id, "", true, true, 100)));
        world.Add(BaseItemKind.Movie, "Seen", world.Movies, s => s.UserData.Add(new FakeUserData(admin.Id, "", true, false, 0)));
        world.Add(BaseItemKind.Movie, "Missing", world.Movies, s => { s.IsVirtual = true; s.UserData.Add(new FakeUserData(admin.Id, "", false, false, 500)); });
        world.Add(BaseItemKind.Series, "Show", world.Shows, s => s.UserData.Add(new FakeUserData(admin.Id, "", false, true, 0)));

        foreach (var (field, property) in new[] { ("Played", "IsPlayed"), ("Favorite", "IsFavorite"), ("InProgress", "IsResumable") })
        {
            var yes = world.Plan(Q($$"""{"Field":"{{field}}","Op":"Is","Value":true}"""), admin);
            Assert.True(Flag(yes.CandidateQuery!, property));
            Assert.Equal(PushdownExactness.Superset, yes.Steps[0].Exactness);
            Assert.True(yes.NeedsUserData);

            Assert.Null(Flag(world.Plan(Q($$"""{"Field":"{{field}}","Op":"Is","Value":false}"""), admin).CandidateQuery!, property));
            Assert.Null(Flag(world.Plan(Q($$"""{"Field":"{{field}}","Op":"Is","Value":true}""")).CandidateQuery!, property));
            foreach (var value in new[] { "true", "false" })
            {
                foreach (var kinds in new[] { "\"Movie\"", "\"Movie\",\"Series\"" })
                {
                    world.RunAndCheck(Q($$"""{"Field":"{{field}}","Op":"Is","Value":{{value}}}""", kinds), admin);
                    world.RunAndCheck(Q($$"""{"Field":"{{field}}","Op":"Is","Value":{{value}}}""", kinds, includeMissing: true), admin);
                }
            }
        }

        // Kinds beyond the field's: Played on a series is core's "any episode" rule, never pushed.
        Assert.Null(world.Plan(Q("""{"Field":"Played","Op":"Is","Value":true}""", "\"Movie\",\"Series\""), admin).CandidateQuery!.IsPlayed);
        Assert.True(world.Plan(Q("""{"Field":"Favorite","Op":"Is","Value":true}""", "\"Movie\",\"Series\""), admin).CandidateQuery!.IsFavorite);

        // IsResumable makes core drop missing items, so it isn't pushed when the scope includes them.
        Assert.Null(world.Plan(Q("""{"Field":"InProgress","Op":"Is","Value":true}""", includeMissing: true), admin).CandidateQuery!.IsResumable);
    }

    [Fact]
    public void UserData_IsNotPushed_WhenTheUsersRowsOfAnItemDisagree()
    {
        var world = new PlannerWorld();
        var admin = world.Admin;

        // Two rows of one item for the user: the first says played, the other not (SQL reads the first, memory may read the other).
        world.Add(BaseItemKind.Movie, "Rekeyed", world.Movies, s =>
        {
            s.UserData.Add(new FakeUserData(admin.Id, "old-key", true, true, 10));
            s.UserData.Add(new FakeUserData(admin.Id, "", false, false, 0));
        });
        var plan = world.Plan(Q("""{"Field":"Played","Op":"Is","Value":true}"""), admin);
        Assert.Null(plan.CandidateQuery!.IsPlayed);
        Assert.Equal(PushdownExactness.Declined, plan.Steps[0].Exactness);
        foreach (var field in new[] { "Played", "Favorite", "InProgress" })
        {
            world.RunAndCheck(Q($$"""{"Field":"{{field}}","Op":"Is","Value":true}"""), admin);
            world.RunAndCheck(Q($$"""{"Field":"{{field}}","Op":"Is","Value":false}"""), admin);
        }

        // Another user's rows don't matter.
        Assert.True(world.Plan(Q("""{"Field":"Played","Op":"Is","Value":true}"""), world.Kid).CandidateQuery!.IsPlayed);
    }

    [Fact]
    public void Providers_PushOnlyTheKey_AndHasProviderIdIsFalseNever()
    {
        var world = new PlannerWorld();
        world.Add(BaseItemKind.Movie, "Upper", world.Movies, s => s.Providers.Add(("IMDB", "TT0133093")));
        world.Add(BaseItemKind.Movie, "Lower", world.Movies, s => s.Providers.Add(("imdb", "tt0133093")));
        world.Add(BaseItemKind.Movie, "Blank", world.Movies, s => s.Providers.Add(("Imdb", " ")));
        world.Add(BaseItemKind.Movie, "Other", world.Movies, s => s.Providers.Add(("Tmdb", "603")));
        world.Add(BaseItemKind.Movie, "None", world.Movies);

        var has = world.Plan(Q("""{"Field":"HasProviderId","Op":"Is","Value":true,"Provider":"imdb"}"""));
        Assert.True(has.CandidateQuery!.HasImdbId);
        Assert.Equal(PushdownExactness.Superset, has.Steps[0].Exactness);
        Assert.Null(world.Plan(Q("""{"Field":"HasProviderId","Op":"Is","Value":false,"Provider":"Imdb"}""")).CandidateQuery!.HasImdbId);
        var eq = world.Plan(Q("""{"Field":"ProviderId","Op":"Eq","Value":"tt0133093","Provider":"Imdb"}"""));
        Assert.True(eq.CandidateQuery!.HasImdbId);
        Assert.True(eq.NeedsProviderIds);
        Assert.Null(eq.CandidateQuery.HasAnyProviderId);

        foreach (var rule in new[]
        {
            """{"Field":"HasProviderId","Op":"Is","Value":true,"Provider":"imdb"}""", """{"Field":"HasProviderId","Op":"Is","Value":false,"Provider":"Imdb"}""",
            """{"Field":"ProviderId","Op":"Eq","Value":"tt0133093","Provider":"Imdb"}""", """{"Field":"ProviderId","Op":"Eq","Value":"603","Provider":"TMDB"}"""
        })
        {
            world.RunAndCheck(Q(rule));
        }
    }

    [Fact]
    public void Series_AndSeriesName_SetAncestorIds()
    {
        var world = new PlannerWorld();
        var alpha = world.Add(BaseItemKind.Series, "Alpha Prime", world.Shows);
        var beta = world.Add(BaseItemKind.Series, "Beta", world.Shows);
        world.Add(BaseItemKind.Episode, "A1", world.Shows, s => { s.Series = alpha; s.SeriesName = "Alpha"; }); // stored before a rename
        world.Add(BaseItemKind.Episode, "A2", world.Shows, s => { s.Series = alpha; s.SeriesName = "Alpha Prime"; });
        world.Add(BaseItemKind.Episode, "B1", world.Shows, s => s.Series = beta);
        world.Add(BaseItemKind.Movie, "Alpha the movie", world.Movies);

        var series = world.Plan(Q($$"""{"Field":"Series","Op":"AnyOf","Values":[{"Id":"{{alpha.Id:N}}","Name":"Alpha Prime"}]}""", "\"Episode\""));
        Assert.Equal([alpha.Id], series.CandidateQuery!.AncestorIds);
        Assert.True(series.Steps[0].LeftResidual);

        var name = world.Plan(Q("""{"Field":"SeriesName","Op":"Eq","Value":"alpha"}""", "\"Episode\",\"Movie\""));
        Assert.Equal([alpha.Id], name.CandidateQuery!.AncestorIds);
        Assert.Equal(PushdownExactness.Superset, name.Steps[0].Exactness);
        Assert.False(name.Steps[0].LeftResidual);

        Assert.Equal(QueryPlanPath.Empty, world.Plan(Q("""{"Field":"SeriesName","Op":"Eq","Value":"gamma"}""", "\"Episode\"")).Path);

        foreach (var rule in new[]
        {
            $$"""{"Field":"Series","Op":"AnyOf","Values":[{"Id":"{{alpha.Id:N}}","Name":"Alpha Prime"}]}""",
            $$"""{"Field":"Series","Op":"NoneOf","Values":[{"Id":"{{alpha.Id:N}}","Name":"Alpha Prime"}]}""",
            """{"Field":"SeriesName","Op":"Eq","Value":"alpha"}""", """{"Field":"SeriesName","Op":"Contains","Value":"ALPHA"}""",
            """{"Field":"SeriesName","Op":"NotContains","Value":"alpha"}""", """{"Field":"SeriesName","Op":"Eq","Value":"gamma"}"""
        })
        {
            world.RunAndCheck(Q(rule, "\"Episode\",\"Movie\""));
            world.RunAndCheck(Q(rule, "\"Episode\""), world.Kid);
        }

        // An episode whose series id is missing but whose name matches: the name can't be turned into ids.
        world.Add(BaseItemKind.Episode, "Orphan", world.Shows, s => s.SeriesName = "Alpha");
        Assert.Empty(world.Plan(Q("""{"Field":"SeriesName","Op":"Eq","Value":"alpha"}""", "\"Episode\"")).CandidateQuery!.AncestorIds);
        world.RunAndCheck(Q("""{"Field":"SeriesName","Op":"Eq","Value":"alpha"}""", "\"Episode\""));
        world.Core.EpisodesReadable = false;
        Assert.Empty(world.Plan(Q("""{"Field":"SeriesName","Op":"Eq","Value":"beta"}""", "\"Episode\"")).CandidateQuery!.AncestorIds);
    }

    [Fact]
    public void Series_PushesOnlyIdsThatAreSeriesTheCallerSees_NeverAnotherAncestor()
    {
        // An episode's AncestorIds hold its season, series, physical folder, the root and every library over that folder
        // (BaseItem.GetAncestorIds), so pushing any id as AncestorIds matched far more than Episode.SeriesId does:
        // Series AnyOf [Hidden Shows] would show Kid which episodes a library hidden from him holds. Every id that isn't a
        // series the caller sees keeps the rule in memory, whatever it names, so the plan tells nothing about it.
        var world = new PlannerWorld();
        var alpha = world.Add(BaseItemKind.Series, "Alpha", world.Shows);
        var a1 = world.Add(BaseItemKind.Episode, "A1", world.Shows, s => s.Series = alpha);
        world.Add(BaseItemKind.Episode, "A2", world.Shows, s => s.Series = alpha);
        var season = world.Core.SeasonOf(alpha.Id);
        Assert.Contains(world.HiddenShows.Id, a1.Ancestors);
        Assert.Contains(world.Shows.Id, a1.Ancestors);
        Assert.Contains(season, a1.Ancestors);
        Assert.Contains(world.Core.RootId, a1.Ancestors);

        string Rule(params Guid[] ids) => $$"""{"Field":"Series","Op":"AnyOf","Values":[{{string.Join(",", ids.Select(id => $$"""{"Id":"{{id:N}}","Name":"x"}"""))}}]}""";
        var notSeries = new[] { world.HiddenShows.Id, world.Shows.Id, season, world.Shows.PhysicalFolderIds[0], world.Core.RootId };
        foreach (var user in new[] { world.Kid, world.Admin })
        {
            foreach (var id in notSeries)
            {
                // No episode's series: the rule stays in memory, as for an id of nothing, and nothing matches.
                var plan = world.Plan(Q(Rule(id), "\"Episode\""), user);
                Assert.Equal(QueryPlanPath.Residual, plan.Path);
                Assert.Equal(PushdownExactness.Declined, plan.Steps[0].Exactness);
                Assert.Empty(plan.CandidateQuery!.AncestorIds);
                Assert.Empty(world.RunAndCheck(Q(Rule(id), "\"Episode\""), user));
                Assert.Empty(world.RunAndCheck(Q(Rule(id), "\"Episode\",\"Movie\""), user));
            }

            // Series only: pushed, as Exact. With another id among them, nothing is pushed.
            var only = world.Plan(Q(Rule(alpha.Id), "\"Episode\""), user);
            Assert.Equal([alpha.Id], only.CandidateQuery!.AncestorIds);
            Assert.Equal(PushdownExactness.Exact, only.Steps[0].Exactness);
            Assert.True(only.Steps[0].LeftResidual);
            var mixed = world.Plan(Q(Rule(world.HiddenShows.Id, alpha.Id, season), "\"Episode\""), user);
            Assert.Empty(mixed.CandidateQuery!.AncestorIds);
            Assert.Equal(PushdownExactness.Declined, mixed.Steps[0].Exactness);
            Assert.Equal(2, world.RunAndCheck(Q(Rule(world.HiddenShows.Id, alpha.Id, season), "\"Episode\""), user).Count);
        }

        // A series the caller's access hides (a blocked tag) is treated as an id of nothing: Kid's rule stays in memory,
        // the administrator's is pushed.
        var gore = world.Add(BaseItemKind.Series, "Gory", world.Shows, s => s.Tags = ["Gore"]);
        Assert.Equal(PushdownExactness.Declined, world.Plan(Q(Rule(gore.Id), "\"Episode\""), world.Kid).Steps[0].Exactness);
        Assert.Equal(PushdownExactness.Exact, world.Plan(Q(Rule(gore.Id), "\"Episode\""), world.Admin).Steps[0].Exactness);

        // An id that names no item could be an orphan episode's series id, which SQL can't find: the rule stays in memory.
        var orphanSeries = Guid.NewGuid();
        world.Add(BaseItemKind.Episode, "Orphan", world.Shows, s => s.SeriesId = orphanSeries);
        var unknown = world.Plan(Q(Rule(orphanSeries, alpha.Id), "\"Episode\""), world.Kid);
        Assert.Equal(PushdownExactness.Declined, unknown.Steps[0].Exactness);
        Assert.Empty(unknown.CandidateQuery!.AncestorIds);
        Assert.Equal(3, world.RunAndCheck(Q(Rule(orphanSeries, alpha.Id), "\"Episode\""), world.Kid).Count);

        // A series name resolves to the series ids episodes store; one that isn't a Series item stops the pushdown.
        world.Add(BaseItemKind.Episode, "Misfiled", world.Shows, s => { s.SeriesId = season; s.SeriesName = "Alpha"; });
        var name = world.Plan(Q("""{"Field":"SeriesName","Op":"Eq","Value":"alpha"}""", "\"Episode\""), world.Kid);
        Assert.Equal(PushdownExactness.Declined, name.Steps[0].Exactness);
        world.RunAndCheck(Q("""{"Field":"SeriesName","Op":"Eq","Value":"alpha"}""", "\"Episode\""), world.Kid);
    }

    [Fact]
    public void KindsBeyondAFieldsKinds_MakeAnExactMappingASuperset()
    {
        var world = new PlannerWorld();
        world.Add(BaseItemKind.Movie, "Film", world.Movies, s => s.Year = 2000);
        world.Add(BaseItemKind.BoxSet, "Box", world.Collections, s => s.Year = 2000);
        var plan = world.Plan(Q("""{"Field":"Year","Op":"Eq","Value":2000}""", "\"Movie\",\"BoxSet\""));
        Assert.Equal([2000], plan.CandidateQuery!.Years);
        Assert.Equal(PushdownExactness.Superset, plan.Steps[0].Exactness);
        Assert.False(plan.Steps[0].LeftResidual);
        Assert.Single(world.RunAndCheck(Q("""{"Field":"Year","Op":"Eq","Value":2000}""", "\"Movie\",\"BoxSet\"")));

        // A mandatory ItemType rule narrows the kinds, and the Year rule is Exact again.
        var narrowed = world.Plan(Q("""{"Field":"ItemType","Op":"AnyOf","Values":["Movie"]},{"Field":"Year","Op":"Eq","Value":2000}""", "\"Movie\",\"BoxSet\""), world.Admin);
        Assert.Equal(PushdownExactness.Exact, narrowed.Steps[1].Exactness);
        Assert.Equal(QueryPlanPath.Fast, narrowed.Path);
    }

    // ------------------------------------------------------------------------------------------------ OR, NOT

    [Fact]
    public void AnyGroup_PushesOnlyWhatEveryBranchImplies_AndNegatePushesNothing()
    {
        var world = new PlannerWorld();
        Populate(world);
        var years = world.Plan(Q("""{"Match":"Any","Children":[{"Field":"Year","Op":"Eq","Value":2000},{"Field":"Year","Op":"Between","From":2005,"To":2006}]}"""));
        Assert.Equal([2000, 2005, 2006], years.CandidateQuery!.Years);
        Assert.All(years.Steps, s => Assert.False(s.LeftResidual));

        var mixed = world.Plan(Q("""{"Match":"Any","Children":[{"Field":"Year","Op":"Eq","Value":2000},{"Field":"Genres","Op":"AnyOf","Values":["Drama"]}]}"""));
        Assert.Empty(mixed.CandidateQuery!.Years);
        Assert.Empty(mixed.CandidateQuery.Genres);

        var excluded = world.Plan(Q("""{"Match":"Any","Children":[{"Field":"Tags","Op":"NoneOf","Values":["a","b"]},{"Field":"Tags","Op":"NoneOf","Values":["b","c"]}]}"""));
        Assert.Equal(["b"], excluded.CandidateQuery!.ExcludeTags);

        var negated = world.Plan(Q("""{"Negate":true,"Children":[{"Field":"Year","Op":"Eq","Value":2000},{"Field":"Locked","Op":"Is","Value":true}]}"""));
        Assert.Empty(negated.CandidateQuery!.Years);
        Assert.Null(negated.CandidateQuery.IsLocked);

        foreach (var json in new[]
        {
            Q("""{"Match":"Any","Children":[{"Field":"Year","Op":"Eq","Value":2000},{"Field":"Year","Op":"Between","From":2005,"To":2006}]}"""),
            Q("""{"Match":"Any","Children":[{"Field":"Year","Op":"Eq","Value":2000},{"Field":"Genres","Op":"AnyOf","Values":["Drama"]}]}"""),
            Q("""{"Match":"Any","Children":[{"Field":"Tags","Op":"NoneOf","Values":["a","b"]},{"Field":"Tags","Op":"NoneOf","Values":["b","c"]}]}"""),
            Q("""{"Negate":true,"Children":[{"Field":"Year","Op":"Eq","Value":2000},{"Field":"Locked","Op":"Is","Value":true}]}"""),
            Q("""{"Negate":true,"Match":"Any","Children":[{"Field":"Year","Op":"Eq","Value":2000},{"Field":"Genres","Op":"AnyOf","Values":["Drama"]}]}""")
        })
        {
            world.RunAndCheck(json);
            world.RunAndCheck(json, world.Kid);
        }
    }

    [Fact]
    public void AnEmptyAllInsideAnAny_StaysInTheResidual()
    {
        var world = new PlannerWorld();
        Populate(world);

        // The Any's first branch is an All whose only rule is implied by the base; pruning it must not make the Any false.
        var json = Q($$"""{"Match":"Any","Children":[{"Match":"All","Children":[{"Field":"Library","Op":"AnyOf","Values":["{{world.Movies.Id}}"]}]},{"Field":"Year","Op":"Eq","Value":1999}]}""");
        world.RunAndCheck(json);
        world.RunAndCheck(json, world.Admin);
    }

    // ------------------------------------------------------------------------------------------------ access

    [Fact]
    public void Library_AHiddenLibraryRevealsNothing_EvenThroughASharedFolder()
    {
        var world = new PlannerWorld();
        Populate(world);
        var kid = world.Kid;
        var hidden = $$"""{"Field":"Library","Op":"AnyOf","Values":["{{world.Hidden.Id}}"]}""";

        Assert.Equal(QueryPlanPath.Empty, world.Plan(Q(hidden), kid).Path);
        Assert.Empty(world.RunAndCheck(Q(hidden), kid));
        Assert.Empty(world.RunAndCheck(Q($$"""{"Match":"Any","Children":[{{hidden}},{"Field":"Title","Op":"Eq","Value":"nothing"}]}"""), kid));

        // The membership the residual reads is the same intersection.
        var plan = world.Plan(Q($$"""{"Match":"Any","Children":[{{hidden}},{"Field":"Year","Op":"Eq","Value":1999}]}"""), kid);
        Assert.Empty(plan.Environment.LibraryMembers!([world.Hidden.Id]));
        Assert.Empty(plan.Environment.LibraryMembers!([world.Private.Id]));
        Assert.NotEmpty(plan.Environment.LibraryMembers!([world.Movies.Id]));
        world.RunAndCheck(Q($$"""{"Match":"Any","Children":[{{hidden}},{"Field":"Year","Op":"Eq","Value":1999}]}"""), kid);

        // The administrator can open Hidden, whose folder is Movies' first.
        var admin = world.Plan(Q(hidden), world.Admin);
        Assert.Equal([world.FolderA], admin.CandidateQuery!.TopParentIds);
        Assert.NotEmpty(world.RunAndCheck(Q(hidden), world.Admin));

        // NoneOf a library the user can't open excludes nothing; NoneOf one they can is the complement.
        var noneOfHidden = Q($$"""{"Field":"Library","Op":"NoneOf","Values":["{{world.Hidden.Id}}"]}""");
        Assert.Equal(QueryPlanPath.Fast, world.Plan(noneOfHidden, kid).Path);
        Assert.Equal(PlanRunner.Keys(PlanRunner.ItemsEndpoint(PlanRunner.Read(noneOfHidden), world.Core, kid).Shown, world.Core), PlanRunner.Keys(world.RunAndCheck(noneOfHidden, kid), world.Core));
        var noneOf = world.Plan(Q($$"""{"Field":"Library","Op":"NoneOf","Values":["{{world.Movies.Id}}"]}""", AllKinds), world.Admin);
        Assert.DoesNotContain(world.FolderA, noneOf.CandidateQuery!.TopParentIds);
        world.RunAndCheck(Q($$"""{"Field":"Library","Op":"NoneOf","Values":["{{world.Movies.Id}}"]}""", AllKinds), world.Admin);
    }

    [Fact]
    public void Access_ARestrictedUser_IsNeverWiderThanItems()
    {
        var world = new PlannerWorld();
        Populate(world);
        var kid = world.Kid;
        var q = world.Plan(Q("""{"Field":"Overview","Op":"Contains","Value":"e"}""", AllKinds), kid).CandidateQuery!;
        Assert.Same(kid, q.User);
        Assert.Equal(13, q.MaxParentalRating!.Score);
        Assert.Contains(UnratedItem.Movie, q.BlockUnratedItems);
        Assert.Contains(UnratedItem.Other, q.BlockUnratedItems);
        Assert.Equal(["gore"], q.ExcludeInheritedTags);

        var evaluate = world.Plan(Q("""{"Field":"Overview","Op":"Contains","Value":"e"}""", AllKinds), kid, PlanPurpose.EvaluateAll).CandidateQuery!;
        Assert.Same(kid, evaluate.User);
        Assert.Equal(13, evaluate.MaxParentalRating!.Score);
        Assert.Contains(UnratedItem.Other, evaluate.BlockUnratedItems);

        foreach (var json in Battery(world).Append(Q("""{"Field":"Title","Op":"IsNotEmpty"}""", AllKinds)))
        {
            foreach (var purpose in new[] { PlanPurpose.Search, PlanPurpose.EvaluateAll })
            {
                var results = world.RunAndCheck(json, kid, purpose);
                var items = PlanRunner.ItemsEndpoint(PlanRunner.Read(json), world.Core, kid);
                Assert.True(results.IsSubsetOf(items.Shown), json);
            }
        }

        var everything = world.RunAndCheck(Q("""{"Field":"Title","Op":"IsNotEmpty"}""", AllKinds), kid);
        var names = everything.Select(id => world.Core.RowById(id)!.Item.Name).ToHashSet();
        Assert.Contains("PG-13 film", names);
        Assert.DoesNotContain("R film", names);
        Assert.DoesNotContain("Unrated film", names);
        Assert.DoesNotContain("Gore film", names);
        Assert.DoesNotContain("Gore episode", names);
        Assert.DoesNotContain("Private film", names);
        Assert.DoesNotContain("Extra", names);
    }

    // ------------------------------------------------------------------------------------- stale clean forms

    [Fact]
    public void CleanForms_WithoutTheProbe_NoTextOrValueIsPushed()
    {
        var world = new PlannerWorld();
        Populate(world);
        world.Core.AddStudio("A24");
        world.Core.ProbeRan = false;
        var plan = world.Plan(Q("""{"Field":"Title","Op":"Contains","Value":"a"},{"Field":"Title","Op":"Eq","Value":"a"},{"Field":"Genres","Op":"AnyOf","Values":["Drama"]},{"Field":"Tags","Op":"NoneOf","Values":["x"]},{"Field":"Studios","Op":"AnyOf","Values":["A24"]}"""));
        var q = plan.CandidateQuery!;
        Assert.Null(q.NameContains);
        Assert.Null(q.Name);
        Assert.Empty(q.Genres);
        Assert.Empty(q.ExcludeTags);
        Assert.Empty(q.StudioIds);
        Assert.All(plan.Steps, s => Assert.Equal(PushdownExactness.Declined, s.Exactness));
        world.RunAndCheck(Q("""{"Field":"Genres","Op":"AnyOf","Values":["Drama"]}"""));
    }

    [Fact]
    public void CleanForms_AStaleCleanValue_DeclinesOrWidens()
    {
        var world = new PlannerWorld();

        // ≤ 10.8 cleaned "Brød" to "brød" (FormD leaves ø); today it is "brod". SQL misses the row the evaluator matches:
        // not pushed.
        Assert.Equal("brod", FakeCore.Clean("Brød"));
        world.Core.SeedValue(ItemValueType.Genre, "Brød", "brød");
        world.Add(BaseItemKind.Movie, "Old", world.Movies, s => s.Genres = ["Brød"]);

        // A row SQL matches and the evaluator doesn't: pushed, and re-checked.
        world.Core.SeedValue(ItemValueType.Genre, "Dräma (old)", "drama");
        world.Add(BaseItemKind.Movie, "Odd", world.Movies, s => s.Genres = ["Dräma (old)"]);
        world.Add(BaseItemKind.Movie, "Real", world.Movies, s => s.Genres = ["Drama"]);

        var stale = world.Plan(Q("""{"Field":"Genres","Op":"AnyOf","Values":["brod"]}"""));
        Assert.Empty(stale.CandidateQuery!.Genres);
        Assert.Equal(PushdownExactness.Declined, stale.Steps[0].Exactness);
        Assert.Single(world.RunAndCheck(Q("""{"Field":"Genres","Op":"AnyOf","Values":["brod"]}""")));

        var wide = world.Plan(Q("""{"Field":"Genres","Op":"AnyOf","Values":["Drama"]}"""));
        Assert.Equal(["Drama"], wide.CandidateQuery!.Genres);
        Assert.Equal(PushdownExactness.Superset, wide.Steps[0].Exactness);
        Assert.Single(world.RunAndCheck(Q("""{"Field":"Genres","Op":"AnyOf","Values":["Drama"]}""")));

        // An exclusion SQL applies to a row the evaluator keeps would drop a match: not pushed.
        world.Core.SeedValue(ItemValueType.Tags, "Ærø", "aero (old)");
        world.Add(BaseItemKind.Movie, "Tagged", world.Movies, s => s.Tags = ["Ærø"]);
        world.Core.SeedValue(ItemValueType.Tags, "Kept", "aero");
        world.Add(BaseItemKind.Movie, "Kept", world.Movies, s => s.Tags = ["Kept"]);
        Assert.Empty(world.Plan(Q("""{"Field":"Tags","Op":"NoneOf","Values":["aero"]}""")).CandidateQuery!.ExcludeTags);
        world.RunAndCheck(Q("""{"Field":"Tags","Op":"NoneOf","Values":["aero"]}"""));
    }

    [Fact]
    public void CleanForms_AValueHoldingAPipe_IsReadPerPlan()
    {
        var world = new PlannerWorld();

        // The item's Genres column joins with '|', so "Sci|Fi" loads as two genres; its ItemValues row is one. The value
        // arrives after the probe (a metadata edit between scans), so only the per-plan read knows it.
        world.Add(BaseItemKind.Movie, "Plain", world.Movies, s => s.Genres = ["Sci"]);
        world.Core.RunProbe();
        world.Add(BaseItemKind.Movie, "Piped", world.Movies, s => s.Genres = ["Sci|Fi"]);
        Assert.DoesNotContain(world.Core.CleanForms!.Values, v => v.Value == "Sci|Fi");
        var hidden = world.Plan(Q("""{"Field":"Genres","Op":"AnyOf","Values":["Fi"]}"""));
        Assert.Empty(hidden.CandidateQuery!.Genres);
        Assert.Single(world.RunAndCheck(Q("""{"Field":"Genres","Op":"AnyOf","Values":["Fi"]}""")));
        Assert.Equal(2, world.RunAndCheck(Q("""{"Field":"Genres","Op":"AnyOf","Values":["sci"]}""")).Count);
        world.RunAndCheck(Q("""{"Field":"Genres","Op":"AnyOf","Values":["Sci|Fi"]}"""));

        // Read once per plan; unreadable means nothing is pushed.
        var reads = world.Core.PipeReads;
        world.Plan(Q("""{"Field":"Genres","Op":"AnyOf","Values":["x"]},{"Field":"Tags","Op":"AnyOf","Values":["y"]}"""));
        Assert.Equal(reads + 1, world.Core.PipeReads);
        world.Core.PipesReadable = false;
        Assert.Empty(world.Plan(Q("""{"Field":"Genres","Op":"AnyOf","Values":["Drama"]}""")).CandidateQuery!.Genres);
    }

    [Fact]
    public void CleanForms_AStaleCleanName_DeclinesTitle()
    {
        var world = new PlannerWorld();
        world.Add(BaseItemKind.Movie, "Brød Flux", world.Movies, s => s.CleanName = "brød flux");
        world.Add(BaseItemKind.Movie, "Brod", world.Movies);
        Assert.Null(world.Plan(Q("""{"Field":"Title","Op":"Contains","Value":"brod"}""")).CandidateQuery!.NameContains);
        Assert.Null(world.Plan(Q("""{"Field":"Title","Op":"Eq","Value":"Brød Flux"}""")).CandidateQuery!.Name);
        Assert.Equal("flux", world.Plan(Q("""{"Field":"Title","Op":"Contains","Value":"flux"}""")).CandidateQuery!.NameContains);
        Assert.Equal(2, world.RunAndCheck(Q("""{"Field":"Title","Op":"Contains","Value":"brod"}""")).Count);
        Assert.Single(world.RunAndCheck(Q("""{"Field":"Title","Op":"Eq","Value":"Brød Flux"}""")));
        Assert.Single(world.RunAndCheck(Q("""{"Field":"Title","Op":"Contains","Value":"flux"}""")));
    }

    [Fact]
    public void OwnedItems_AreDroppedByTheRecheck_EvenWithATopParent()
    {
        var world = new PlannerWorld();
        var film = world.Add(BaseItemKind.Movie, "Film", world.Movies, s => s.Year = 2000);
        world.Add(BaseItemKind.Video, "Extra", world.Movies, s => { s.Owner = film; s.Year = 2000; s.TopParentId = world.FolderA; });
        var results = world.RunAndCheck(Q("""{"Field":"Year","Op":"Eq","Value":2000}""", AllKinds));
        Assert.Equal([film.Id], results);
        Assert.Equal([film.Id], world.RunAndCheck(Q("""{"Field":"Year","Op":"Eq","Value":2000}""", AllKinds), world.Admin, PlanPurpose.EvaluateAll));
    }

    // ------------------------------------------------------------------------------------------------ helpers

    internal static string Q(string rules, string kinds = "\"Movie\"", IReadOnlyList<Guid>? libraries = null, bool includeMissing = false, string extra = "")
    {
        var ids = libraries is null ? string.Empty : ",\"LibraryIds\":[" + string.Join(",", libraries.Select(id => "\"" + id + "\"")) + "]";
        var missing = includeMissing ? ",\"IncludeMissing\":true" : string.Empty;
        return "{\"Scope\":{\"ItemKinds\":[" + kinds + "]" + ids + missing + "},\"Where\":{\"Children\":[" + rules + "]}" + extra + "}";
    }

    private static bool? Flag(InternalItemsQuery q, string property) => property switch
    {
        "IsPlayed" => q.IsPlayed,
        "IsFavorite" => q.IsFavorite,
        _ => q.IsResumable
    };

    private static void AssertUtc(string? expected, DateTime? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(DateTimeKind.Utc, actual.Value.Kind);
        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal), actual.Value);
    }

    // Rows dated at 00:00Z and at local midnights of the world's zone, around the tests' bounds.
    private static void AddDatedRows(PlannerWorld world)
    {
        var days = new[] { new DateTime(2025, 12, 31), new DateTime(2026, 1, 1), new DateTime(2026, 1, 31), new DateTime(2026, 2, 1), new DateTime(2026, 2, 2), new DateTime(2026, 10, 2), new DateTime(2026, 10, 3) };
        foreach (var day in days)
        {
            foreach (var offset in new[] { TimeSpan.Zero, TimeSpan.FromHours(12), TimeSpan.FromHours(23) })
            {
                var utc = DateTime.SpecifyKind(day + offset, DateTimeKind.Utc);
                var local = DateTime.SpecifyKind(day + offset, DateTimeKind.Unspecified);
                world.Add(BaseItemKind.Movie, "U" + utc.ToString("o", System.Globalization.CultureInfo.InvariantCulture), world.Movies, s => { s.PremiereDate = utc; s.DateCreated = utc; });
                world.Add(BaseItemKind.Episode, "L" + local.ToString("s", System.Globalization.CultureInfo.InvariantCulture), world.Shows, s => { s.PremiereDate = local; s.DateCreated = local; });
            }
        }

        world.Add(BaseItemKind.Movie, "Undated", world.Movies);
    }

    // A small library with something for every rule of the battery, for both users.
    private static void Populate(PlannerWorld world)
    {
        var admin = world.Admin;
        var kid = world.Kid;
        var show = world.Add(BaseItemKind.Series, "Gore show", world.Shows, s => { s.Tags = ["Gore"]; s.InheritedRating = 10; s.OfficialRating = "PG"; });
        var clean = world.Add(BaseItemKind.Series, "Clean show", world.Shows, s => { s.InheritedRating = 0; s.OfficialRating = "G"; s.Year = 1999; });
        world.Add(BaseItemKind.Episode, "Gore episode", world.Shows, s => { s.Series = show; s.InheritedRating = 10; s.Year = 2000; });
        world.Add(BaseItemKind.Episode, "Clean episode", world.Shows, s => { s.Series = clean; s.InheritedRating = 0; s.Year = 1999; s.UserData.Add(new FakeUserData(kid.Id, "", true, true, 0)); });
        var pg = world.Add(BaseItemKind.Movie, "PG-13 film", world.Movies, s =>
        {
            s.InheritedRating = 13; s.OfficialRating = "PG-13"; s.Year = 2000; s.Genres = ["Drama"]; s.Tags = ["a"]; s.Overview = "seen";
            s.UserData.Add(new FakeUserData(admin.Id, "", true, false, 0));
            s.UserData.Add(new FakeUserData(kid.Id, "", false, true, 50));
        });
        world.Add(BaseItemKind.Movie, "R film", world.Movies, s => { s.InheritedRating = 17; s.OfficialRating = "R"; s.Year = 2005; s.Genres = ["Drama"]; s.TopParentId = world.FolderB; s.Overview = "free"; });
        world.Add(BaseItemKind.Movie, "Unrated film", world.Movies, s => { s.Year = 2006; s.Tags = ["b", "c"]; s.Overview = "none"; });
        world.Add(BaseItemKind.Movie, "Gore film", world.Movies, s => { s.InheritedRating = 10; s.Tags = ["gore"]; s.Year = 1999; s.Overview = "red"; });
        world.Add(BaseItemKind.Movie, "Private film", world.Private, s => { s.InheritedRating = 0; s.Year = 2000; s.Overview = "secret"; });
        world.Add(BaseItemKind.Video, "Home video", world.Home, s => { s.Year = 2000; s.Overview = "me"; });
        world.Add(BaseItemKind.Video, "Rated home video", world.Home, s => { s.InheritedRating = 0; s.Year = 1999; s.Overview = "we"; });
        world.Add(BaseItemKind.BoxSet, "Box", world.Collections, s => { s.InheritedRating = 0; s.Year = 2000; s.Overview = "set"; });
        world.Add(BaseItemKind.Movie, "Missing film", world.Movies, s => { s.IsVirtual = true; s.InheritedRating = 0; s.Year = 2000; s.Overview = "gone"; });
        world.Add(BaseItemKind.Video, "Extra", world.Movies, s => { s.Owner = pg; s.InheritedRating = 0; s.Year = 2000; });
        world.Add(BaseItemKind.Movie, "Version A", world.Movies, s => { s.InheritedRating = 0; s.Year = 1999; s.PresentationUniqueKey = "merged"; s.Overview = "one"; });
        world.Add(BaseItemKind.Movie, "Version B", world.Movies, s => { s.InheritedRating = 0; s.Year = 1999; s.PresentationUniqueKey = "merged"; s.TopParentId = world.FolderB; s.Overview = "two"; });
    }

    private static IEnumerable<string> Battery(PlannerWorld world) =>
    [
        Q("""{"Field":"Year","Op":"Eq","Value":2000}"""),
        Q("""{"Field":"Year","Op":"Eq","Value":2000}""", AllKinds),
        Q("""{"Field":"Year","Op":"AnyOf","Values":[1999,2000]}""", AllKinds, includeMissing: true),
        Q("""{"Field":"Year","Op":"Eq","Value":1999}""", AllKinds, extra: ""","Limit":10"""),
        Q("""{"Field":"Genres","Op":"AnyOf","Values":["drama"]}"""),
        Q("""{"Field":"Tags","Op":"NoneOf","Values":["A"]}""", AllKinds),
        Q("""{"Field":"Played","Op":"Is","Value":true}""", "\"Movie\",\"Episode\""),
        Q("""{"Field":"Favorite","Op":"Is","Value":true}""", AllKinds),
        Q("""{"Field":"InProgress","Op":"Is","Value":false}"""),
        Q("""{"Field":"Overview","Op":"IsNotEmpty"}""", AllKinds),
        Q("""{"Field":"Overview","Op":"IsEmpty"}""", AllKinds),
        Q($$"""{"Field":"Library","Op":"AnyOf","Values":["{{world.Hidden.Id}}","{{world.Home.Id}}"]}""", AllKinds),
        Q($$"""{"Match":"Any","Children":[{"Field":"Library","Op":"AnyOf","Values":["{{world.Private.Id}}"]},{"Field":"Year","Op":"Eq","Value":2006}]}""", AllKinds),
        Q("""{"Field":"Year","Op":"Eq","Value":2000}""", AllKinds, libraries: [world.Hidden.Id, world.Collections.Id]),
        Q("""{"Field":"Year","Op":"Eq","Value":2000}""", AllKinds, libraries: [world.Private.Id]),
        Q("""{"Field":"OfficialRating","Op":"AnyOf","Values":["R","PG-13"]}""", AllKinds, extra: ""","Sort":[{"Key":"ProductionYear","Order":"Descending"}]"""),
        Q("""{"Field":"Title","Op":"Contains","Value":"FILM"}"""),
        Q("""{"Field":"Title","Op":"Contains","Value":"version"}""", extra: ""","Limit":5""")
    ];
}
