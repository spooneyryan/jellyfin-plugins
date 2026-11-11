using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;

namespace Jellyfin.Plugin.SmartSearch.Tests.Execution;

/// <summary>
/// The differential test run through the whole engine: for each generated query, a search paged to its end
/// for an administrator and a restricted user (Fast or Residual, the page hydrator, the cache's token), and the auto-collections'
/// evaluation for the restricted user and for no user. By presentation key for a user (grouped rows are one result, as
/// <c>/Items</c> shows them, a chain's own key included), by version set without one (the auto-collections' whole-set
/// collapse), the results must equal the full in-memory evaluation (<see cref="PlanRunner.Truth"/>): no false negative,
/// no false positive, no key twice; and nothing shown, counted or reported in the count may go beyond what core's
/// <c>/Items</c> shows and counts for the user and the scope (<see cref="PlanRunner.ItemsEndpoint"/>).
/// </summary>
internal static class EngineDifferential
{
    /// <summary>The engine's page size here: small, so that a run has several pages and some come back short.</summary>
    public const int PageSize = 16;

    public static void Run(PlannerWorld world, QueryGenerator generator, int queries, string label, EngineDifferentialReport report)
    {
        using var harness = new EngineHarness(world);
        harness.Settings = new EngineSettings(MaxCandidates: 1_000_000, SearchTimeoutSeconds: 300);
        report.Rows += world.Core.Rows.Count;
        report.PassCache = new PassItemCache(1_000_000);
        var core = world.Core;
        FakeRow? Pointed(FakeRow r) => r.PrimaryVersionId is { } p ? core.RowById(Guid.ParseExact(p, "N")) : null;
        report.ChainedRows += core.Rows.Count(r => Pointed(r) is { PrimaryVersionId: not null });
        report.ListedOwnKeyRows += core.Rows.Count(r => r.PrimaryVersionId is null && core.Rows.Any(p => p.LinkedVersions.Contains(r.Id)));
        for (var i = 0; i < queries; i++)
        {
            var json = generator.Next();
            var query = PlanRunner.TryRead(json);
            if (query is null)
            {
                continue;
            }

            report.Valid++;
            var runs = new (User? User, string Name, EvaluationPurpose? Purpose)[]
            {
                (world.Admin, "admin", null),
                (world.Kid, "kid", null),
                (world.Kid, "kid", EvaluationPurpose.AutoCollection),
                (null, "none", EvaluationPurpose.AutoCollection)
            };
            foreach (var (user, name, purpose) in runs)
            {
                var where = label + " #" + i.ToString(CultureInfo.InvariantCulture) + " user=" + name + " " + (purpose?.ToString() ?? "Search") + ": " + json;
                try
                {
                    Check(harness, world, query, user, purpose, where, report);
                }
                catch (SmartSearchRefusedException ex)
                {
                    report.Refusals.Add(where + " → " + ex.Code);
                }
                catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
                {
                    report.Errors.Add(where + " → " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }
    }

    private static void Check(EngineHarness harness, PlannerWorld world, SmartSearchQueryDefinition query, User? user, EvaluationPurpose? purpose, string where, EngineDifferentialReport report)
    {
        var core = world.Core;
        var grouped = ResidualEvaluator.GroupsByPresentationKey(user, query.Scope.ItemKinds);
        var setOf = grouped ? null : SetRoots(core);
        string Key(Guid id) => setOf is null ? core.RowById(id)!.PresentationUniqueKey : setOf[id].ToString("N");
        string Names(IEnumerable<Guid> ids) => string.Join(", ", ids.Select(id => core.RowById(id)!.Item.Name + "#" + id.ToString("N")[..6]));
        var items = PlanRunner.ItemsEndpoint(query, core, user);

        // The whole result, compared with the truth; a query with a Limit also runs as asked, and must be its prefix.
        var limit = query.Limit;
        EngineOutcome all;
        EngineOutcome? limited = null;
        try
        {
            if (limit is not null)
            {
                limited = Execute(harness, core, query, user, purpose, report);
                query.Limit = null;
            }

            all = Execute(harness, core, query, user, purpose, report);
        }
        finally
        {
            query.Limit = limit;
        }

        var truth = PlanRunner.Truth(query, core, user, world.Environment);
        report.Matches += truth.Count;
        var truthKeys = truth.Select(Key).ToHashSet(StringComparer.Ordinal);
        var resultKeys = all.Results.Select(Key).ToHashSet(StringComparer.Ordinal);
        var missing = truth.Where(id => !resultKeys.Contains(Key(id))).ToList();
        var extra = all.Results.Where(id => !truthKeys.Contains(Key(id))).ToList();
        var twice = all.Results.GroupBy(Key, StringComparer.Ordinal).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
        if (missing.Count > 0)
        {
            report.FalseNegatives.Add(where + " → " + all.Path + " missing [" + Names(missing) + "]");
        }

        if (extra.Count > 0)
        {
            report.FalsePositives.Add(where + " → " + all.Path + " extra [" + Names(extra) + "]");
        }

        if (twice.Count > 0)
        {
            report.Duplicates.Add(where + " → " + all.Path + " twice [" + Names(twice) + "]");
        }

        if (limited is { } capped)
        {
            report.Limited++;
            // The same path orders the same way, so the limited run is the whole run's prefix (a random order excepted). A
            // Limit above 200 takes the Residual path where the whole run took the Fast one; both order as SQL does, but
            // SQL leaves ties in no defined order: then it is some of the whole run's results, as many as asked.
            var random = query.Sort?.Any(s => string.Equals(s.Key?.Trim(), "Random", StringComparison.OrdinalIgnoreCase)) == true;
            var expected = all.Results.Take(capped.Results.Count).ToList();
            var full = capped.Path == QueryPlanPath.Fast || capped.Results.Count == Math.Min(limit!.Value, all.Results.Count);
            var prefix = !random && capped.Path == all.Path;
            if (capped.Path != all.Path)
            {
                report.LimitedOtherPath++;
            }

            if (!full || (prefix && !capped.Results.SequenceEqual(expected)) || (!prefix && !capped.Results.All(id => resultKeys.Contains(Key(id)))))
            {
                report.FalseNegatives.Add(where + " → " + capped.Path + " with its Limit isn't the first " + capped.Results.Count + " of its " + all.Results.Count + " results: [" + Names(capped.Results) + "] vs [" + Names(expected) + "]");
            }
        }

        // Never wider than /Items for the same user: what it shows, the rows its count counts, and the count.
        string ItemsKey(Guid id) => core.RowById(id)!.PresentationUniqueKey;
        var shownKeys = PlanRunner.Keys(items.Shown, core);
        var countedKeys = PlanRunner.Keys(items.Counted, core);
        var itemsCount = grouped ? countedKeys.Count : items.Counted.Count;
        foreach (var outcome in limited is null ? [all] : new[] { all, limited })
        {
            var shownWider = outcome.Results.Where(id => !shownKeys.Contains(ItemsKey(id))).ToList();
            var countedWider = outcome.Counted.Where(id => !countedKeys.Contains(ItemsKey(id))).ToList();
            if (shownWider.Count > 0 || countedWider.Count > 0 || outcome.Total > itemsCount)
            {
                report.WiderThanItems.Add(where + " → " + outcome.Path + ": shown beyond /Items [" + Names(shownWider) + "], counted beyond /Items' count [" + Names(countedWider) + "], count " + outcome.Total + " of /Items' " + itemsCount);
            }

            // The Residual path's count is its results'; the Fast path's is SQL's, which a short page can leave above them.
            if (outcome.Path == QueryPlanPath.Residual && outcome.Total != outcome.Results.Count)
            {
                report.Errors.Add(where + " → the Residual count " + outcome.Total + " isn't its " + outcome.Results.Count + " results");
            }
        }

        if (purpose is null && user is not null && query.Scope.ItemKinds is [BaseItemKind.BoxSet])
        {
            report.BoxSetOnly++;
        }
    }

    // Each row's version-set root over the whole table (the kit's grouping, every row an input).
    private static Dictionary<Guid, Guid> SetRoots(FakeCore core)
        => Jellyfin.Plugin.Kit.Library.VersionSets.Group(core.Rows.Select(r => Jellyfin.Plugin.Kit.Library.VersionMember.From(r.Item)))
            .SelectMany(s => s.MemberIds.Select(id => (id, s.RootId)))
            .GroupBy(p => p.id)
            .ToDictionary(g => g.Key, g => g.First().RootId);

    private static EngineOutcome Execute(EngineHarness harness, FakeCore core, SmartSearchQueryDefinition query, User? user, EvaluationPurpose? purpose, EngineDifferentialReport report)
    {
        var counted = new HashSet<Guid>();
        core.OnItemsResult = (_, rows) => counted.UnionWith(rows.Select(r => r.Id));
        try
        {
            EngineOutcome outcome;
            if (purpose is { } p)
            {
                var evaluated = harness.Engine.EvaluateAllAsync(query, user, p, CancellationToken.None).GetAwaiter().GetResult();
                outcome = new EngineOutcome([.. evaluated.OrderedIds], evaluated.OrderedIds.Count, evaluated.Plan.Path, counted);

                // A pass's evaluation (the rules in memory over the scope its cache keeps, shared with every query before it)
                // gives the same result in the same order.
                core.OnItemsResult = null;
                var inPass = harness.Engine.EvaluateAllAsync(query, new EvaluationContext(p, user, WholeSets: false, ItemCache: report.PassCache), CancellationToken.None).GetAwaiter().GetResult();
                report.InPassRuns++;
                var random = (query.Sort ?? []).Any(k => k.Key == "Random");
                var same = !random ? inPass.OrderedIds.SequenceEqual(evaluated.OrderedIds)
                    : query.Limit is not null || inPass.OrderedIds.ToHashSet().SetEquals(evaluated.OrderedIds);
                if (!same)
                {
                    report.Errors.Add("in a pass (" + (user?.Username ?? "none") + "): " + inPass.OrderedIds.Count + " vs " + evaluated.OrderedIds.Count + ", same set " + inPass.OrderedIds.ToHashSet().SetEquals(evaluated.OrderedIds) + ", missing " + string.Join(",", evaluated.OrderedIds.Except(inPass.OrderedIds).Take(3).Select(id => core.RowById(id)?.Item.Name)) + ", extra " + string.Join(",", inPass.OrderedIds.Except(evaluated.OrderedIds).Take(3).Select(id => core.RowById(id)?.Item.Name)) + " :: " + System.Text.Json.JsonSerializer.Serialize(query));
                }
            }
            else
            {
                var run = harness.SearchAll(query, user!, PageSize).GetAwaiter().GetResult();
                report.Pages += run.Pages.Count;
                report.ShortPages += run.ShortPages;
                foreach (var warning in run.Warnings)
                {
                    report.Warnings[warning] = report.Warnings.GetValueOrDefault(warning) + 1;
                }

                outcome = new EngineOutcome(run.Ids, run.Total, run.Path, counted);
            }

            report.Runs++;
            report.Paths[outcome.Path] = report.Paths.GetValueOrDefault(outcome.Path) + 1;
            return outcome;
        }
        finally
        {
            core.OnItemsResult = null;
        }
    }
}

/// <summary>One engine run: the results in order, the count it reported, its path and the rows SQL's count counted.</summary>
internal sealed record EngineOutcome(List<Guid> Results, int? Total, QueryPlanPath Path, HashSet<Guid> Counted);

/// <summary>What an engine differential run did and found.</summary>
internal sealed class EngineDifferentialReport
{
    public int Seed { get; set; }

    /// <summary>Gets or sets the cache of the world's pass: every evaluation in it shares one.</summary>
    public PassItemCache? PassCache { get; set; }

    /// <summary>Gets or sets the evaluations repeated as a pass would run them.</summary>
    public int InPassRuns { get; set; }

    public int Valid { get; set; }

    public int Runs { get; set; }

    public int Rows { get; set; }

    /// <summary>Gets or sets the rows pointing at a secondary (core's chains: keyed by that secondary).</summary>
    public int ChainedRows { get; set; }

    /// <summary>Gets or sets the rows a primary lists that point nowhere (in its set, with their own key).</summary>
    public int ListedOwnKeyRows { get; set; }

    public long Matches { get; set; }

    public int Pages { get; set; }

    public int ShortPages { get; set; }

    public Dictionary<string, int> Warnings { get; } = new(StringComparer.Ordinal);

    public int Limited { get; set; }

    public int LimitedOtherPath { get; set; }

    public int BoxSetOnly { get; set; }

    public Dictionary<QueryPlanPath, int> Paths { get; } = [];

    public List<string> FalseNegatives { get; } = [];

    public List<string> FalsePositives { get; } = [];

    public List<string> Duplicates { get; } = [];

    public List<string> WiderThanItems { get; } = [];

    public List<string> Refusals { get; } = [];

    public List<string> Errors { get; } = [];

    public string Summary()
        => FormattableString.Invariant($"seed {Seed}: {Valid} valid queries, {Runs} engine runs over {Rows} rows ({ChainedRows} chained, {ListedOwnKeyRows} listed with their own key), {Matches} matches; ")
            + "paths " + string.Join(", ", Paths.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)) + "; "
            + FormattableString.Invariant($"pages {Pages} ({ShortPages} short), {Limited} limited runs ({LimitedOtherPath} on the other path), {BoxSetOnly} BoxSet-only searches; ")
            + "warnings " + string.Join(", ", Warnings.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)) + "; "
            + FormattableString.Invariant($"false negatives {FalseNegatives.Count}, false positives {FalsePositives.Count}, keys twice {Duplicates.Count}, wider than /Items {WiderThanItems.Count}, refusals {Refusals.Count}, errors {Errors.Count}");
}
