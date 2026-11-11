using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Execution;

/// <summary>
/// The engine's three-valued evaluation (phase A): a facts leaf without facts is Unknown,
/// <c>All(false, …) = false</c>, <c>Any(true, …) = true</c>, <c>Not(Unknown) = Unknown</c>; with facts, or without
/// facts fields, it equals the two-valued test the planner re-checks with.
/// </summary>
public sealed class TriStateTests
{
    // "Up" matches Title Contains "up".
    private const string TitleTrue = """{"Field":"Title","Op":"Contains","Value":"up"}""";
    private const string TitleFalse = """{"Field":"Title","Op":"Contains","Value":"zzz"}""";
    private const string Fact = """{"Field":"TestFact","Op":"Ge","Value":50}""";

    private static readonly EvaluationEnvironment Environment = new() { NowUtc = PlannerWorld.Now, TimeZone = TimeZoneInfo.Utc };

    [Theory]
    // All: a false child decides; otherwise an unknown one leaves it unknown.
    [InlineData("All", false, TitleFalse + "," + Fact, "False")]
    [InlineData("All", false, Fact + "," + TitleFalse, "False")]
    [InlineData("All", false, TitleTrue + "," + Fact, "Unknown")]
    [InlineData("All", false, Fact, "Unknown")]
    // Any: a true child decides; otherwise an unknown one leaves it unknown.
    [InlineData("Any", false, Fact + "," + TitleTrue, "True")]
    [InlineData("Any", false, TitleFalse + "," + Fact, "Unknown")]
    [InlineData("Any", false, TitleFalse, "False")]
    // Not(Unknown) is Unknown, never true or false; Not of a decided group inverts it.
    [InlineData("All", true, Fact, "Unknown")]
    [InlineData("Any", true, TitleFalse + "," + Fact, "Unknown")]
    [InlineData("All", true, TitleFalse + "," + Fact, "True")]
    [InlineData("Any", true, Fact + "," + TitleTrue, "False")]
    public void WithoutFacts_FollowsKleene(string match, bool negate, string children, string expected)
    {
        var expectedState = Enum.Parse<TriState>(expected);
        var tree = Tree(match, negate, children);
        var item = new Movie { Id = Guid.NewGuid(), Name = "Up", SortName = "up" };
        Assert.Equal(expectedState, RuleTree.CompileTriState(tree, TestFacts.Registry, Environment)(new EvaluationRow(item)));
    }

    [Theory]
    [InlineData(70.0, "True")]
    [InlineData(10.0, "False")]
    [InlineData(null, "False")]
    public void WithFacts_ALeafIsDecided_AndAnEmptyFactIsFalse(double? fact, string expectedName)
    {
        var item = new Movie { Id = Guid.NewGuid(), Name = "Up", SortName = "up" };
        var expected = Enum.Parse<TriState>(expectedName);
        var row = new EvaluationRow(item, null, new Dictionary<string, object?> { [TestFacts.FieldId] = fact });
        Assert.Equal(expected, RuleTree.CompileTriState(Tree("All", false, Fact), TestFacts.Registry, Environment)(row));
        Assert.Equal(expected == TriState.True, RuleTree.Compile(Tree("All", false, Fact), TestFacts.Registry, Environment)(row));
        Assert.Equal(expected == TriState.True ? TriState.False : TriState.True, RuleTree.CompileTriState(Tree("All", true, Fact), TestFacts.Registry, Environment)(row));
    }

    [Fact]
    public void FactsFields_ListsEachFactsFieldOnce()
    {
        var tree = Tree("Any", false, Fact + "," + TitleTrue + ",{\"Match\":\"All\",\"Negate\":true,\"Children\":[" + Fact + "]}");
        Assert.Equal([TestFacts.FieldId], RuleTree.FactsFields(tree, TestFacts.Registry));
        Assert.Empty(RuleTree.FactsFields(Tree("All", false, TitleTrue), TestFacts.Registry));
    }

    [Fact]
    public void WithoutFactsFields_EqualsTheTwoValuedTest_OverGeneratedQueries()
    {
        var world = new PlannerWorld();
        var rng = new Random(DifferentialTests.DefaultSeed);
        SyntheticLibrary.Populate(world, rng, 120, disagreeingUserData: false);
        var generator = new QueryGenerator(
            rng,
            SyntheticLibrary.Names,
            SyntheticLibrary.Genres,
            SyntheticLibrary.Tags,
            SyntheticLibrary.Studios,
            SyntheticLibrary.Ratings.Where(r => r is not null).Select(r => r!).ToList(),
            world.Core.Libraries.Select(l => l.Id).ToList(),
            world.Core.Rows.Where(r => r.Kind == BaseItemKind.Series).Select(r => (r.Id, r.Item.Name)).ToList());
        var movies = world.Core.Rows.Where(r => r.Kind == BaseItemKind.Movie).Select(r => r.Id).ToHashSet();
        var environment = new EvaluationEnvironment
        {
            NowUtc = world.Environment.NowUtc,
            TimeZone = world.Environment.TimeZone,
            RatingScore = world.Environment.RatingScore,
            LibraryMembers = _ => movies
        };
        var compared = 0;
        for (var i = 0; i < 300; i++)
        {
            if (PlanRunner.TryRead(generator.Next()) is not { } query)
            {
                continue;
            }

            var two = RuleTree.Compile(query.Where, PlanRunner.Registry, environment);
            var three = RuleTree.CompileTriState(query.Where, PlanRunner.Registry, environment);
            foreach (var row in world.Core.Rows)
            {
                var evaluation = new EvaluationRow(row.Item, world.Core.MemoryUserData(world.Admin, row));
                Assert.Equal(two(evaluation) ? TriState.True : TriState.False, three(evaluation));
                compared++;
            }
        }

        Assert.True(compared > 20_000, compared.ToString(CultureInfo.InvariantCulture));
    }

    private static SmartSearchQueryNode Tree(string match, bool negate, string children)
    {
        var json = "{\"Version\":1,\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Where\":{\"Match\":\"" + match + "\",\"Negate\":" + (negate ? "true" : "false") + ",\"Children\":[" + children + "]}}";
        var read = QueryJson.Read(json);
        Assert.NotNull(read.Definition);
        var validation = TestFacts.Validator.Validate(read.Definition!, QueryValidationContext.Admin);
        Assert.True(validation.Valid, json + " → " + string.Join(", ", validation.Errors.Select(e => e.Path + ":" + e.Code)));
        return read.Definition!.Where;
    }
}
