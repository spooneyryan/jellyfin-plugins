using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Api;

/// <summary>
/// A series name or a studio that only items hidden from the caller carry (a parental limit, a blocked tag, a library
/// they can't open) plans exactly like a name nobody has: the names are resolved in the caller's own scope, so neither a
/// plan's path, its steps nor <c>/Validate</c> tells whether such a name exists. Through the value index (the server's
/// wiring) and through the per-plan reads (a planner without it).
/// </summary>
public sealed class ScopedNameTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Q(string rules, string kinds) => QueryPlannerTests.Q(rules, kinds);

    private static string SeriesName(string op, string text) => Q("{\"Field\":\"SeriesName\",\"Op\":\"" + op + "\",\"Value\":\"" + text + "\"}", "\"Episode\"");

    private static string Studio(string name) => Q("{\"Field\":\"Studios\",\"Op\":\"AnyOf\",\"Values\":[\"" + name + "\"]}", "\"Movie\"");

    private static PlannerWorld SeriesWorld()
    {
        var world = new PlannerWorld();
        var hidden = world.Add(BaseItemKind.Series, "Bloodbath Academy", world.Shows, s => { s.OfficialRating = "R"; s.InheritedRating = 17; });
        world.Add(BaseItemKind.Episode, "Pilot", world.Shows, s => { s.Series = hidden; s.SeriesName = "Bloodbath Academy"; s.OfficialRating = "R"; s.InheritedRating = 17; });
        var gory = world.Add(BaseItemKind.Series, "Gutsy Tales", world.Shows, s => { s.OfficialRating = "PG"; s.InheritedRating = 10; s.Tags = ["Gore"]; });
        world.Add(BaseItemKind.Episode, "One", world.Shows, s => { s.Series = gory; s.SeriesName = "Gutsy Tales"; s.OfficialRating = "PG"; s.InheritedRating = 10; s.Tags = ["Gore"]; });
        var open = world.Add(BaseItemKind.Series, "Blue Skies", world.Shows, s => { s.OfficialRating = "PG"; s.InheritedRating = 10; });
        world.Add(BaseItemKind.Episode, "Sunny", world.Shows, s => { s.Series = open; s.SeriesName = "Blue Skies"; s.OfficialRating = "PG"; s.InheritedRating = 10; });
        return world;
    }

    private static PlannerWorld StudioWorld()
    {
        var world = new PlannerWorld();
        world.Core.AddStudio("Secret Pictures");
        world.Core.AddStudio("Open Pictures");
        world.Add(BaseItemKind.Movie, "Private film", world.Private, s => { s.Studios = ["Secret Pictures"]; s.OfficialRating = "PG"; s.InheritedRating = 10; });
        world.Add(BaseItemKind.Movie, "Gory film", world.Movies, s => { s.Studios = ["Gore Pictures"]; s.Tags = ["Gore"]; s.OfficialRating = "PG"; s.InheritedRating = 10; });
        world.Core.AddStudio("Gore Pictures");
        world.Add(BaseItemKind.Movie, "Open film", world.Movies, s => { s.Studios = ["Open Pictures"]; s.OfficialRating = "PG"; s.InheritedRating = 10; });
        return world;
    }

    [Theory]
    [InlineData("Contains", "bloodb", "bloodc")]
    [InlineData("StartsWith", "Gutsy T", "Gutsy X")]
    [InlineData("Eq", "Bloodbath Academy", "Bloodbath Academie")]
    public async Task AHiddenSeriesName_PlansLikeANameNobodyHas(string op, string hiddenText, string unknownText)
    {
        await using var host = await new SearchApiHost(SeriesWorld()).StartAsync();

        // Kid sees neither series nor any of their episodes: the picker and a search agree.
        Assert.Equal(0, (await host.OkAsync("GET", "/SmartSearch/Series?prefix=" + Uri.EscapeDataString(hiddenText[..3]), host.Kid)).GetArrayLength());
        Assert.Empty(await host.SearchAllAsync(SeriesName(op, hiddenText), host.Kid));

        // The plan through the value index, as the server wires it, and through the per-plan reads.
        foreach (var plan in new Func<string, Task<QueryPlan>>[]
        {
            text => host.Engine.Engine.ExplainAsync(host.Engine.Read(SeriesName(op, text)), host.Kid, PlanPurpose.Search, Ct),
            text => Task.FromResult(host.World.Plan(SeriesName(op, text), host.Kid))
        })
        {
            var hidden = await plan(hiddenText);
            var unknown = await plan(unknownText);
            Assert.Equal(QueryPlanPath.Empty, hidden.Path);
            Assert.Equal(unknown.Path, hidden.Path);
            Assert.Equal(unknown.Steps.Select(s => (s.Exactness, s.Properties, s.Reason)), hidden.Steps.Select(s => (s.Exactness, s.Properties, s.Reason)));
        }

        // The administrator, who sees the series, finds it; Kid's own series is pushed down for him.
        Assert.Single(await host.SearchAllAsync(SeriesName(op, hiddenText), host.Admin));
        var mine = await host.Engine.Engine.ExplainAsync(host.Engine.Read(SeriesName("Contains", "blue")), host.Kid, PlanPurpose.Search, Ct);
        Assert.Equal(PushdownExactness.Superset, mine.Steps[0].Exactness);
        Assert.Single(await host.SearchAllAsync(SeriesName("Contains", "blue"), host.Kid));
    }

    [Theory]
    [InlineData("Secret Pictures")]
    [InlineData("Gore Pictures")]
    public async Task AStudioOnlyHiddenItemsCarry_PlansLikeANameNobodyHas(string hiddenStudio)
    {
        await using var host = await new SearchApiHost(StudioWorld()).StartAsync();

        Assert.DoesNotContain(hiddenStudio, (await host.OkAsync("GET", "/SmartSearch/Values/Studios?limit=100", host.Kid)).GetRawText(), StringComparison.Ordinal);

        foreach (var plan in new Func<string, Task<QueryPlan>>[]
        {
            name => host.Engine.Engine.ExplainAsync(host.Engine.Read(Studio(name)), host.Kid, PlanPurpose.Search, Ct),
            name => Task.FromResult(host.World.Plan(Studio(name), host.Kid))
        })
        {
            var hidden = await plan(hiddenStudio);
            var unknown = await plan("No Such Pictures");
            Assert.Equal(QueryPlanPath.Residual, hidden.Path);
            Assert.Equal(unknown.Path, hidden.Path);
            Assert.Equal(PushdownExactness.Declined, hidden.Steps[0].Exactness);
            Assert.Equal(unknown.Steps.Select(s => (s.Exactness, s.Properties, s.Reason)), hidden.Steps.Select(s => (s.Exactness, s.Properties, s.Reason)));

            // A studio Kid can see is pushed down; the administrator's hidden one too.
            Assert.Equal(QueryPlanPath.Fast, (await plan("Open Pictures")).Path);
        }

        Assert.Equal(QueryPlanPath.Fast, (await host.Engine.Engine.ExplainAsync(host.Engine.Read(Studio(hiddenStudio)), host.Admin, PlanPurpose.Search, Ct)).Path);
        Assert.Empty(await host.SearchAllAsync(Studio(hiddenStudio), host.Kid));
        Assert.Single(await host.SearchAllAsync(Studio("Open Pictures"), host.Kid));
    }

    [Fact]
    public async Task Validate_TellsANonAdministratorNoPath_WhateverTheNamesHold()
    {
        await using var host = await new SearchApiHost(SeriesWorld()).StartAsync();

        foreach (var text in new[] { "bloodb", "bloodc", "blue" })
        {
            var check = await host.OkAsync("POST", "/SmartSearch/Validate", host.Kid, "{\"Query\":" + SeriesName("Contains", text) + "}");
            Assert.True(check.GetProperty("Valid").GetBoolean());
            Assert.False(check.TryGetProperty("Paths", out _));
        }

        var admin = await host.OkAsync("POST", "/SmartSearch/Validate", host.Admin, "{\"Query\":" + SeriesName("Contains", "bloodb") + "}");
        Assert.Equal(["Residual"], admin.GetProperty("Paths").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task ThePlanner_TakesTheIndexOnlyWhenBuiltAndNoEventTouchedIt_AndOtherwiseReadsForItselfWithoutWaiting()
    {
        await using var host = await new SearchApiHost(SeriesWorld()).StartAsync();
        Task<QueryPlan> Plan() => host.Engine.Engine.ExplainAsync(host.Engine.Read(SeriesName("Contains", "blue")), host.Kid, PlanPurpose.Search, Ct);
        int OwnReads()
        {
            lock (host.World.Core.Queries)
            {
                // One query object per read of the episodes (its id list, then its batches).
                return host.World.Core.Queries.Where(q => q.IncludeItemTypes.SequenceEqual([BaseItemKind.Episode])).Distinct().Count();
            }
        }
        async Task UntilTheIndexIsTaken()
        {
            for (var i = 0; i < 500; i++)
            {
                var before = OwnReads();
                Assert.Equal(PushdownExactness.Superset, (await Plan()).Steps[0].Exactness);
                if (OwnReads() == before)
                {
                    return;
                }

                await Task.Delay(10, Ct);
            }

            Assert.Fail("The planner never took the index.");
        }

        // No index yet: the plan reads the episodes itself, and the index is built in the background for later plans.
        Assert.Equal(PushdownExactness.Superset, (await Plan()).Steps[0].Exactness);
        Assert.Equal(1, OwnReads());
        await UntilTheIndexIsTaken();
        var builds = host.Values.Builds;
        var reads = OwnReads();

        // During a scan the index may lag: the plan reads for itself.
        host.ScanRunning = true;
        Assert.Equal(PushdownExactness.Superset, (await Plan()).Steps[0].Exactness);
        Assert.Equal(reads + 1, OwnReads());
        host.ScanRunning = false;

        // Right after an item event the suggestions may still be served, but the planner doesn't take them, and nothing
        // is rebuilt while the events may still be coming; once they settle the rebuild starts and the index is taken
        // again when it is done.
        host.World.Core.Library.Raise(l => l.ItemUpdated += null, host.World.Core.Library.Object, new ItemChangeEventArgs { Item = new Movie(), UpdateReason = ItemUpdateType.MetadataEdit });
        Assert.Equal(PushdownExactness.Superset, (await Plan()).Steps[0].Exactness);
        Assert.Equal(reads + 2, OwnReads());
        Assert.Equal(builds, host.Values.Builds);
        host.Clock.Advance(Search.ValueIndexService.Debounce);
        await UntilTheIndexIsTaken();
        Assert.True(host.Values.Builds > builds);
    }
}
