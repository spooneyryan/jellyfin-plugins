using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Grouping;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Normalization;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Duplicates;

/// <summary>
/// The pieces under <c>GET /Groups</c>: the options' parsing and defaults (the 400s are <c>GroupsApiTests</c>'), the
/// options' hash, the resolution label, and a 10k synthetic grouping under a generous bound (the 100 ms
/// acceptance is the <c>scale</c> sandbox probe's, in Release).
/// </summary>
public sealed class GroupsQueryTests
{
    private static readonly DateTime Today = new(2026, 11, 1);

    [Fact]
    public void WithNoOptions_TheSettingsDefaultsApply()
    {
        var query = Parse(new PluginConfiguration());

        Assert.Equal((MatchMode.TitleAndYear, UnknownYearPolicy.MatchUnknownOnly), (query.Grouping.Mode, query.Grouping.UnknownYear));
        Assert.Equal(2027, query.Grouping.Normalization.ReleaseNameMaxYear);
        Assert.Null(query.LibraryIds);
        Assert.Null(query.Confidence);
        Assert.Null(query.Search);
        Assert.Equal((GroupSort.Confidence, 0, GroupsQuery.DefaultLimit), (query.Sort, query.StartIndex, query.Limit));

        var settings = Parse(new PluginConfiguration { DefaultMatchMode = "Title", UnknownYearPolicy = "MatchAny", StripEditionSuffixes = false });
        Assert.Equal((MatchMode.Title, UnknownYearPolicy.MatchAny, false), (settings.Grouping.Mode, settings.Grouping.UnknownYear, settings.Grouping.Normalization.StripEditionSuffixes));

        // A blank option is no option.
        Assert.Equal(MatchMode.Title, Parse(new PluginConfiguration { DefaultMatchMode = "Title" }, mode: " ").Grouping.Mode);
    }

    [Fact]
    public void TheOptions_OverrideThem_ByNameIgnoringCase_WithListsTrimmed()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var query = Parse(
            new PluginConfiguration(),
            mode: "title",
            unknownYear: "MATCHANY",
            libraryIds: $" {a:N} ,{b},,{a:D}",
            confidence: "likely, Conflict,likely",
            search: "  heat ",
            sort: "newest",
            startIndex: 100,
            limit: GroupsQuery.MaxLimit);

        Assert.Equal((MatchMode.Title, UnknownYearPolicy.MatchAny), (query.Grouping.Mode, query.Grouping.UnknownYear));
        Assert.Equal([a, b], query.LibraryIds);
        Assert.Equal(new HashSet<GroupConfidence> { GroupConfidence.Likely, GroupConfidence.Conflict }, query.Confidence);
        Assert.Equal("heat", query.Search);
        Assert.Equal((GroupSort.Newest, 100, 200), (query.Sort, query.StartIndex, query.Limit));
        Assert.Null(Parse(new PluginConfiguration(), libraryIds: " , ", confidence: ",", search: "   ").LibraryIds);
    }

    [Fact]
    public void TheHash_ChangesWithEveryOptionThatChangesTheGroups_AndOnlyWithThem()
    {
        var hash = GroupingOptions.Default.Hash();
        Assert.Matches("^[0-9a-f]{16}$", hash);
        Assert.Equal(hash, new GroupingOptions().Hash());

        var rules = NormalizationOptions.Default;
        GroupingOptions[] others =
        [
            GroupingOptions.Default with { Mode = MatchMode.Title },
            GroupingOptions.Default with { UnknownYear = UnknownYearPolicy.MatchAny },
            With(new NormalizationOptions { StripEditionSuffixes = false }),
            With(new NormalizationOptions { EditionPhrases = ["redux"] }),
            With(new NormalizationOptions { ArticleLanguages = ["En", "Fr"] }),
            With(new NormalizationOptions { RomanNumerals = true }),
            With(new NormalizationOptions { IgnoreSpaces = true }),
            With(new NormalizationOptions { ReleaseNameCleanup = true, ReleaseNameMaxYear = 2027 }),
        ];
        var hashes = others.Select(o => o.Hash()).Append(hash).ToList();
        Assert.Equal(hashes.Count, hashes.Distinct().Count());

        // The phrases and languages are sets, in their comparison forms; N10's last year counts only with N10 on.
        Assert.Equal(hash, With(new NormalizationOptions { EditionPhrases = rules.EditionPhrases.Reverse().Select(p => p.ToUpperInvariant()).ToArray() }).Hash());
        Assert.Equal(hash, With(new NormalizationOptions { ArticleLanguages = ["en"] }).Hash());
        Assert.Equal(hash, With(new NormalizationOptions { ReleaseNameMaxYear = 2030 }).Hash());
        Assert.NotEqual(
            With(new NormalizationOptions { ReleaseNameCleanup = true, ReleaseNameMaxYear = 2027 }).Hash(),
            With(new NormalizationOptions { ReleaseNameCleanup = true, ReleaseNameMaxYear = 2028 }).Hash());

        // With edition suffixes off, the phrases don't count.
        Assert.Equal(
            With(new NormalizationOptions { StripEditionSuffixes = false }).Hash(),
            With(new NormalizationOptions { StripEditionSuffixes = false, EditionPhrases = ["redux"] }).Hash());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(640, "SD")]
    [InlineData(1199, "SD")]
    [InlineData(1200, "720p")]
    [InlineData(1899, "720p")]
    [InlineData(1900, "1080p")]
    [InlineData(1920, "1080p")]
    [InlineData(2500, "1440p")]
    [InlineData(3799, "1440p")]
    [InlineData(3800, "2160p")]
    [InlineData(7680, "2160p")]
    public void TheResolutionLabel_ComesFromTheWidth(int? width, string? label)
        => Assert.Equal(label, DuplicateGroupsService.ResolutionLabel(width));

    [Fact]
    public void TenThousandSyntheticMovies_GroupWellUnderASecond_AfterAWarmUp()
    {
        // About 8 % copies, realistic titles (accents, articles, editions, punctuation), most with ids.
        var entries = Synthetic(10_000);
        DuplicateGrouper.Group(entries, GroupingOptions.Default);

        var best = double.MaxValue;
        GroupingResult result = null!;
        for (var run = 0; run < 5; run++)
        {
            var watch = Stopwatch.StartNew();
            result = DuplicateGrouper.Group(entries, GroupingOptions.Default);
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }

        Assert.Equal(10_000, result.Entries);
        Assert.InRange(result.Groups.Count, 400, 1000);
        Assert.True(best < 1000, string.Create(CultureInfo.InvariantCulture, $"The best of 5 groupings took {best:F1} ms."));
    }

    private static GroupingOptions With(NormalizationOptions rules) => GroupingOptions.Default with { Normalization = rules };

    private static GroupsQuery Parse(
        PluginConfiguration configuration,
        string? mode = null,
        string? unknownYear = null,
        string? libraryIds = null,
        string? confidence = null,
        string? search = null,
        string? sort = null,
        int? startIndex = null,
        int? limit = null)
        => GroupsQuery.Parse(configuration, Today, mode, unknownYear, libraryIds, confidence, search, sort, startIndex, limit);

    private static List<VersionEntry> Synthetic(int count)
    {
        var library = Guid.NewGuid();
        var random = new Random(7);
        string[] words = ["The", "Dark", "Night", "Return", "of", "a", "City", "Blade", "Runner", "Star", "War", "Last", "Ship", "Red", "River", "Lost", "Kingdom", "Amélie", "L.A.", "Love", "Heat", "Fire", "Ice", "Storm", "Ghost", "King's", "Mission:", "Impossible", "Part", "II"];
        var entries = new List<VersionEntry>(count);
        for (var i = 0; i < count; i++)
        {
            string name;
            int? year;
            if (i > 100 && random.NextDouble() < 0.08)
            {
                var earlier = entries[random.Next(entries.Count)].Representative;
                name = earlier.Name + (random.NextDouble() < 0.3 ? " - Director's Cut" : string.Empty);
                year = random.NextDouble() < 0.1 ? null : earlier.ProductionYear;
            }
            else
            {
                name = string.Join(' ', Enumerable.Range(0, random.Next(1, 5)).Select(_ => words[random.Next(words.Length)])) + " " + i.ToString(CultureInfo.InvariantCulture);
                year = random.NextDouble() < 0.03 ? null : random.Next(1930, 2026);
            }

            var movie = new MovieRecord
            {
                Id = Guid.NewGuid(),
                Name = name,
                ProductionYear = year,
                Path = $"/movies/{i}/{i}.mkv",
                ContainingFolder = $"/movies/{i}",
                VideoType = VideoType.VideoFile,
                Size = random.NextInt64(1_000_000_000, 40_000_000_000),
                Width = random.NextDouble() < 0.5 ? 1920 : 3840,
                TotalBitrate = random.Next(2_000_000, 80_000_000),
                RunTimeTicks = TimeSpan.FromMinutes(random.Next(80, 180)).Ticks,
                DateCreated = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(random.Next(0, 2000)),
                Tmdb = random.NextDouble() < 0.9 ? random.Next(1, 900_000).ToString(CultureInfo.InvariantCulture) : null,
                LibraryIds = [library]
            };
            entries.Add(new VersionEntry(movie, [movie], 1, VersionSetState.None, [], [], [library], new HashSet<Guid> { movie.Id }, movie.Id, []));
        }

        return entries;
    }
}
