using System;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Planning;

/// <summary>What a plan's steps show outside the server (Explain shows property names and counts).</summary>
public sealed class PushdownStepTests
{
    [Fact]
    public void ResolvedIds_NeverReachTheExplanation()
    {
        // A series name resolves to ids read from the episodes in the user's folders: Explain shows property names and
        // counts only. (Only the episodes the user can see are read, so a series their access hides is never pushed for them.)
        var world = new PlannerWorld();
        var hidden = world.Add(BaseItemKind.Series, "Alpha", world.Shows, s => s.Tags = ["Gore"]);
        var other = world.Add(BaseItemKind.Series, "Alpha", world.Shows);
        world.Add(BaseItemKind.Episode, "A1", world.Shows, s => s.Series = hidden);
        world.Add(BaseItemKind.Episode, "B1", world.Shows, s => s.Series = other);
        var kid = world.Plan(QueryPlannerTests.Q("""{"Field":"SeriesName","Op":"Eq","Value":"alpha"}""", "\"Episode\""), world.Kid);
        Assert.Equal(PushdownExactness.Superset, kid.Steps[0].Exactness);
        Assert.Equal([other.Id], kid.CandidateQuery!.AncestorIds);
        var step = world.Plan(QueryPlannerTests.Q("""{"Field":"SeriesName","Op":"Eq","Value":"alpha"}""", "\"Episode\""), world.Admin).Steps[0];
        Assert.Contains(hidden.Id.ToString("N"), step.Properties, StringComparison.Ordinal);

        var explained = step.ForExplain();
        Assert.DoesNotContain(hidden.Id.ToString("N"), explained.Properties, StringComparison.Ordinal);
        Assert.DoesNotContain(other.Id.ToString("N"), explained.Properties, StringComparison.Ordinal);
        Assert.Contains("AncestorIds=[2 ids]", explained.Properties, StringComparison.Ordinal);
        Assert.Equal(step with { Properties = explained.Properties }, explained);

        var years = world.Plan(QueryPlannerTests.Q("""{"Field":"Year","Op":"Eq","Value":2000}"""), world.Kid).Steps[0];
        Assert.Equal(years, years.ForExplain());
    }
}
