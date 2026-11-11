using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Execution;

/// <summary>
/// The Residual path over a large library stays bounded: the candidates are an id list, hydrated and evaluated
/// a batch at a time and dropped, so what the engine holds is the ids and the matches' sort keys, not the items. The
/// managed heap is measured after a full collection at every batch, so the peak is what the evaluation keeps alive.
/// 20,000 rows by default; <c>SMARTSEARCH_SCALE_ROWS</c> sets another size and <c>SMARTSEARCH_SCALE_REPORT</c> names a
/// file for the numbers.
/// </summary>
public sealed class EngineScaleTests
{
    [Fact]
    public async Task AResidualSearch_OverALargeLibrary_HoldsItsIdsAndMatches_NotItsItems()
    {
        var rows = int.TryParse(Environment.GetEnvironmentVariable("SMARTSEARCH_SCALE_ROWS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 20_000;
        var world = new PlannerWorld();
        for (var i = 0; i < rows; i++)
        {
            var keep = i % 100 == 0;
            world.Add(BaseItemKind.Movie, "Film " + i.ToString(CultureInfo.InvariantCulture), world.Movies, s =>
            {
                s.Year = 1990 + (i % 30);
                s.Overview = keep ? "keep" : "other";
                s.OfficialRating = "PG";
                s.InheritedRating = 10;
            });
        }

        using var harness = new EngineHarness(world);
        harness.Settings = new EngineSettings(MaxCandidates: 1_000_000, SearchTimeoutSeconds: 300);
        var report = new System.Collections.Generic.List<string> { FormattableString.Invariant($"rows {rows}") };
        foreach (var (label, overview, expected) in new[] { ("narrow (1 in 100 matches)", "keep", rows / 100 + (rows % 100 == 0 ? 0 : 1)), ("broad (99 in 100 match)", "other", rows - (rows / 100 + (rows % 100 == 0 ? 0 : 1))) })
        {
            var query = harness.Read(QueryPlannerTests.Q("{\"Field\":\"Overview\",\"Op\":\"Contains\",\"Value\":\"" + overview + "\"}", extra: ",\"Sort\":[{\"Key\":\"ProductionYear\",\"Order\":\"Descending\"}]"));
            harness.Cache.InvalidateAll();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var baseline = GC.GetTotalMemory(true);
            var peak = baseline;
            var batches = 0;
            world.Core.OnQuery = q =>
            {
                if (q.ItemIds.Length > 0)
                {
                    batches++;
                    peak = Math.Max(peak, GC.GetTotalMemory(true));
                }
            };

            var clock = Stopwatch.StartNew();
            var page = await harness.Search(query, world.Admin, 0, 50);
            clock.Stop();
            world.Core.OnQuery = null;
            peak = Math.Max(peak, GC.GetTotalMemory(true));
            var grown = (peak - baseline) / (1024.0 * 1024.0);

            Assert.Equal(expected, page.TotalRecordCount);
            Assert.Equal(rows, page.CandidateCount);
            Assert.Equal(50, page.Items.Count);

            // The time of the same search without the collections.
            harness.Cache.InvalidateAll();
            var plain = Stopwatch.StartNew();
            Assert.Equal(expected, (await harness.Search(query, world.Admin, 0, 50)).TotalRecordCount);
            plain.Stop();

            // Ids (16 bytes each) and a sort row per match (a few hundred bytes with its boxed key): well under 1 KB a row.
            var bound = 8 + (rows * 1024.0 / (1024 * 1024));
            report.Add(FormattableString.Invariant($"{label}: {page.TotalRecordCount} matches of {page.CandidateCount} candidates, {batches} batch loads, {plain.Elapsed.TotalMilliseconds:F0} ms ({clock.Elapsed.TotalMilliseconds:F0} ms with a full collection per batch), managed heap +{grown:F1} MB at its peak (bound {bound:F0} MB), process peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024)} MB"));
            Assert.True(grown < bound, report[^1]);
        }

        if (Environment.GetEnvironmentVariable("SMARTSEARCH_SCALE_REPORT") is { Length: > 0 } file)
        {
            await File.WriteAllLinesAsync(file, report, TestContext.Current.CancellationToken);
        }
    }
}
