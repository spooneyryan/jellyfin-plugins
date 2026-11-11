using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartSearch.Query.Planning;

namespace Jellyfin.Plugin.SmartSearch.Tests.Planning;

/// <summary>
/// The differential test's engine: for each generated query, user and purpose, the planner's pushdown runs
/// through <see cref="FakeCore"/>'s SQL and the re-check (<see cref="PlanRunner.Execute"/>, the Fast path a page at a
/// time through <c>GetItemsResult</c>), and the result must equal the full in-memory evaluation of the whole tree over
/// every row <c>/Items</c> would show the user (<see cref="PlanRunner.Truth"/>). Neither the results nor the rows the
/// reported count counts may be wider than what <c>/Items</c> shows and counts for the scope
/// (<see cref="PlanRunner.ItemsEndpoint"/>), by presentation key.
/// </summary>
internal static class Differential
{
    /// <summary>Runs queries over one world; the report counts them, and lists every difference.</summary>
    public static DifferentialReport Run(PlannerWorld world, QueryGenerator generator, int queries, string label, DifferentialReport? into = null)
        => Run(new DifferentialWorld(world.Core, world.Planner, world.Environment, world.Admin, world.Kid), generator, queries, label, into);

    /// <summary>The Fast path's page size here: small, so that a run has several pages and some come back short.</summary>
    public const int PageSize = 16;

    /// <summary>Runs queries over a fake core, for no user, an administrator and a restricted user (who also plans for the auto-collections).</summary>
    public static DifferentialReport Run(DifferentialWorld world, QueryGenerator generator, int queries, string label, DifferentialReport? into = null)
    {
        var report = into ?? new DifferentialReport();
        var notSeriesBefore = generator.NotSeriesRules;
        report.Rows += world.Core.Rows.Count;
        var users = new (User? User, string Name)[] { (null, "none"), (world.Admin, "admin"), (world.Kid, "kid") };
        for (var i = 0; i < queries; i++)
        {
            var json = generator.Next();
            report.Generated++;
            var query = PlanRunner.TryRead(json, out var codes);
            if (query is null)
            {
                report.Invalid++;
                foreach (var code in codes.Distinct())
                {
                    report.Refusals[code] = report.Refusals.GetValueOrDefault(code) + 1;
                }

                continue;
            }

            report.Valid++;
            foreach (var (user, name) in users)
            {
                foreach (var purpose in name == "kid" ? new[] { PlanPurpose.Search, PlanPurpose.EvaluateAll } : [PlanPurpose.Search])
                {
                    var where = label + " #" + i.ToString(CultureInfo.InvariantCulture) + " user=" + name + " " + purpose + ": " + json;
                    try
                    {
                        Check(world, query, user, purpose, where, report);
                    }
                    catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
                    {
                        report.Errors.Add(where + " → " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
        }

        report.NotSeriesRules += generator.NotSeriesRules - notSeriesBefore;
        return report;
    }

    private static void Check(DifferentialWorld world, Jellyfin.Plugin.SmartSearch.Api.Models.SmartSearchQueryDefinition query, User? user, PlanPurpose purpose, string where, DifferentialReport report)
    {
        var plan = world.Planner.Plan(new PlanRequest(query, user, purpose), world.Environment);
        report.Runs++;
        report.Paths[plan.Path] = report.Paths.GetValueOrDefault(plan.Path) + 1;
        foreach (var step in plan.Steps)
        {
            report.Exactness[step.Exactness] = report.Exactness.GetValueOrDefault(step.Exactness) + 1;
            if (step.LeftResidual)
            {
                report.LeftResidual++;
            }
        }

        if (plan.CandidateQuery is { } candidates)
        {
            foreach (var property in Pushed(candidates))
            {
                report.Pushed[property] = report.Pushed.GetValueOrDefault(property) + 1;
            }
        }

        var run = PlanRunner.Execute(plan, world.Core, user, PageSize);
        var results = run.Results;
        report.Pages += run.Pages;
        report.ShortPages += run.ShortPages;
        var truth = PlanRunner.Truth(query, world.Core, user, world.Environment);
        report.Matches += truth.Count;

        // A Fast page shows what SQL kept of a key, if IsVisible lets it through; /Items does the same, so a key whose
        // kept row the user can't see (a merged set across libraries, say) is hidden there too. The planner alone
        // matches /Items here; the engine shows another member of the key (its differential checks that).
        if (plan.Path == QueryPlanPath.Fast && user is not null)
        {
            var hidden = PlanRunner.Keys(run.Counted.Where(id => !world.Core.IsVisibleTo(world.Core.RowById(id)!, user)), world.Core);
            var kept = truth.Where(id => !hidden.Contains(world.Core.RowById(id)!.PresentationUniqueKey)).ToHashSet();
            report.HiddenLikeItems += truth.Count - kept.Count;
            truth = kept;
        }

        var (missing, extra) = PlanRunner.Compare(plan, truth, results, world.Core);
        if (missing.Count > 0)
        {
            report.FalseNegatives.Add(where + " → missing [" + string.Join(", ", missing) + "]; steps " + string.Join(" | ", plan.Steps.Select(s => s.Field + " " + s.Op + " " + s.Exactness + " " + s.Properties)));
        }

        if (extra.Count > 0)
        {
            report.FalsePositives.Add(where + " → extra [" + string.Join(", ", extra) + "]");
        }

        // Never wider than /Items for the same user: what it shows, and what its count counts.
        var items = PlanRunner.ItemsEndpoint(query, world.Core, user);
        report.Invisible += items.Sql.Count - items.Shown.Count;
        if (user is not null && query.Scope.ItemKinds is [Jellyfin.Data.Enums.BaseItemKind.BoxSet])
        {
            report.BoxSetOnly++;
        }

        var shownKeys = PlanRunner.Keys(items.Shown, world.Core);
        var countedKeys = PlanRunner.Keys(items.Counted, world.Core);
        var shownWider = results.Where(id => !shownKeys.Contains(world.Core.RowById(id)!.PresentationUniqueKey)).ToList();
        var countedWider = run.Counted.Where(id => !countedKeys.Contains(world.Core.RowById(id)!.PresentationUniqueKey)).ToList();
        if (shownWider.Count > 0 || countedWider.Count > 0)
        {
            string Names(IEnumerable<Guid> ids) => string.Join(", ", ids.Select(id => world.Core.RowById(id)!.Item.Name));
            report.WiderThanItems.Add(where + " → " + plan.Path + ": shown beyond /Items [" + Names(shownWider) + "], counted beyond /Items' count [" + Names(countedWider) + "] (count " + run.Count + ", /Items " + items.Counted.Count + ")");
        }
    }

    private static IEnumerable<string> Pushed(MediaBrowser.Controller.Entities.InternalItemsQuery q)
    {
        if (q.Name is not null) { yield return "Name"; }
        if (q.NameContains is not null) { yield return "NameContains"; }
        if (q.HasOverview is not null) { yield return "HasOverview"; }
        if (q.Years.Length > 0) { yield return "Years"; }
        if (q.OfficialRatings.Length > 0) { yield return "OfficialRatings"; }
        if (q.Genres.Count > 0) { yield return "Genres"; }
        if (q.Tags.Length > 0) { yield return "Tags"; }
        if (q.ExcludeTags.Length > 0) { yield return "ExcludeTags"; }
        if (q.StudioIds.Length > 0) { yield return "StudioIds"; }
        if (q.AncestorIds.Length > 0) { yield return "AncestorIds"; }
        if (q.MinCommunityRating is not null) { yield return "MinCommunityRating"; }
        if (q.MinPremiereDate is not null) { yield return "MinPremiereDate"; }
        if (q.MaxPremiereDate is not null) { yield return "MaxPremiereDate"; }
        if (q.MinDateCreated is not null) { yield return "MinDateCreated"; }
        if (q.MinWidth is not null) { yield return "MinWidth"; }
        if (q.MaxWidth is not null) { yield return "MaxWidth"; }
        if (q.IsLocked is not null) { yield return "IsLocked"; }
        if (q.IsPlayed is not null) { yield return "IsPlayed"; }
        if (q.IsFavorite is not null) { yield return "IsFavorite"; }
        if (q.IsResumable is not null) { yield return "IsResumable"; }
        if (q.HasTmdbId is not null) { yield return "HasTmdbId"; }
        if (q.HasImdbId is not null) { yield return "HasImdbId"; }
        if (q.HasTvdbId is not null) { yield return "HasTvdbId"; }
        if (q.IncludeItemTypes.Length is > 0 and < 6) { yield return "IncludeItemTypes(narrowed)"; }
    }
}

/// <summary>A fake core with its planner, environment, an administrator and a restricted user.</summary>
internal sealed record DifferentialWorld(FakeCore Core, QueryPlanner Planner, Jellyfin.Plugin.SmartSearch.Query.Evaluation.EvaluationEnvironment Environment, User Admin, User Kid);

/// <summary>What a differential run did and found.</summary>
internal sealed class DifferentialReport
{
    public int Seed { get; set; }

    public int Generated { get; set; }

    public int Valid { get; set; }

    public int Invalid { get; set; }

    public int Runs { get; set; }

    public int Rows { get; set; }

    public long Matches { get; set; }

    public int LeftResidual { get; set; }

    public int Pages { get; set; }

    public int ShortPages { get; set; }

    /// <summary>Gets or sets how many rows SQL's access let through that IsVisible refused, over every run's scope.</summary>
    public long Invisible { get; set; }

    public int BoxSetOnly { get; set; }

    /// <summary>Gets or sets how many Fast matches /Items hides too: SQL kept a row of their key the user can't see.</summary>
    public int HiddenLikeItems { get; set; }

    /// <summary>Gets or sets how many Series rules named ids that aren't series (libraries, seasons, folders, the root).</summary>
    public int NotSeriesRules { get; set; }

    public Dictionary<QueryPlanPath, int> Paths { get; } = [];

    public Dictionary<PushdownExactness, int> Exactness { get; } = [];

    public SortedDictionary<string, int> Pushed { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, int> Refusals { get; } = new(StringComparer.Ordinal);

    public List<string> FalseNegatives { get; } = [];

    public List<string> FalsePositives { get; } = [];

    public List<string> WiderThanItems { get; } = [];

    public List<string> Errors { get; } = [];

    public string Summary()
        => FormattableString.Invariant($"seed {Seed}: {Generated} queries generated, {Valid} valid ({Invalid} refused by the validator), {Runs} runs over {Rows} rows, {Matches} matches; ")
            + "paths " + string.Join(", ", Paths.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)) + "; "
            + "mappings " + string.Join(", ", Exactness.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)) + FormattableString.Invariant($" ({LeftResidual} left the residual); ")
            + "pushed " + string.Join(", ", Pushed.Select(p => p.Key + "=" + p.Value)) + "; "
            + "refused " + string.Join(", ", Refusals.OrderByDescending(p => p.Value).Take(12).Select(p => p.Key + "=" + p.Value)) + "; "
            + FormattableString.Invariant($"Fast pages {Pages} ({ShortPages} short), {Invisible} rows IsVisible refused, {BoxSetOnly} BoxSet-only runs, {HiddenLikeItems} matches /Items hides by key, {NotSeriesRules} Series rules of non-series ids; ")
            + FormattableString.Invariant($"false negatives {FalseNegatives.Count}, false positives {FalsePositives.Count}, wider than /Items {WiderThanItems.Count}, errors {Errors.Count}");
}
