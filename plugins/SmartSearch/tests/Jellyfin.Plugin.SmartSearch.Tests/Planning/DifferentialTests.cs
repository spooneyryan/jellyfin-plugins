using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Kit.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Planning;

/// <summary>
/// The planner's key test: about 2,000 random valid queries, each for no user, an administrator and a restricted user
/// (searching, and evaluating for the auto-collections), over synthetic libraries on servers west of, at and east of UTC; the planner's
/// pushdown through <see cref="FakeCore"/>'s SQL (every quirk the planner relies on) plus the re-check must equal the full in-memory
/// evaluation of every row <c>/Items</c> would show (SQL's access, then <c>IsVisible</c>). Zero false negatives, no false
/// positive that survives the re-check, and nothing shown or counted beyond what <c>/Items</c> shows and counts. The seed
/// is fixed so a failure reproduces; <c>SMARTSEARCH_DIFF_SEED</c> runs another, and <c>SMARTSEARCH_DIFF_REPORT</c> names
/// a file for the summary.
/// </summary>
public sealed class DifferentialTests
{
    public const int DefaultSeed = 20261102;

    private const int QueriesPerWorld = 760;

    private const int RowsPerWorld = 170;

    [Fact]
    public void PlannerThroughCoreSql_EqualsTheFullInMemoryEvaluation()
    {
        var seed = int.TryParse(Environment.GetEnvironmentVariable("SMARTSEARCH_DIFF_SEED"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : DefaultSeed;
        var report = new DifferentialReport { Seed = seed };
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
                NotSeries(world),
                people);
            Differential.Run(world, generator, QueriesPerWorld, zones[w].Item1, report);
        }

        if (Environment.GetEnvironmentVariable("SMARTSEARCH_DIFF_REPORT") is { Length: > 0 } file)
        {
            File.WriteAllLines(file, [report.Summary(), .. report.FalseNegatives.Take(50), .. report.FalsePositives.Take(50), .. report.Errors.Take(50), .. report.WiderThanItems.Take(50)]);
        }

        var detail = report.Summary() + Environment.NewLine + string.Join(Environment.NewLine, report.FalseNegatives.Concat(report.FalsePositives).Concat(report.Errors).Concat(report.WiderThanItems).Take(8));
        Assert.True(report.FalseNegatives.Count == 0 && report.FalsePositives.Count == 0 && report.Errors.Count == 0 && report.WiderThanItems.Count == 0, detail);

        // The run covers what it claims: most queries are valid, every path is taken, every property is pushed somewhere.
        Assert.True(report.Valid >= 1800, detail);
        Assert.True(report.Paths.Count == 3 && report.Paths.Values.All(n => n >= 50), detail);
        string[] properties =
        [
            "Name", "NameContains", "HasOverview", "Years", "OfficialRatings", "Genres", "Tags", "ExcludeTags", "StudioIds", "AncestorIds",
            "MinCommunityRating", "MinPremiereDate", "MaxPremiereDate", "MinDateCreated", "MinWidth", "MaxWidth", "IsLocked", "IsPlayed",
            "IsFavorite", "IsResumable", "HasTmdbId", "HasImdbId", "HasTvdbId", "PersonIds", "IncludeItemTypes(narrowed)"
        ];

        // Jellyfin 12 never gets MaxWidth (any wider version drops the item there).
        properties = [.. properties.Where(p => !JellyfinLine.Is12 || p != "MaxWidth")];
        Assert.All(properties, p => Assert.True(report.Pushed.GetValueOrDefault(p) > 0, p + " was never pushed: " + detail));
        Assert.True(report.Matches > 10_000, detail);

        // And it exercises what core's /Items does that SQL doesn't: IsVisible refusals, short Fast pages, box sets alone,
        // Series rules naming other ancestors.
        Assert.True(report.Invisible > 1000 && report.ShortPages >= 15 && report.BoxSetOnly > 50 && report.NotSeriesRules > 50, detail);
    }

    /// <summary>The ids an episode's AncestorIds hold besides its series: libraries (Hidden Shows too), folders, seasons, the root.</summary>
    internal static List<(Guid Id, string Name)> NotSeries(PlannerWorld world)
        => world.Core.Libraries.Select(l => (l.Id, l.Name))
            .Concat(world.Core.Libraries.SelectMany(l => l.PhysicalFolderIds).Distinct().Select(f => (f, "folder")))
            .Concat(world.Core.Rows.Where(r => r.Kind == Jellyfin.Data.Enums.BaseItemKind.Series).Select(r => (world.Core.SeasonOf(r.Id), "Season 1")))
            .Append((world.Core.RootId, "root"))
            .ToList();
}
