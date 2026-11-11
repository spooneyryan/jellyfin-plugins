using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Planning;

/// <summary>
/// The planner's live check, replayed: the planner's plans for the recorded queries and users, run through
/// <see cref="FakeCore"/> loaded with the recorded sandbox rows, must do what the real 10.11.5 library manager did (the
/// same path, steps and candidates, results, count and truth), the fake's model of <c>/Items</c> must show and count what
/// the real <c>/Items</c> did, and the live run itself must have found no false negative, no false positive, nothing
/// shown or counted beyond the real <c>/Items</c> and no error. Then the differential test over the sandbox's own rows.
/// </summary>
public sealed class LiveCheckTests
{
    public const int QueryCount = 300;

    // Values the pools add to the sandbox's: '|' values as an item's column splits them, and names no item has.
    private static readonly string[] ExtraGenres = ["Sci|Fi", "Drama"];

    private static readonly string[] ExtraTags = ["a|b", "Gore"];

    private static readonly string[] ExtraStudios = ["Pixar", "Neon", "Pixar|Disney"];

    private static readonly string[] ExtraRatings = ["PG", "PG-13", "R"];

    /// <summary>A tool: writes the live check's queries from a recorded sandbox's world.json; skipped unless asked.</summary>
    [Fact]
    public void WriteLiveQueries()
    {
        var worldFile = Environment.GetEnvironmentVariable("SMARTSEARCH_LIVE_WORLD");
        var output = Environment.GetEnvironmentVariable("SMARTSEARCH_LIVE_QUERIES");
        Assert.SkipWhen(string.IsNullOrEmpty(worldFile) || string.IsNullOrEmpty(output), "Set SMARTSEARCH_LIVE_WORLD and SMARTSEARCH_LIVE_QUERIES to write the live check's queries.");
        var seed = int.TryParse(Environment.GetEnvironmentVariable("SMARTSEARCH_LIVE_SEED"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : DifferentialTests.DefaultSeed;
        using var world = JsonDocument.Parse(File.ReadAllText(worldFile!));
        var root = world.RootElement;
        var items = root.GetProperty("Items").EnumerateArray().ToList();
        IEnumerable<string> Strings(string property) => items.SelectMany(i => i.GetProperty(property).EnumerateArray().Select(v => v.GetString()!));
        var generator = Generator(
            new Random(seed),
            items.Select(i => i.GetProperty("Name").GetString()!),
            Strings("Genres"),
            Strings("Tags"),
            Strings("Studios"),
            items.Select(i => i.TryGetProperty("OfficialRating", out var rating) ? rating.GetString() : null).OfType<string>(),
            root.GetProperty("Libraries").EnumerateArray().Select(l => Guid.Parse(l.GetProperty("Id").GetString()!)),
            items.Where(i => i.GetProperty("Kind").GetString() == "Series").Select(i => (Guid.Parse(i.GetProperty("Id").GetString()!), i.GetProperty("Name").GetString()!)),
            root.GetProperty("Libraries").EnumerateArray().Select(l => (Guid.Parse(l.GetProperty("Id").GetString()!), l.GetProperty("Name").GetString()!))
                .Concat(root.GetProperty("Others").EnumerateArray().Select(o => (Guid.Parse(o.GetProperty("Id").GetString()!), o.GetProperty("Name").GetString()!))));
        var queries = new List<string>();
        for (var i = 0; i < QueryCount * 2 && queries.Count < QueryCount; i++)
        {
            var json = generator.Next();
            if (PlanRunner.TryRead(json) is not null)
            {
                queries.Add(json);
            }
        }

        var now = root.GetProperty("NowUtc").GetDateTime();
        var nowUtc = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        File.WriteAllText(output!, JsonSerializer.Serialize(new { NowUtc = nowUtc, Seed = seed, Queries = queries }));
        Assert.Equal(QueryCount, queries.Count);
    }

    [Fact]
    public void LiveCheck_FoundNoFalseNegative_AndTheFakeDoesWhatCoreDid()
    {
        var fixture = LiveFixture.Load();
        Assert.Equal("10.11.5", fixture.Server);
        Assert.True(fixture.Runs.Count >= 1000, fixture.Runs.Count + " runs");

        // The live run: no false negative (a visible truth item the results lack), no false positive, nothing wider than /Items.
        var bad = fixture.Runs.Where(r => r.Missing > 0 || r.Extra > 0 || r.Wider > 0 || r.Error is not null).ToList();
        Assert.True(bad.Count == 0, string.Join(Environment.NewLine, bad.Take(5).Select(r => fixture.Queries[r.Q] + " → " + r)));

        var (core, planner, environment, users) = fixture.Build();
        var byId = users.Values.ToDictionary(u => u.Id);

        // What IsVisible refused, per user: the matches it refused, and the scopes' rows /Items didn't show (by key).
        HashSet<Guid> Rows(string bits) => LiveFixture.Indexes(bits).Select(i => fixture.Rows[i].Id).ToHashSet();
        var invisible = fixture.Runs.GroupBy(r => fixture.CheckUsers[r.U]).ToDictionary(g => g.Key, g => g
            .SelectMany(r =>
            {
                var shown = PlanRunner.Keys(Rows(r.Shown), core);
                return Rows(r.Invisible).Concat(fixture.CheckUsers[r.U] == Guid.Empty ? [] : Rows(r.ItemsSql).Where(id => !shown.Contains(core.RowById(id)!.PresentationUniqueKey)));
            })
            .ToHashSet());
        core.IsVisible = (row, user) => !(invisible.TryGetValue(user.Id, out var hidden) && hidden.Contains(row.Id));

        var differences = new List<string>();
        var compared = 0;
        var replanned = 0;
        foreach (var run in fixture.Runs)
        {
            var json = fixture.Queries[run.Q];
            var query = PlanRunner.Read(json);
            var userId = fixture.CheckUsers[run.U];
            User? user = userId == Guid.Empty ? null : byId[userId];
            var plan = planner.Plan(new PlanRequest(query, user, Enum.Parse<PlanPurpose>(run.P)), environment);
            var where = "#" + run.Q.ToString(CultureInfo.InvariantCulture) + " " + (user?.Username ?? "none") + " " + run.P + " " + json + ": ";
            HashSet<Guid> Ids(string rows) => LiveFixture.Indexes(rows).Select(i => fixture.Rows[i].Id).ToHashSet();
            void Same(string what, IReadOnlySet<Guid> fake, IReadOnlySet<Guid> live, bool byKey)
            {
                var (a, b) = byKey ? (PlanRunner.Keys(fake, core), PlanRunner.Keys(live, core)) : (fake.Select(g => g.ToString()).ToHashSet(), live.Select(g => g.ToString()).ToHashSet());
                if (!a.SetEquals(b))
                {
                    differences.Add(where + what + ": fake only [" + string.Join(", ", a.Except(b)) + "], core only [" + string.Join(", ", b.Except(a)) + "]");
                }
            }

            // The recording's planner pushed a Series rule's ids by whether they exist, and a studio by whether a Studio
            // item has its name; now every id that isn't a series the caller sees, and every studio no item they see
            // carries, keeps the rule in memory. Such a run's plan isn't the recorded one, so its path, steps and
            // candidates can't be compared with core's; its results, count and truth still are (core's results were the
            // truth: the live run found no false negative or positive).
            var steps = plan.Steps.Select(s => s.ToString()).ToList();
            var recorded = fixture.Steps[run.S];
            var seriesReplanned = !steps.SequenceEqual(recorded)
                && steps.Count == recorded.Count
                && steps.Zip(recorded).All(p => p.First == p.Second || IsSeriesStep(p.First) || IsNarrowerSeriesName(p.First, p.Second));
            if (seriesReplanned)
            {
                replanned++;
            }
            else if (plan.Path.ToString() != run.Path)
            {
                differences.Add(where + "path " + plan.Path + ", core's " + run.Path);
                continue;
            }
            else if (!steps.SequenceEqual(recorded))
            {
                differences.Add(where + "steps [" + string.Join(" | ", steps) + "], core's [" + string.Join(" | ", recorded) + "]");
            }

            if (!seriesReplanned && plan.NeedsUserData != run.D)
            {
                differences.Add(where + "user data " + plan.NeedsUserData);
            }

            var grouped = plan.Path == QueryPlanPath.Fast || (seriesReplanned && run.Path == nameof(QueryPlanPath.Fast));
            if (!seriesReplanned)
            {
                Same("candidates", plan.CandidateQuery is null ? [] : core.Run(plan.CandidateQuery).Select(r => r.Id).ToHashSet(), Ids(run.Sql), grouped);
            }

            // The fake's /Items against the real one: what it shows and what it counts (for no user the real one was the
            // administrator's, which the fake's no-user view equals only in what it counts).
            var view = PlanRunner.ItemsEndpoint(query, core, user);
            if (user is not null)
            {
                Same("/Items shown", view.Shown, Ids(run.Shown), true);
            }

            if (PlanRunner.Keys(view.Counted, core).Count != run.ItemsCount && run.ItemsCount > 0)
            {
                differences.Add(where + "/Items count: the fake's " + PlanRunner.Keys(view.Counted, core).Count + ", core's " + run.ItemsCount);
            }

            // The recorded rows hold no paths, so a Path rule's results can't be replayed.
            if (!json.Contains("\"Path\"", StringComparison.Ordinal))
            {
                var replay = PlanRunner.Execute(plan, core, user, fixture.PageSize);
                Same("results", replay.Results, Ids(run.Results), grouped);
                // A re-planned run that left the Fast path counts its visible results, never more than SQL's count did.
                var otherPath = seriesReplanned && plan.Path.ToString() != run.Path;
                if (otherPath ? replay.Count > run.Count : replay.Count != run.Count)
                {
                    differences.Add(where + "count " + replay.Count + ", core's " + run.Count);
                }

                Same("truth", PlanRunner.Truth(query, core, user, environment), Ids(run.Truth), false);
                compared++;
            }
        }

        Assert.True(differences.Count == 0, differences.Count + " differences, e.g." + Environment.NewLine + string.Join(Environment.NewLine, differences.Take(6)));
        Assert.True(compared >= fixture.Runs.Count * 9 / 10, compared + " runs compared in full");
        Assert.True(replanned > 0 && replanned < fixture.Runs.Count / 5, replanned + " runs with a Series rule planned anew");

        // The live run had the targeted cases: IsVisible refusing matches, short Fast pages, box sets alone.
        Assert.True(fixture.Targets > 0, "no targeted queries");
        Assert.Contains(fixture.Runs, r => LiveFixture.Indexes(r.Invisible).Any());
        Assert.Contains(fixture.Runs, r => r.ShortPages > 0);
        Assert.Contains(fixture.Runs, r => fixture.Queries[r.Q].Contains("\"ItemKinds\":[\"BoxSet\"]", StringComparison.Ordinal) && r.Results != r.Sql);
    }

    [Fact]
    public void Differential_OverTheSandboxRows()
    {
        var fixture = LiveFixture.Load();
        var (core, planner, environment, users) = fixture.Build();
        var values = fixture.Values;
        var generator = Generator(
            new Random(fixture.Seed + 7),
            fixture.Rows.Select(r => r.Name),
            values.Where(v => v.Type == (int)ItemValueType.Genre).Select(v => v.Value),
            values.Where(v => v.Type == (int)ItemValueType.Tags).Select(v => v.Value),
            values.Where(v => v.Type == (int)ItemValueType.Studios).Select(v => v.Value),
            fixture.Rows.Select(r => r.Rating).OfType<string>(),
            fixture.Libraries.Select(l => l.Id),
            fixture.Rows.Where(r => r.Kind == "Series").Select(r => (r.Id, r.Name)),
            fixture.Libraries.Select(l => (l.Id, l.Name)).Concat(fixture.Others.Select(o => (o.Id, o.Kind))));
        var report = Differential.Run(new DifferentialWorld(core, planner, environment, users["admin"], users["viewer2"]), generator, 600, "sandbox");
        report.Seed = fixture.Seed + 7;
        var detail = report.Summary() + Environment.NewLine + string.Join(Environment.NewLine, report.FalseNegatives.Concat(report.FalsePositives).Concat(report.Errors).Concat(report.WiderThanItems).Take(8));
        Assert.True(report.FalseNegatives.Count == 0 && report.FalsePositives.Count == 0 && report.Errors.Count == 0 && report.WiderThanItems.Count == 0, detail);
        Assert.True(report.Valid >= 450 && report.Paths.Count == 3, detail);
    }

    // A Series, SeriesName or Studios step the current planner keeps in memory (a recording may have pushed it: the names
    // were then resolved server-wide, now in the caller's scope).
    private static bool IsSeriesStep(string step)
        => (step.Contains("Field = Series,", StringComparison.Ordinal) || step.Contains("Field = SeriesName,", StringComparison.Ordinal)
                || step.Contains("Field = Studios,", StringComparison.Ordinal))
            && step.Contains("Exactness = Declined", StringComparison.Ordinal);

    // A SeriesName step that pushes fewer series than the recording did: the names are now read only from the episodes the
    // caller can see, so a series only hidden episodes name is left out.
    private static bool IsNarrowerSeriesName(string step, string recorded)
    {
        static HashSet<string>? Ids(string text)
        {
            var match = System.Text.RegularExpressions.Regex.Match(text, @"AncestorIds=\[([^\]]*)\]");
            return match.Success ? match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal) : null;
        }

        return step.Contains("Field = SeriesName,", StringComparison.Ordinal)
            && recorded.Contains("Field = SeriesName,", StringComparison.Ordinal)
            && Ids(step) is { } now
            && Ids(recorded) is { } then
            && now.IsSubsetOf(then);
    }

    private static QueryGenerator Generator(Random rng, IEnumerable<string> names, IEnumerable<string> genres, IEnumerable<string> tags, IEnumerable<string> studios, IEnumerable<string> ratings, IEnumerable<Guid> libraries, IEnumerable<(Guid Id, string Name)> series, IEnumerable<(Guid Id, string Name)> notSeries)
    {
        static List<string> Pool(IEnumerable<string> values, IEnumerable<string> extra) => values.Concat(extra).Where(v => v.Trim().Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new QueryGenerator(
            rng,
            Pool(names, []),
            Pool(genres, ExtraGenres),
            Pool(tags, ExtraTags),
            Pool(studios, ExtraStudios),
            Pool(ratings, ExtraRatings),
            libraries.Order().ToList(),
            series.OrderBy(s => s.Id).ToList(),
            notSeries.DistinctBy(s => s.Id).OrderBy(s => s.Id).ToList());
    }
}
