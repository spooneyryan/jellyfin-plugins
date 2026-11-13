using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Execution;

/// <summary>
/// The differential test through the engine: the planner differential's worlds and queries (same seed,
/// same three server zones), each searched by an administrator and a restricted user and evaluated for the auto-collections by the
/// restricted user and by no user, through <see cref="Query.Execution.SmartSearchEngine"/> over <see cref="FakeCore"/>.
/// Zero false negatives, zero false positives, no presentation key twice, nothing shown or counted beyond
/// <c>/Items</c>. <c>SMARTSEARCH_DIFF_SEED</c> runs another seed and <c>SMARTSEARCH_ENGINE_DIFF_REPORT</c> names a file for
/// the summary.
/// </summary>
public sealed class EngineDifferentialTests
{
    private const int QueriesPerWorld = 760;

    private const int RowsPerWorld = 170;

    [Fact]
    public void TheEngine_EqualsTheFullInMemoryEvaluation_AndIsNeverWiderThanItems()
    {
        var seed = int.TryParse(Environment.GetEnvironmentVariable("SMARTSEARCH_DIFF_SEED"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : DifferentialTests.DefaultSeed;
        var report = new EngineDifferentialReport { Seed = seed };
        var zones = new[] { ("west", PlannerWorld.West), ("utc", TimeZoneInfo.Utc), ("east", PlannerWorld.East) };
        for (var w = 0; w < zones.Length; w++)
        {
            var rng = new Random(seed + w);
            var world = new PlannerWorld(zones[w].Item2);
            SyntheticLibrary.Populate(world, rng, RowsPerWorld, disagreeingUserData: w == 2);
            var people = SyntheticLibrary.AddPeople(world, seed + w);
            var generator = new QueryGenerator(
                rng,
                SyntheticLibrary.Names,
                SyntheticLibrary.Genres,
                SyntheticLibrary.Tags,
                SyntheticLibrary.Studios,
                SyntheticLibrary.Ratings.Where(r => r is not null).Select(r => r!).ToList(),
                world.Core.Libraries.Select(l => l.Id).ToList(),
                world.Core.Rows.Where(r => r.Kind == Jellyfin.Data.Enums.BaseItemKind.Series).Select(r => (r.Id, r.Item.Name)).ToList(),
                DifferentialTests.NotSeries(world),
                people);
            EngineDifferential.Run(world, generator, QueriesPerWorld, zones[w].Item1, report);
        }

        if (Environment.GetEnvironmentVariable("SMARTSEARCH_ENGINE_DIFF_REPORT") is { Length: > 0 } file)
        {
            File.WriteAllLines(file, [report.Summary(), .. report.FalseNegatives.Take(50), .. report.FalsePositives.Take(50), .. report.Duplicates.Take(50), .. report.WiderThanItems.Take(50), .. report.Errors.Take(50), .. report.Refusals.Take(50)]);
        }

        var detail = report.Summary() + Environment.NewLine + string.Join(Environment.NewLine, report.FalseNegatives.Concat(report.FalsePositives).Concat(report.Duplicates).Concat(report.WiderThanItems).Concat(report.Errors).Concat(report.Refusals).Take(8));
        Assert.True(report.FalseNegatives.Count == 0 && report.FalsePositives.Count == 0 && report.Duplicates.Count == 0 && report.WiderThanItems.Count == 0 && report.Errors.Count == 0 && report.Refusals.Count == 0, detail);

        // It covers what it claims: both paths, short Fast pages, box sets alone, merged versions in most worlds' results,
        // chains and listed items that keep their own key.
        Assert.True(report.Valid >= 1800 && report.Runs >= 4 * 1800 && report.InPassRuns >= 2 * 1800, detail);
        Assert.True(report.Paths.GetValueOrDefault(QueryPlanPath.Residual) >= 2000, detail);

        // How many queries take the Fast path depends on the seed's draws; the bar holds for the default seed only, while
        // every seed keeps the correctness checks above.
        if (seed == DifferentialTests.DefaultSeed)
        {
            Assert.True(report.Paths.GetValueOrDefault(QueryPlanPath.Fast) >= 200, detail);
        }
        Assert.True(report.ShortPages > 20 && report.BoxSetOnly > 50 && report.Matches > 10_000, detail);
        Assert.True(report.ChainedRows > 0 && report.ListedOwnKeyRows > 0, detail);
    }
}
