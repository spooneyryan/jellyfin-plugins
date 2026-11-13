using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SmartSearch.Tests.Planning;

/// <summary>
/// The differential test's synthetic library: random rows drawn from pools of edge values, so that the
/// generated queries, drawn from the same pools, match something. Titles and original titles with diacritics, non-ASCII
/// capitals and <c>% _ [ ] ^</c>; years 0, 1799, 1800, 2100, 2101, 9999; ratings next to float boundaries; dates at
/// 00:00Z, at a west and an east zone's midnight and stored without a kind; provider ids in both cases and blank; comma
/// containers; genre, tag and studio values with <c>|</c>, in two cases and stale (cleaned the ≤ 10.8 way); stale clean
/// names and episodes' stale series names; a studio without a Studio item and one whose Studio item has a stale name;
/// owned extras (with and without a top parent); missing items; merged versions split across folders and libraries,
/// chains as core's "Merge versions" leaves them (an item pointing at a secondary, keyed by it) and items a primary lists
/// that keep their own key;
/// items with no user data, and users whose rows of one item disagree. And what only <c>IsVisible(user)</c> reads: box
/// sets whose children are all in a library the restricted user can't open, episodes whose season carries a blocked
/// tag, and ratings compared in memory that the stored inherited rating lags.
/// </summary>
internal static class SyntheticLibrary
{
    public static readonly string[] Names =
    [
        "Amélie", "AMELIE", "amélie 2", "Crème Brûlée", "Brød", "Ærø", "İstanbul", "ÉCOLE", "Ångström", "Straße", "100% Pure",
        "100 Rifles", "Snake_Case", "SnakeXCase", "[Rec]", "Rec Room", "Up^Down", "Alpha", "Alpha Prime", "Beta", "gamma ray",
        "Ωmega", "  padded  ", "x", "naïve café", "Œuvre", "Łódź", "ǅemal", "Film", "film noir"
    ];

    public static readonly string?[] OriginalTitles = [null, null, null, "Le Fabuleux Destin d'Amélie", "ÉMILIE", "Ærøskøbing", "100 Prozent", "snake_case", "Über", "ALPHA"];

    public static readonly string?[] Overviews = [null, string.Empty, " ", "A story.", "Ünïcödé text", "gore and more"];

    public static readonly int?[] Years = [null, 0, 1799, 1800, 1801, 1999, 2000, 2005, 2099, 2100, 2101, 9999];

    public static readonly string[] Genres = ["Drama", "drama", "Comedy", "Sci|Fi", "Sci", "Fi", "Brød", "Ærø", " ", "Crime", "Dräma", "Łódź"];

    public static readonly string[] Tags = ["Gore", "gore", "Keep", "Drop", "a|b", "a", "Brød", "Ærø", "Kids", "ÉTÉ"];

    public static readonly string[] Studios = ["A24", "a24", "Ghibli", "Neon", "Pixar|Disney", "Pixar", "Łódź Films"];

    public static readonly string?[] Ratings = [null, null, "G", "PG", "PG-13", "R", "NC-17", "XYZ"];

    public static readonly float?[] CommunityRatings = [null, 7.1f, MathF.BitDecrement(7.1f), MathF.BitIncrement(7.1f), 0f, 10f, 5.5f, 6.9f, MathF.BitDecrement(6.9f), 8.25f];

    public static readonly int[] Widths = [0, 0, 1, 720, 1199, 1200, 1280, 1899, 1900, 1920, 2499, 2500, 2560, 3799, 3800, 3840, 7680];

    public static readonly string?[] Containers = [null, "mkv", "mov,mp4,m4a,3gp,3g2,mj2", "avi", "MKV", "mp4"];

    public static readonly (string Key, string Value)[][] ProviderSets =
    [
        [], [], [("Tmdb", "603")], [("IMDB", "TT0133093")], [("imdb", "tt0133093")], [("Imdb", " ")], [("Tvdb", "81189"), ("Tmdb", "1399")], [("tvdb", string.Empty)]
    ];

    // Days around the generated date values, and the times of day an item can carry.
    public static readonly DateOnly[] Days =
    [
        new(2025, 12, 31), new(2026, 1, 1), new(2026, 1, 31), new(2026, 2, 1), new(2026, 2, 2), new(2026, 6, 15), new(2026, 10, 2),
        new(2026, 10, 3), new(2026, 11, 1), new(2026, 11, 2), new(1999, 12, 31)
    ];

    /// <summary>The ≤ 10.8 clean form: <c>FormD</c> with non-spacing marks removed, then <c>ToLowerInvariant</c>.</summary>
    public static string OldClean(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var kept = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                kept.Append(c);
            }
        }

        return kept.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>Adds <paramref name="count"/> random rows (and the studios, stale values and series they need).</summary>
    /// <param name="world">The world.</param>
    /// <param name="rng">The seeded generator.</param>
    /// <param name="count">How many movies, episodes, videos and box sets.</param>
    /// <param name="disagreeingUserData">Whether some users' rows of one item disagree (which stops their pushdown).</param>
    public static void Populate(PlannerWorld world, Random rng, int count, bool disagreeingUserData)
    {
        var core = world.Core;
        core.AddStudio("A24");
        core.AddStudio("Ghibli", storedCleanName: OldClean("Ghibli") + " (old)");
        core.AddStudio("Pixar");
        core.AddStudio("Łódź Films", storedCleanName: OldClean("Łódź Films"));

        // Values first created by an older version keep its clean form (shared rows are reused by exact value).
        foreach (var (type, pool) in new[] { (ItemValueType.Genre, Genres), (ItemValueType.Tags, Tags), (ItemValueType.Studios, Studios) })
        {
            foreach (var value in pool.Where(v => !string.IsNullOrWhiteSpace(v) && rng.Next(3) == 0))
            {
                core.SeedValue(type, value, OldClean(value));
            }
        }

        var series = new List<FakeRow>();
        foreach (var i in Enumerable.Range(0, 6))
        {
            series.Add(world.Add(BaseItemKind.Series, Pick(rng, Names), world.Shows, s => Fill(world, rng, s)));
        }

        var movies = new List<FakeRow>();
        for (var i = 0; i < count; i++)
        {
            var roll = rng.Next(100);
            if (roll < 40)
            {
                var library = rng.Next(10) switch { < 6 => world.Movies, 6 or 7 => world.Private, _ => world.Hidden };
                FakeRow? primary = null;
                FakeRow? secondary = null;
                var listOnly = false;
                var row = world.Add(BaseItemKind.Movie, Pick(rng, Names), library, s =>
                {
                    Fill(world, rng, s);
                    if (library == world.Movies && rng.Next(2) == 0)
                    {
                        s.TopParentId = world.FolderB;
                    }

                    // A merged pair ("Merge versions"): the secondary points at the primary and shares its key, in another folder
                    // or library. Every fourth time, core's chain instead: the new item is merged with the pick first, then
                    // the pick is merged into its primary (or, when it is a standalone movie, into another standalone
                    // one), so the new item points at the pick and is keyed by it. Every fourth time a primary only lists
                    // the new item, which keeps its own key.
                    if (movies.Count > 0 && rng.Next(8) == 0)
                    {
                        var pick = movies[rng.Next(movies.Count)];
                        primary = RootOf(core, pick);
                        switch (rng.Next(4))
                        {
                            case 0:
                                secondary = pick;
                                if (primary == pick)
                                {
                                    var others = movies.Where(m => m != pick && m.PrimaryVersionId is null && m.LinkedVersions.Count == 0).ToList();
                                    primary = others.Count == 0 ? null : others[rng.Next(others.Count)];
                                }

                                break;
                            case 1:
                                listOnly = true;
                                break;
                            default:
                                s.PresentationUniqueKey = pick.PresentationUniqueKey;
                                break;
                        }
                    }
                });
                if (primary is not null && secondary is not null)
                {
                    core.MergeVersions(secondary, row);
                    core.MergeVersions(primary, secondary);
                }
                else if (primary is not null && listOnly)
                {
                    core.ListVersion(primary, row);
                }
                else if (primary is not null)
                {
                    core.Merge(primary, row);
                }
                movies.Add(row);
            }
            else if (roll < 70)
            {
                var show = series[rng.Next(series.Count)];
                world.Add(BaseItemKind.Episode, Pick(rng, Names), world.Shows, s =>
                {
                    Fill(world, rng, s);
                    switch (rng.Next(10))
                    {
                        case 0:
                            s.SeriesName = show.Item.Name + " (old)"; // stored before a rename
                            s.Series = show;
                            break;
                        case 1:
                            s.SeriesName = Pick(rng, Names); // no series id
                            break;
                        default:
                            s.Series = show;
                            break;
                    }
                });
            }
            else if (roll < 80)
            {
                world.Add(BaseItemKind.Video, Pick(rng, Names), rng.Next(3) == 0 ? world.Movies : world.Home, s => Fill(world, rng, s));
            }
            else if (roll < 86)
            {
                world.Add(BaseItemKind.MusicVideo, Pick(rng, Names), world.Home, s => Fill(world, rng, s));
            }
            else if (roll < 92)
            {
                world.Add(BaseItemKind.BoxSet, Pick(rng, Names), world.Collections, s => Fill(world, rng, s));
            }
            else if (movies.Count > 0)
            {
                // An extra: no top parent as 10.11 saves it, or one as an older row might have.
                var owner = movies[rng.Next(movies.Count)];
                world.Add(BaseItemKind.Video, Pick(rng, Names), world.Movies, s =>
                {
                    Fill(world, rng, s);
                    s.Owner = owner;
                    s.TopParentId = rng.Next(2) == 0 ? owner.TopParentId : null;
                });
            }
        }

        // Box sets' linked children: none, any movies, or (every third) only movies in Private, which Kid can't open, so
        // BoxSet.IsVisible refuses the set though SQL lets it through.
        var boxSets = core.Rows.Where(r => r.Kind == BaseItemKind.BoxSet).ToList();
        var privateMovies = movies.Where(m => m.TopParentId is { } top && world.Private.PhysicalFolderIds.Contains(top)).ToList();
        for (var b = 0; b < boxSets.Count; b++)
        {
            var hiddenOnly = b % 3 == 0 && privateMovies.Count > 0;
            var pool = hiddenOnly ? privateMovies : movies;
            var children = pool.Count == 0 ? 0 : hiddenOnly ? 1 + rng.Next(3) : rng.Next(4);
            foreach (var child in Enumerable.Range(0, children).Select(_ => pool[rng.Next(pool.Count)]).Distinct())
            {
                boxSets[b].Children.Add(child.Id);
            }
        }

        // A season's tag (Gore, which Kid blocks: SQL doesn't read seasons' tags), and ratings the stored one lags.
        foreach (var row in core.Rows)
        {
            if (row.Kind == BaseItemKind.Episode && rng.Next(8) == 0)
            {
                row.MemoryTags.Add(rng.Next(2) == 0 ? "Gore" : "GORE");
            }

            if (rng.Next(12) == 0)
            {
                row.MemoryScore = Pick(rng, new int?[] { null, 10, 17 });
            }
        }

        if (disagreeingUserData)
        {
            // One item whose user rows disagree, per user: the planner must stop pushing that user's flag.
            var target = movies.Count > 0 ? movies[0] : series[0];
            foreach (var user in new[] { world.Admin, world.Kid })
            {
                target.UserData.Add(new FakeUserData(user.Id, "old-key-" + user.Id.ToString("N")[..4], true, true, 900, 3));
                target.UserData.Add(new FakeUserData(user.Id, target.Id.ToString("N"), false, false, 0));
            }
        }
    }

    private static T Pick<T>(Random rng, IReadOnlyList<T> pool) => pool[rng.Next(pool.Count)];

    // The end of a row's pointer chain.
    private static FakeRow RootOf(FakeCore core, FakeRow row)
    {
        for (var hops = 0; hops < 10 && row.PrimaryVersionId is { } up && core.RowById(Guid.ParseExact(up, "N")) is { } next; hops++)
        {
            row = next;
        }

        return row;
    }

    private static IReadOnlyList<string> Some(Random rng, IReadOnlyList<string> pool)
        => rng.Next(4) == 0 ? [] : Enumerable.Range(0, 1 + rng.Next(3)).Select(_ => Pick(rng, pool)).ToList();

    private static DateTime? Date(PlannerWorld world, Random rng)
    {
        if (rng.Next(6) == 0)
        {
            return null;
        }

        var day = Pick(rng, Days).ToDateTime(TimeOnly.MinValue);
        return rng.Next(5) switch
        {
            0 => DateTime.SpecifyKind(day, DateTimeKind.Utc), // 00:00Z
            1 => DateTime.SpecifyKind(day, DateTimeKind.Unspecified), // a local midnight, stored in the world's zone
            2 => DateTime.SpecifyKind(day - PlannerWorld.West.BaseUtcOffset, DateTimeKind.Utc), // the west zone's midnight
            3 => DateTime.SpecifyKind(day - PlannerWorld.East.BaseUtcOffset, DateTimeKind.Utc), // the east zone's midnight
            _ => DateTime.SpecifyKind(day.AddMinutes(rng.Next(24 * 60)), DateTimeKind.Utc)
        };
    }

    private static void Fill(PlannerWorld world, Random rng, RowSpec s)
    {
        var admin = world.Admin;
        var kid = world.Kid;
        if (rng.Next(8) == 0)
        {
            s.CleanName = OldClean(s.Name);
        }

        s.OriginalTitle = Pick(rng, OriginalTitles);
        s.Overview = Pick(rng, Overviews);
        s.Year = Pick(rng, Years);
        s.PremiereDate = Date(world, rng);
        s.DateCreated = Date(world, rng) ?? new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        s.Genres = Some(rng, Genres);
        s.Tags = Some(rng, Tags);
        s.Studios = Some(rng, Studios);
        s.OfficialRating = Pick(rng, Ratings);
        s.InheritedRating = s.OfficialRating is { } rating && PlannerWorld.Scores.TryGetValue(rating, out var score) ? score : rng.Next(5) == 0 ? 13 : null;
        s.CommunityRating = Pick(rng, CommunityRatings);
        s.CriticRating = rng.Next(3) == 0 ? null : rng.Next(101);
        s.RunTimeTicks = rng.Next(4) == 0 ? null : TimeSpan.FromSeconds(rng.Next(1, 3 * 3600)).Ticks;
        s.Width = Pick(rng, Widths);
        s.Container = Pick(rng, Containers);
        s.IsLocked = rng.Next(4) == 0;
        s.IsVirtual = rng.Next(12) == 0;
        foreach (var provider in Pick(rng, ProviderSets))
        {
            s.Providers.Add(provider);
        }

        foreach (var user in new[] { admin, kid })
        {
            if (rng.Next(3) == 0)
            {
                var ticks = rng.Next(3) == 0 ? rng.Next(1, 1_000_000) : 0;
                s.UserData.Add(new FakeUserData(user.Id, string.Empty, rng.Next(2) == 0, rng.Next(3) == 0, ticks, rng.Next(4), rng.Next(2) == 0 ? new DateTime(2026, 10, 30, 20, 0, 0, DateTimeKind.Utc) : null));
            }
        }
    }

    /// <summary>The people credited in <see cref="AddPeople"/>: case twins, a non-ASCII name, one with no Person item.</summary>
    public static readonly string[] PeopleNames = ["Ridley Scott", "Tony Scott", "ridley scott", "Al Pacino", "Robert De Niro", "Zoë Kravitz", "Nobody Item", "Ann Lee"];

    /// <summary>The credit types written: the field's roles and some it doesn't name.</summary>
    public static readonly string[] CreditTypes = ["Actor", "Actor", "Director", "Writer", "Producer", "Composer", "GuestStar", "Conductor", "Lyricist"];

    /// <summary>
    /// Credits people on the world's rows from a generator of its own (so the other draws stay as they were), and returns the
    /// references a People rule may name: each person's item, a stale id with a known name, an unknown person.
    /// </summary>
    /// <param name="world">The populated world.</param>
    /// <param name="seed">The seed.</param>
    /// <returns>The references.</returns>
    public static List<(Guid Id, string Name)> AddPeople(PlannerWorld world, int seed)
    {
        var rng = new Random(seed);
        var core = world.Core;
        var refs = new List<(Guid Id, string Name)>();
        foreach (var name in PeopleNames.Where(n => n != "Nobody Item"))
        {
            var person = core.AddPerson(name);
            refs.Add((person.Id, person.Name));
        }

        refs.Add((Guid.NewGuid(), "Al Pacino"));
        refs.Add((Guid.NewGuid(), "Nobody Item"));
        refs.Add((Guid.NewGuid(), "Nobody At All"));
        foreach (var row in core.Rows.Where(r => r.Kind is BaseItemKind.Movie or BaseItemKind.Series or BaseItemKind.Episode or BaseItemKind.Video))
        {
            var credits = rng.Next(4);
            for (var c = 0; c < credits; c++)
            {
                row.Credits.Add((Pick(rng, PeopleNames), Pick(rng, CreditTypes)));
            }
        }

        return refs;
    }
}
