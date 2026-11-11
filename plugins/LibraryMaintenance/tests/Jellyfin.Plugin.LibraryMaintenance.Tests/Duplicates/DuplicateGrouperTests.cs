using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Grouping;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Normalization;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Duplicates;

/// <summary>
/// The grouper's required behaviour: both modes, the unknown-year policies, the confidence rules in their order (the
/// precedence rows), the flags, the suggested keep, the reclaimable bytes, the group id and the library scope.
/// </summary>
public sealed class DuplicateGrouperTests
{
    private static readonly Guid LibraryA = new("b0000000-0000-0000-0000-00000000000a");
    private static readonly Guid LibraryB = new("b0000000-0000-0000-0000-00000000000b");
    private static readonly GroupingOptions Title = new() { Mode = MatchMode.Title };
    private static readonly GroupingOptions MatchAny = new() { UnknownYear = UnknownYearPolicy.MatchAny };
    private static readonly long Minutes = TimeSpan.TicksPerMinute;

    private int _next;

    // ------------------------------------------------------------------ the modes

    [Fact]
    public void TitleAndYear_GroupsATitlesMoviesOfOneYear_AndLeavesTheOtherYearOut()
    {
        var a = Movie("Heat", 1995);
        var b = Movie("Heat", 1995) with { LibraryIds = [LibraryB] };
        var result = Group(GroupingOptions.Default, a, b, Movie("Heat", 1986));

        var group = Assert.Single(result.Groups);
        Assert.Equal("heat|1995", group.Key);
        Assert.Equal(MatchMode.TitleAndYear, group.Mode);
        Assert.Equal([a.Id, b.Id], group.Entries.Select(e => e.Id).Order());
        Assert.Equal(GroupConfidence.Likely, group.Confidence);
        Assert.Equal(GroupReasons.TitleAndYear, group.Reasons);
        Assert.Equal(GroupFlags.CrossLibrary, group.Flags);
        Assert.Equal([1995], group.Years);
        Assert.Equal((3, 3, 0), (result.Entries, result.Movies, result.NoTitle));
    }

    [Fact]
    public void TitleOnly_GroupsEveryYearOfATitle_CappedAtPossible()
    {
        var result = Group(Title, Movie("Heat", 1995), Movie("Heat", 1995), Movie("The Heat", 1986), Movie("Heat"));

        var group = Assert.Single(result.Groups);
        Assert.Equal("heat", group.Key);
        Assert.Equal(4, group.Entries.Count);
        Assert.Equal(GroupConfidence.Possible, group.Confidence);
        Assert.Equal(GroupReasons.Title, group.Reasons);
        Assert.Equal(GroupFlags.YearsDiffer | GroupFlags.YearUnknown, group.Flags);
        Assert.Equal([1986, 1995], group.Years);
    }

    [Fact]
    public void TitleOnly_WithOneYear_IsLikely()
    {
        var group = Assert.Single(Group(Title, Movie("Heat", 1995), Movie("Heat", 1995)).Groups);

        Assert.Equal(GroupConfidence.Likely, group.Confidence);
        Assert.Equal(GroupFlags.None, group.Flags);
    }

    [Fact]
    public void TheTitleRules_MakeSpellingsOneKey()
    {
        var result = Group(
            GroupingOptions.Default,
            Movie("Amélie", 2001), Movie("Amelie", 2001),
            Movie("The Matrix", 1999), Movie("Matrix, The", 1999),
            Movie("Schindler's List", 1993), Movie("Schindlers List", 1993),
            Movie("L.A. Confidential", 1997), Movie("LA Confidential", 1997),
            Movie("Blade Runner - The Final Cut", 1982), Movie("Blade Runner", 1982),
            Movie("Mission: Impossible - Fallout", 2018), Movie("Mission Impossible", 2018));

        Assert.Equal(["amelie|2001", "blade runner|1982", "la confidential|1997", "matrix|1999", "schindlers list|1993"], result.Groups.Select(g => g.Key));
        Assert.Equal(GroupFlags.EditionsDiffer, result.Groups.Single(g => g.Key == "blade runner|1982").Flags);
    }

    [Fact]
    public void BladeRunner2049_KeepsItsNumber_AndOnlyAMixedFoldersCutName_MeetsBladeRunner_InTitleMode()
    {
        // An unbracketed year is never cut, so the sequel's own title never meets the original.
        var original = Movie("Blade Runner", 1982);
        Assert.Empty(Group(Title, original, Movie("Blade Runner 2049", 2017)).Groups);

        // An unidentified mixed-folder "Blade Runner 2049.mkv" is stored by Jellyfin as "Blade Runner" with the year 2049.
        var cut = Movie("Blade Runner", 2049) with { Path = "/movies/Blade Runner 2049.mkv", ContainingFolder = "/movies" };
        Assert.Empty(Group(GroupingOptions.Default, original, cut).Groups);
        var group = One(Title, original, cut);
        Assert.Equal(GroupConfidence.Possible, group.Confidence);
        Assert.Equal(GroupFlags.YearsDiffer, group.Flags);
        Assert.Equal([1982, 2049], group.Years);
    }

    [Fact]
    public void AVersionSet_IsOneEntry_AndNeverItsOwnDuplicate()
    {
        var primary = Movie("Heat", 1995, size: 30);
        var version = Movie("Heat", 1995, size: 20);
        var set = Entry(primary, version);
        Assert.Empty(Group(GroupingOptions.Default, [set]).Groups);

        var copy = Movie("Heat", 1995, size: 40);
        var group = Assert.Single(Group(GroupingOptions.Default, [set, Entry(copy)]).Groups);
        Assert.Equal(2, group.Entries.Count);
        Assert.Equal(copy.Id, group.SuggestedKeep.Id);
        Assert.Equal(50, group.ReclaimableBytes);
        Assert.Equal(3, Group(GroupingOptions.Default, [set, Entry(copy)]).Movies);
    }

    // ------------------------------------------------------------------ the unknown year

    [Fact]
    public void UnknownYear_MatchUnknownOnly_GroupsOnlyWithOtherUnknownYears()
    {
        var result = Group(GroupingOptions.Default, Movie("Alien"), Movie("Alien"), Movie("Alien", 1979));

        var group = Assert.Single(result.Groups);
        Assert.Equal("alien|?", group.Key);
        Assert.Equal(2, group.Entries.Count);
        Assert.Equal(GroupConfidence.Possible, group.Confidence);
        Assert.Equal(GroupFlags.YearUnknown, group.Flags);
        Assert.Empty(group.Years);

        // With a shared id the years are unknown, not different: confirmed.
        var shared = Assert.Single(Group(GroupingOptions.Default, Movie("Alien", tmdb: "348"), Movie("Alien", tmdb: "348")).Groups);
        Assert.Equal(GroupConfidence.Confirmed, shared.Confidence);
    }

    [Fact]
    public void UnknownYear_MatchAny_JoinsTheTitlesOnlyYear_CappedAtPossible_EvenWithTheSameId()
    {
        var known = Movie("Alien", 1979, tmdb: "348");
        var unknown = Movie("Alien", tmdb: "348");
        var group = Assert.Single(Group(MatchAny, known, unknown, Movie("Aliens", 1986)).Groups);

        Assert.Equal("alien|1979", group.Key);
        Assert.Equal([known.Id, unknown.Id], group.Entries.Select(e => e.Id).Order());
        Assert.Equal(GroupConfidence.Possible, group.Confidence);
        Assert.Equal(GroupReasons.TitleAndYear | GroupReasons.UnknownYearMatch | GroupReasons.SameTmdbId, group.Reasons);
        Assert.Equal(GroupFlags.YearUnknown, group.Flags);
    }

    [Fact]
    public void UnknownYear_MatchAny_WithSeveralYears_GroupsTheUnknownYearsAlone_Ambiguous()
    {
        var group = Assert.Single(Group(MatchAny, Movie("Dune", 1984), Movie("Dune", 2021), Movie("Dune"), Movie("Dune")).Groups);
        Assert.Equal("dune|?", group.Key);
        Assert.Equal(2, group.Entries.Count);
        Assert.Equal(GroupFlags.YearUnknown | GroupFlags.Ambiguous, group.Flags);
        Assert.Equal(GroupConfidence.Possible, group.Confidence);

        // One movie with no year among several years is no group; under MatchUnknownOnly a pair isn't ambiguous.
        Assert.Empty(Group(MatchAny, Movie("Dune", 1984), Movie("Dune", 2021), Movie("Dune")).Groups);
        Assert.Equal(GroupFlags.YearUnknown, Assert.Single(Group(GroupingOptions.Default, Movie("Dune", 1984), Movie("Dune", 2021), Movie("Dune"), Movie("Dune")).Groups).Flags);

        // Only movies with no year: they group, and nothing is ambiguous.
        Assert.Equal(GroupFlags.YearUnknown, Assert.Single(Group(MatchAny, Movie("Dune"), Movie("Dune")).Groups).Flags);
    }

    [Fact]
    public void TheYear_IsTheProductionYear_ThenThePremieresYear_ThenTheTitlesYear()
    {
        var keys = Group(
            GroupingOptions.Default,
            Movie("Test Pattern (2020)"), Movie("Test Pattern", 2020),
            Movie("Night (1999)") with { PremiereYear = 2001 }, Movie("Night", 2001),
            Movie("Day (1999)") with { ProductionYear = 2003, PremiereYear = 2001 }, Movie("Day", 2003)).Groups.Select(g => g.Key);

        Assert.Equal(["day|2003", "night|2001", "test pattern|2020"], keys);
    }

    // ------------------------------------------------------------------ confidence, in its order

    [Fact]
    public void Step1_DifferentTmdbIds_OrDifferentImdbIds_AreAConflict()
    {
        var tmdb = One(GroupingOptions.Default, Movie("Heat", 1995, tmdb: "949"), Movie("Heat", 1995, tmdb: "1000"));
        Assert.Equal(GroupConfidence.Conflict, tmdb.Confidence);
        Assert.Equal(GroupFlags.DifferentProviderIds, tmdb.Flags);

        var imdb = One(GroupingOptions.Default, Movie("Heat", 1995, tmdb: "949", imdb: "tt0113277"), Movie("Heat", 1995, tmdb: "949", imdb: "tt9999999"));
        Assert.Equal(GroupConfidence.Conflict, imdb.Confidence);
        Assert.Equal(GroupReasons.TitleAndYear | GroupReasons.SameTmdbId, imdb.Reasons);
    }

    [Fact]
    public void Step1_TheConflict_ComesBeforeTheCap()
    {
        var group = One(Title, Movie("The Thing", 1982, tmdb: "1091"), Movie("The Thing", 2011, tmdb: "60935"));

        Assert.Equal(GroupConfidence.Conflict, group.Confidence);
        Assert.Equal(GroupFlags.DifferentProviderIds | GroupFlags.YearsDiffer, group.Flags);
    }

    [Fact]
    public void Step2_TheSameIdWithDifferentYears_IsCappedAtPossible_SameIdYearsDiffer()
    {
        // TMDb resolves remakes to one id: a same-id remake pair is never confirmed.
        var group = One(Title, Movie("The Thing", 1982, tmdb: "1091"), Movie("The Thing", 2011, tmdb: "1091"));

        Assert.Equal(GroupConfidence.Possible, group.Confidence);
        Assert.Equal(GroupFlags.YearsDiffer | GroupFlags.SameIdYearsDiffer, group.Flags);
        Assert.Equal(GroupReasons.Title | GroupReasons.SameTmdbId, group.Reasons);
    }

    [Theory]
    [InlineData(100, 130, true)] // 30 min, 23 % of the longer
    [InlineData(100, 112, false)] // 12 min but 10.7 %
    [InlineData(50, 59, false)] // 15.3 % but 9 min
    [InlineData(85, 100, false)] // exactly 15 %: not more than
    [InlineData(60, 71, true)] // 11 min, 15.5 %
    [InlineData(60, 70.5, false)] // 10.5 min but 14.9 %
    [InlineData(0, 130, false)] // a runtime unknown
    public void Step2_RuntimesDifferingByMoreThan15PercentAndMoreThan10Minutes_CapTheGroup(double first, double second, bool capped)
    {
        var a = Movie("Heat", 1995, tmdb: "949") with { RunTimeTicks = (long)(first * Minutes) };
        var b = Movie("Heat", 1995, tmdb: "949") with { RunTimeTicks = (long)(second * Minutes) };
        var group = One(GroupingOptions.Default, a, b);

        var longest = Math.Max(first, second);
        var expected = first > 0 && second > 0 && Math.Abs(first - second) > 10 && Math.Abs(first - second) > 0.15 * longest;
        Assert.Equal(expected, capped);
        Assert.Equal(capped ? GroupConfidence.Possible : GroupConfidence.Confirmed, group.Confidence);
        Assert.Equal(capped ? GroupFlags.RuntimesDiffer : GroupFlags.None, group.Flags);
    }

    [Fact]
    public void Step2_RuntimesDiffer_ButTheEditionsDo_IsNoCap()
    {
        var cut = Movie("Blade Runner - The Final Cut", 1982, tmdb: "78") with { RunTimeTicks = 150 * Minutes };
        var theatrical = Movie("Blade Runner", 1982, tmdb: "78") with { RunTimeTicks = 117 * Minutes };
        var group = One(GroupingOptions.Default, cut, theatrical);

        Assert.Equal(GroupConfidence.Confirmed, group.Confidence);
        Assert.Equal(GroupFlags.EditionsDiffer, group.Flags);
    }

    [Fact]
    public void Step2_TheEditionExemption_IsAPairs_NeverTheGroups()
    {
        // An edition spares its pair, never the group: two Crashes with no edition, 90 and 140 minutes, and a Director's Cut of 95. The cut
        // differs from neither by its edition's right; the two without one still differ by 50 minutes, so the cap applies.
        var three = One(GroupingOptions.Default, Movie("Crash", 2004) with { RunTimeTicks = 90 * Minutes }, Movie("Crash", 2004) with { RunTimeTicks = 140 * Minutes }, Movie("Crash - Director's Cut", 2004) with { RunTimeTicks = 95 * Minutes });
        Assert.Equal(GroupConfidence.Possible, three.Confidence);
        Assert.Equal(GroupFlags.RuntimesDiffer | GroupFlags.EditionsDiffer, three.Flags);

        // Each pair of an edition with no edition is exempt, so these aren't capped; nor two of the same edition close in time.
        var pairs = One(GroupingOptions.Default, Movie("Crash", 2004) with { RunTimeTicks = 90 * Minutes }, Movie("Crash - Director's Cut", 2004) with { RunTimeTicks = 140 * Minutes }, Movie("Crash - Director's Cut", 2004) with { RunTimeTicks = 145 * Minutes });
        Assert.Equal(GroupConfidence.Likely, pairs.Confidence);
        Assert.Equal(GroupFlags.EditionsDiffer, pairs.Flags);

        // Two of one edition far apart are capped, whatever the others.
        var cuts = One(GroupingOptions.Default, Movie("Crash", 2004) with { RunTimeTicks = 112 * Minutes }, Movie("Crash - Director's Cut", 2004) with { RunTimeTicks = 90 * Minutes }, Movie("Crash {edition-Director's Cut}", 2004) with { RunTimeTicks = 140 * Minutes });
        Assert.Equal(GroupConfidence.Possible, cuts.Confidence);
        Assert.True(cuts.Flags.HasFlag(GroupFlags.RuntimesDiffer));
    }

    [Fact]
    public void Step3_TwoEntriesSharingAnId_AndNoOtherId_AreConfirmed()
    {
        Assert.Equal(GroupConfidence.Confirmed, One(GroupingOptions.Default, Movie("Heat", 1995, tmdb: "949"), Movie("Heat", 1995, tmdb: "949"), Movie("Heat", 1995)).Confidence);
        var imdb = One(GroupingOptions.Default, Movie("Heat", 1995, imdb: "tt0113277"), Movie("Heat", 1995, imdb: "TT0113277 "));
        Assert.Equal(GroupConfidence.Confirmed, imdb.Confidence);
        Assert.Equal(GroupReasons.TitleAndYear | GroupReasons.SameImdbId, imdb.Reasons);

        // A set takes its first member's id when its representative has none.
        var set = Entry(Movie("Heat", 1995), Movie("Heat", 1995, tmdb: "949"));
        Assert.Equal(GroupConfidence.Confirmed, Assert.Single(Group(GroupingOptions.Default, [set, Entry(Movie("Heat", 1995, tmdb: "949"))]).Groups).Confidence);
    }

    [Fact]
    public void Step3_ASharedId_WithOneYearUnknown_IsConfirmed_InTitleMode()
    {
        // A shared id with one unknown year is Confirmed: one known year and an unknown one don't differ. Under
        // MatchAny the same pair is an unknown-year match, which step 2 caps (UnknownYear_MatchAny_JoinsTheTitlesOnlyYear…).
        var group = One(Title, Movie("Heat", 1995, tmdb: "949"), Movie("Heat", tmdb: "949"));

        Assert.Equal(GroupConfidence.Confirmed, group.Confidence);
        Assert.Equal(GroupFlags.YearUnknown, group.Flags);
        Assert.Equal(GroupReasons.Title | GroupReasons.SameTmdbId, group.Reasons);
    }

    [Fact]
    public void Step4_TheSameKnownYear_WithNoSharedId_IsLikely()
    {
        Assert.Equal(GroupConfidence.Likely, One(GroupingOptions.Default, Movie("Heat", 1995, tmdb: "949"), Movie("Heat", 1995)).Confidence);
        Assert.Equal(GroupConfidence.Likely, One(GroupingOptions.Default, Movie("Heat", 1995, tmdb: "949"), Movie("Heat", 1995, imdb: "tt0113277")).Confidence);
        Assert.Equal(GroupConfidence.Likely, One(GroupingOptions.Default, Movie("Heat", 1995), Movie("Heat", 1995)).Confidence);
    }

    [Fact]
    public void Step5_EverythingElse_IsPossible()
    {
        Assert.Equal(GroupConfidence.Possible, One(GroupingOptions.Default, Movie("Heat"), Movie("Heat")).Confidence);
        Assert.Equal(GroupConfidence.Possible, One(GroupingOptions.Default, Movie("Heat", tmdb: "949"), Movie("Heat")).Confidence);

        // Title only: one known year and one unknown is no "same known year".
        var mixed = One(Title, Movie("Heat", 1995), Movie("Heat"));
        Assert.Equal(GroupConfidence.Possible, mixed.Confidence);
        Assert.Equal(GroupFlags.YearUnknown, mixed.Flags);
    }

    // ------------------------------------------------------------------ the flags

    [Fact]
    public void CrossLibrary_WhenNoOneLibraryHoldsEveryEntry()
    {
        Assert.True(One(GroupingOptions.Default, Movie("Heat", 1995), Movie("Heat", 1995) with { LibraryIds = [LibraryB] }).Flags.HasFlag(GroupFlags.CrossLibrary));
        Assert.False(One(GroupingOptions.Default, Movie("Heat", 1995) with { LibraryIds = [LibraryA, LibraryB] }, Movie("Heat", 1995) with { LibraryIds = [LibraryB] }).Flags.HasFlag(GroupFlags.CrossLibrary));
    }

    [Theory]
    [InlineData(1)] // Orphaned
    [InlineData(2)] // StaleLinks
    [InlineData(4)] // Chained
    [InlineData(8)] // Unlisted
    [InlineData(16)] // ListedNotPointing
    [InlineData(32)] // Cycle
    [InlineData(4 | 2)] // Chained and StaleLinks
    public void EveryVersionState_FlagsTheGroup_BrokenMerge(int value)
    {
        // Every broken version-set state (VersionSetFixtureTests makes each one entry) flags its group.
        var state = (VersionSetState)value;
        Assert.True(Enum.IsDefined(state) || state == (VersionSetState.Chained | VersionSetState.StaleLinks));
        var broken = Entry(Movie("Heat", 1995), Movie("Heat", 1995)) with { State = state };
        var group = Assert.Single(Group(GroupingOptions.Default, [broken, Entry(Movie("Heat", 1995))]).Groups);

        Assert.Equal(2, group.Entries.Count);
        Assert.Equal(GroupFlags.BrokenMerge, group.Flags);
    }

    [Fact]
    public void TheMembersFlags_BrokenMerge_DiscRip_Shortcut_Placeholder()
    {
        var broken = Entry(Movie("Heat", 1995)) with { State = VersionSetState.Orphaned };
        Assert.Equal(GroupFlags.BrokenMerge, Assert.Single(Group(GroupingOptions.Default, [broken, Entry(Movie("Heat", 1995))]).Groups).Flags);
        Assert.Equal(GroupFlags.None, One(GroupingOptions.Default, Movie("Heat", 1995), Movie("Heat", 1995)).Flags);

        // Any member counts, not only the representative.
        var rip = Entry(Movie("Heat", 1995), Movie("Heat", 1995) with { VideoType = VideoType.BluRay });
        Assert.Equal(GroupFlags.ContainsDiscRip, Assert.Single(Group(GroupingOptions.Default, [rip, Entry(Movie("Heat", 1995))]).Groups).Flags);
        Assert.Equal(GroupFlags.ContainsShortcut, One(GroupingOptions.Default, Movie("Heat", 1995) with { IsShortcut = true }, Movie("Heat", 1995)).Flags);
        Assert.Equal(GroupFlags.ContainsPlaceholder, One(GroupingOptions.Default, Movie("Heat", 1995) with { IsPlaceHolder = true }, Movie("Heat", 1995)).Flags);
    }

    // ------------------------------------------------------------------ the suggested keep, the bytes

    [Fact]
    public void TheSuggestedKeep_TakesEachRuleInTurn()
    {
        // The suggested keep's rules in order: a real file, a video file, the width, ids, the bitrate, the size, the
        // sources, the older date added, the lower id. Each pair differs in the rule that decides and in a later one that
        // would decide the other way.
        MovieRecord Plain() => Movie("Heat", 1995) with { Width = 1920, TotalBitrate = 10, Size = 100, DateCreated = new DateTime(2020, 1, 1) };

        KeepIs(Plain() with { IsShortcut = true, Path = "/movies/heat.strm", Tmdb = "949", Width = 3840 }, Plain());
        KeepIs(Plain() with { IsPlaceHolder = true, Imdb = "tt0113277", Width = 3840 }, Plain());
        KeepIs(Plain() with { VideoType = VideoType.Iso, Width = 3840 }, Plain());
        KeepIs(Plain() with { Tmdb = "949" }, Plain() with { Width = 3840 });
        KeepIs(Plain() with { TotalBitrate = 99 }, Plain() with { Tmdb = "949" });
        KeepIs(Plain() with { TotalBitrate = 99 }, Plain() with { Imdb = "tt0113277" });
        KeepIs(Plain() with { Size = 999 }, Plain() with { TotalBitrate = 20 });
        var bigger = Entry(Plain() with { Size = 200 });
        Assert.Equal(bigger.Representative.Id, Keep(Entry(Plain()) with { SourceCount = 2 }, bigger));
        var older = Entry(Plain() with { DateCreated = new DateTime(2000, 1, 1) });
        var sources = Entry(Plain()) with { SourceCount = 2 };
        Assert.Equal(sources.Representative.Id, Keep(older, sources));
        KeepIs(Plain(), Plain() with { DateCreated = new DateTime(2010, 1, 1) });

        // A tie: the lower id, whichever comes first.
        var low = Plain() with { Id = new Guid("00000000-0000-0000-0000-000000000001") };
        var high = Plain() with { Id = new Guid("00000000-0000-0000-0000-000000000002") };
        Assert.Equal(low.Id, Keep(Entry(low), Entry(high)));
        Assert.Equal(low.Id, Keep(Entry(high), Entry(low)));
    }

    [Fact]
    public void TheSuggestedKeep_NeverAShortcutOrAPlaceholder_NorASmallerPictureForItsIds()
    {
        // A real file before ids, two cases. A .strm with ids and no size against the real 4K file without ids: the file.
        var real = Movie("Heat", 1995, size: 30_000_000_000) with { Width = 3840, TotalBitrate = 60_000_000 };
        var strm = Movie("Heat", 1995, tmdb: "949", size: null) with { IsShortcut = true, Path = "/movies/heat.strm" };
        Assert.Equal(real.Id, One(GroupingOptions.Default, strm, real).SuggestedKeep.Id);
        Assert.Equal(real.Id, One(GroupingOptions.Default, real, strm).SuggestedKeep.Id);

        // A 720-wide DVD rip with ids against an unidentified 3840 remux: the remux, as a file and as a disc folder.
        var remux = Movie("Heat", 1995, size: 60_000_000_000) with { Width = 3840 };
        var dvdFile = Movie("Heat", 1995, tmdb: "949", size: 4_000_000_000) with { Width = 720 };
        var dvdFolder = dvdFile with { Id = Guid.NewGuid(), VideoType = VideoType.Dvd };
        Assert.Equal(remux.Id, One(GroupingOptions.Default, dvdFile, remux).SuggestedKeep.Id);
        Assert.Equal(remux.Id, One(GroupingOptions.Default, dvdFolder, remux).SuggestedKeep.Id);

        // Ids still decide between two of the same picture.
        var identified = remux with { Id = Guid.NewGuid(), Tmdb = "949", Size = 1 };
        Assert.Equal(identified.Id, One(GroupingOptions.Default, remux, identified).SuggestedKeep.Id);
    }

    [Fact]
    public void TheSuggestedKeep_NeverA0ByteFile_ButAnUnknownSizeCounts()
    {
        // A 0-byte file with ids against a real file without them, neither width probed: the file.
        var real = Movie("Heat", 1995, size: 8_000_000_000) with { Width = null };
        var empty = Movie("Heat", 1995, tmdb: "949", size: 0) with { Width = null };
        Assert.Equal(real.Id, One(GroupingOptions.Default, empty, real).SuggestedKeep.Id);
        Assert.Equal(real.Id, One(GroupingOptions.Default, real, empty).SuggestedKeep.Id);

        // And a 0-byte file with a wider picture (probed before it was truncated) against a real 1080p file: the real one.
        var hd = Movie("Heat", 1995, size: 4_000_000_000) with { Width = 1920 };
        var truncated = Movie("Heat", 1995, size: 0) with { Width = 3840 };
        Assert.Equal(hd.Id, One(GroupingOptions.Default, truncated, hd).SuggestedKeep.Id);
        Assert.Equal(hd.Id, One(GroupingOptions.Default, hd, truncated).SuggestedKeep.Id);

        // A disc folder has no size in Jellyfin (it sets Size only for a file): still real, so its width decides.
        var disc = Movie("Heat", 1995, size: null) with { Width = 3840, VideoType = VideoType.BluRay };
        var file720 = Movie("Heat", 1995, size: 2_000_000_000) with { Width = 1280, VideoType = VideoType.BluRay };
        Assert.Equal(disc.Id, One(GroupingOptions.Default, file720, disc).SuggestedKeep.Id);

        // Two 0-byte files: the other rules decide between them, as between two shortcuts.
        var wide = Movie("Heat", 1995, size: 0) with { Width = 3840 };
        var narrow = Movie("Heat", 1995, size: 0) with { Width = 1280 };
        Assert.Equal(wide.Id, One(GroupingOptions.Default, narrow, wide).SuggestedKeep.Id);
    }

    [Fact]
    public void TheGroup_ListsTheKeepFirst_ThenTheOthersInTheKeepsOrder_AndIsNamedAfterIt()
    {
        var small = Movie("Heat", 1995, size: 10);
        var large = Movie("HEAT", 1995, size: 30);
        var middle = Movie("Heat", 1995, size: 20);
        var group = One(GroupingOptions.Default, small, large, middle);

        Assert.Equal([large.Id, middle.Id, small.Id], group.Entries.Select(e => e.Id));
        Assert.Equal("HEAT", group.DisplayTitle);
        Assert.Same(group.Entries[0], group.SuggestedKeep);
    }

    [Fact]
    public void Reclaimable_IsEveryOtherEntrysBytes_AtLeastWhenTheIndexDoesntKnowThemAll()
    {
        var keep = Movie("Heat", 1995, size: 100);
        var set = Entry(Movie("Heat", 1995, size: 30), Movie("Heat", 1995, size: 20));
        var other = Entry(Movie("Heat", 1995, size: 10));
        var group = Assert.Single(Group(GroupingOptions.Default, [Entry(keep), set, other]).Groups);
        Assert.Equal(60, group.ReclaimableBytes);
        Assert.False(group.ReclaimableIsLowerBound);

        foreach (var unknown in new[]
        {
            Entry(Movie("Heat", 1995) with { Size = null }),
            Entry(Movie("Heat", 1995, size: 10) with { LocalAlternateCount = 1 }),
            Entry(Movie("Heat", 1995, size: 10) with { PartCount = 2 }),
        })
        {
            var atLeast = Assert.Single(Group(GroupingOptions.Default, [Entry(keep), unknown]).Groups);
            Assert.True(atLeast.ReclaimableIsLowerBound);
        }

        // A member outside the index (a primary in a library the settings leave out) is in no caller's scope: it shows
        // nothing, not even a "≥".
        var external = Assert.Single(Group(GroupingOptions.Default, [Entry(keep), Entry(Movie("Heat", 1995, size: 10)) with { ExternalIds = [Guid.NewGuid()], SourceCount = 2 }]).Groups);
        Assert.Equal((10L, false), (external.ReclaimableBytes, external.ReclaimableIsLowerBound));
        Assert.Equal(1, external.Entries.Single(e => e.Id != keep.Id).SourceCount);

        // The keep's own unknown bytes aren't reclaimed.
        var keptAlternates = Assert.Single(Group(GroupingOptions.Default, [Entry(keep with { LocalAlternateCount = 2 }), other]).Groups);
        Assert.Equal((10L, false), (keptAlternates.ReclaimableBytes, keptAlternates.ReclaimableIsLowerBound));
    }

    // ------------------------------------------------------------------ the id, the scope, the rest

    [Fact]
    public void TheGroupId_IsSha256OverTheModeAndTheSortedRepresentativeIds_16HexDigits()
    {
        // b is the keep, so the entries' order isn't the ids' order.
        var a = Movie("Heat", 1995, size: 10);
        var b = Movie("Heat", 1995, size: 20);
        var group = One(GroupingOptions.Default, b, a);
        Assert.Equal([b.Id, a.Id], group.Entries.Select(e => e.Id));

        var text = "TitleAndYear:" + string.Join(',', new[] { a.Id, b.Id }.Select(i => i.ToString("N", CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal));
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
        Assert.Equal(expected, group.Id);
        Assert.Matches("^[0-9a-f]{16}$", group.Id);
        Assert.Equal(group.Id, One(GroupingOptions.Default, a, b).Id);
        Assert.NotEqual(group.Id, One(Title, a, b).Id);
        Assert.NotEqual(group.Id, One(GroupingOptions.Default, a, b, Movie("Heat", 1995)).Id);
    }

    [Fact]
    public void TheLibraryScope_TakesAnEntryWithAnyMemberInIt()
    {
        var inA = Movie("Heat", 1995);
        var inB = Movie("Heat", 1995) with { LibraryIds = [LibraryB] };
        var both = Movie("Heat", 1995) with { LibraryIds = [LibraryA, LibraryB] };
        var scope = new HashSet<Guid> { LibraryB };

        var result = DuplicateGrouper.Group([Entry(inA), Entry(inB), Entry(both)], GroupingOptions.Default, scope);
        Assert.Equal(new[] { both.Id, inB.Id }.Order(), Assert.Single(result.Groups).Entries.Select(e => e.Id).Order());
        Assert.Equal(2, result.Entries);
        Assert.Empty(DuplicateGrouper.Group([Entry(inA), Entry(inB)], GroupingOptions.Default, scope).Groups);
    }

    [Fact]
    public void TheLibraryScope_ShowsOnlyTheMembersInIt_AndNothingOfTheOthers()
    {
        // The caller's view of a set. A set: its root (the keep's choice: 4K, ids, 900 bytes) in B, a version in A, a broken link
        // to it. A copy in A. Seen from A: the set is its version alone (the representative, its id, its 10 bytes, one
        // source, A only, no broken state); the root's ids don't confirm the group; the copy is the keep.
        var root = Movie("Heat", 1995, tmdb: "949", size: 900) with { Width = 3840, LibraryIds = [LibraryB], LocalAlternateCount = 1 };
        var version = Movie("Heat", 1995, size: 10);
        var copy = Movie("Heat", 1995, size: 50);
        var set = Entry(root, version) with { State = VersionSetState.StaleLinks | VersionSetState.Chained, ChainedIds = [version.Id], SourceCount = 3 };
        var entries = new[] { set, Entry(copy) };

        var whole = Assert.Single(DuplicateGrouper.Group(entries, GroupingOptions.Default).Groups);
        Assert.Equal(root.Id, whole.SuggestedKeep.Id);
        Assert.Equal(GroupFlags.BrokenMerge, whole.Flags);
        Assert.Equal((50L, false), (whole.ReclaimableBytes, whole.ReclaimableIsLowerBound));

        var scope = new HashSet<Guid> { LibraryA };
        var result = DuplicateGrouper.Group(entries, GroupingOptions.Default, scope);
        var group = Assert.Single(result.Groups);
        Assert.Equal((2, 2), (result.Entries, result.Movies));
        Assert.Equal(copy.Id, group.SuggestedKeep.Id);
        Assert.Equal(GroupConfidence.Likely, group.Confidence);
        Assert.Equal(GroupFlags.None, group.Flags);
        Assert.Equal((10L, false), (group.ReclaimableBytes, group.ReclaimableIsLowerBound));
        var seen = group.Entries.Single(e => e.Id != copy.Id);
        Assert.Equal(version.Id, seen.Representative.Id);
        Assert.Equal([version.Id], seen.Members.Select(m => m.Id));
        Assert.Equal([LibraryA], seen.LibraryIds);
        Assert.Equal(1, seen.SourceCount);
        Assert.Equal(VersionSetState.None, seen.State);
        Assert.Null(seen.Tmdb);
        Assert.DoesNotContain(group.Entries.SelectMany(e => e.Members), m => m.Id == root.Id);

        // Seen from B: the root alone; the copy and the version aren't B's, so there is no group.
        Assert.Empty(DuplicateGrouper.Group(entries, GroupingOptions.Default, new HashSet<Guid> { LibraryB }).Groups);

        // The group id is the caller's representatives', so the two views differ.
        Assert.NotEqual(whole.Id, group.Id);
    }

    [Fact]
    public void TheLibraryScope_KeepsABrokenStateOnlyWhenItsMembersAreInIt()
    {
        // The root in A, a chained member in B, an unlisted one in A: seen from A, unlisted (and the root's own stale links)
        // but not chained; seen from B, where the root isn't, nothing.
        var root = Movie("Heat", 1995);
        var chained = Movie("Heat", 1995) with { LibraryIds = [LibraryB] };
        var unlisted = Movie("Heat", 1995);
        var set = Entry(root, chained, unlisted) with
        {
            State = VersionSetState.StaleLinks | VersionSetState.Chained | VersionSetState.Unlisted,
            ChainedIds = [chained.Id],
            UnlistedIds = [unlisted.Id]
        };

        var fromA = GroupedEntry.From(set, NormalizationOptions.Default, new HashSet<Guid> { LibraryA })!;
        Assert.Equal(VersionSetState.StaleLinks | VersionSetState.Unlisted, fromA.State);
        Assert.Equal([root.Id, unlisted.Id], fromA.Members.Select(m => m.Id));
        var fromB = GroupedEntry.From(set, NormalizationOptions.Default, new HashSet<Guid> { LibraryB })!;
        Assert.Equal(VersionSetState.None, fromB.State);
        Assert.Equal(chained.Id, fromB.Representative.Id);
        Assert.Null(GroupedEntry.From(set, NormalizationOptions.Default, new HashSet<Guid> { Guid.NewGuid() }));

        // Narrowing the projection over every library gives the same views.
        var full = GroupedEntry.From(set, NormalizationOptions.Default);
        Assert.Equal(fromA.State, GroupedEntry.Narrow(full, new HashSet<Guid> { LibraryA }, NormalizationOptions.Default)!.State);
        Assert.Same(full, GroupedEntry.Narrow(full, null, NormalizationOptions.Default));
        Assert.Same(full, GroupedEntry.Narrow(full, new HashSet<Guid> { LibraryA, LibraryB }, NormalizationOptions.Default));
    }

    [Fact]
    public void CrossLibrary_IsOverTheCallersLibraries()
    {
        // Cross-library from the caller's view: a in A and B, b in B and C; the caller sees A and C, where no one library holds both.
        var libraryC = Guid.NewGuid();
        var a = Movie("Heat", 1995) with { LibraryIds = [LibraryA, LibraryB] };
        var b = Movie("Heat", 1995) with { LibraryIds = [LibraryB, libraryC] };

        Assert.False(One(GroupingOptions.Default, a, b).Flags.HasFlag(GroupFlags.CrossLibrary));
        var scoped = Assert.Single(DuplicateGrouper.Group([Entry(a), Entry(b)], GroupingOptions.Default, new HashSet<Guid> { LibraryA, libraryC }).Groups);
        Assert.True(scoped.Flags.HasFlag(GroupFlags.CrossLibrary));
        Assert.DoesNotContain(scoped.Entries.SelectMany(e => e.LibraryIds), id => id == LibraryB);
    }

    [Fact]
    public void TheGroups_TheirIds_AndTheirKeeps_DontDependOnTheInputsOrder()
    {
        // The representative, the keep and the group id are the same however the entries come.
        var root = Movie("Heat", 1995, size: 20);
        var version = Movie("Heat", 1995, size: 20);
        var tie = Movie("Heat", 1995, size: 20);
        var alien = Movie("Alien", 1979);
        var alienCopy = Movie("Alien", 1979);
        var set = Entry(root, version);
        VersionEntry[] entries = [set, Entry(tie), Entry(alien), Entry(alienCopy)];

        var first = DuplicateGrouper.Group(entries, GroupingOptions.Default);
        foreach (var order in new VersionEntry[][] { Enumerable.Reverse(entries).ToArray(), [entries[2], entries[0], entries[3], entries[1]] })
        {
            var again = DuplicateGrouper.Group(order, GroupingOptions.Default);
            Assert.Equal(first.Groups.Select(g => (g.Id, g.Key, g.SuggestedKeep.Id)), again.Groups.Select(g => (g.Id, g.Key, g.SuggestedKeep.Id)));
            Assert.Equal(first.Groups.SelectMany(g => g.Entries.Select(e => e.Id)), again.Groups.SelectMany(g => g.Entries.Select(e => e.Id)));
        }
    }

    [Fact]
    public void AnUnknownYearPair_SharingAnId_AmongSeveralYears_IsConfirmed_AndAmbiguous()
    {
        // Ambiguous isn't one of step 2's caps, so the pair that shares an id is Confirmed; the page shows both
        // the confidence and the flag (Confirmed and Ambiguous at once).
        var result = Group(MatchAny, Movie("Heat", tmdb: "949"), Movie("Heat", tmdb: "949"), Movie("Heat", 1995), Movie("Heat", 1986));
        var group = Assert.Single(result.Groups);

        Assert.Equal("heat|?", group.Key);
        Assert.Equal(GroupConfidence.Confirmed, group.Confidence);
        Assert.Equal(GroupFlags.Ambiguous | GroupFlags.YearUnknown, group.Flags);
    }

    [Fact]
    public void TheOtherModesCount_IsTheGroupsItWouldMake_FromTheSameBuckets()
    {
        var movies = new[] { Movie("Heat", 1995), Movie("Heat", 1995), Movie("Heat", 1986), Movie("Heat", 1986), Movie("Alien", 1979), Movie("Alien"), Movie("Alien") };
        var projected = DuplicateGrouper.Project(movies.Select(m => Entry(m)).ToList(), NormalizationOptions.Default);
        var buckets = DuplicateGrouper.Bucket(projected, null, NormalizationOptions.Default);

        foreach (var options in new[] { GroupingOptions.Default, Title, MatchAny, MatchAny with { Mode = MatchMode.Title } })
        {
            Assert.Equal(DuplicateGrouper.Group(buckets, options).Groups.Count, DuplicateGrouper.Count(buckets, options));
            Assert.Equal(DuplicateGrouper.Group(movies.Select(m => Entry(m)).ToList(), options).Groups.Select(g => g.Id), DuplicateGrouper.Group(buckets, options).Groups.Select(g => g.Id));
        }

        Assert.Equal((3, 2), (DuplicateGrouper.Count(buckets, GroupingOptions.Default), DuplicateGrouper.Count(buckets, Title)));
    }

    [Fact]
    public void AnEntryWithNoTitle_NeverGroups()
    {
        var result = Group(Title, Movie(string.Empty), Movie("   "), Movie(" \t "));

        Assert.Empty(result.Groups);
        Assert.Equal((0, 3), (result.Entries, result.NoTitle));
    }

    [Fact]
    public void AReleaseName_IsCleanedUp_OnlyWithTheRuleOn_AndOnlyWithNoId_AsItsFileName()
    {
        var cleanup = GroupingOptions.Default with { Normalization = new NormalizationOptions { ReleaseNameCleanup = true, ReleaseNameMaxYear = 2027 } };
        var release = Movie("curse.of.chucky.2013.unrated.1080p") with { Path = "/movies/curse.of.chucky.2013.unrated.1080p.mkv", ContainingFolder = "/movies" };
        var named = Movie("Curse of Chucky", 2013);

        var group = One(cleanup, release, named);
        Assert.Equal("curse of chucky|2013", group.Key);
        Assert.Equal(GroupFlags.EditionsDiffer, group.Flags);
        Assert.Empty(Group(GroupingOptions.Default, release, named).Groups);
        Assert.Empty(Group(cleanup, release with { Imdb = "tt2230358" }, named).Groups);
        Assert.Empty(Group(cleanup, release with { Path = "/movies/chucky.mkv" }, named).Groups);

        // Its folder's name counts too.
        Assert.Single(Group(cleanup, release with { Path = "/movies/curse.of.chucky.2013.unrated.1080p/movie.mkv", ContainingFolder = "/movies/curse.of.chucky.2013.unrated.1080p" }, named).Groups);
    }

    [Fact]
    public void TheGroups_ComeByKeyThenId()
    {
        var keys = Group(Title, Movie("Zodiac"), Movie("Zodiac"), Movie("Alien"), Movie("Alien"), Movie("Heat"), Movie("Heat")).Groups.Select(g => g.Key);

        Assert.Equal(["alien", "heat", "zodiac"], keys);
    }

    [Fact]
    public void TheOptions_FromTheSettings()
    {
        var defaults = GroupingOptions.FromConfiguration(new PluginConfiguration(), new DateTime(2026, 11, 1));
        Assert.Equal((MatchMode.TitleAndYear, UnknownYearPolicy.MatchUnknownOnly), (defaults.Mode, defaults.UnknownYear));
        Assert.True(defaults.Normalization.StripEditionSuffixes);
        Assert.Equal(NormalizationOptions.DefaultEditionPhrases, defaults.Normalization.EditionPhrases);
        Assert.Equal(["En"], defaults.Normalization.ArticleLanguages);
        Assert.Equal(2027, defaults.Normalization.ReleaseNameMaxYear);
        Assert.False(defaults.Normalization.ReleaseNameCleanup || defaults.Normalization.RomanNumerals || defaults.Normalization.IgnoreSpaces);

        var set = GroupingOptions.FromConfiguration(
            new PluginConfiguration { DefaultMatchMode = "title", UnknownYearPolicy = "MATCHANY", StripEditionSuffixes = false, EditionPhrases = ["redux"], ArticleLanguages = ["En", "Fr"] },
            new DateTime(2026, 11, 1));
        Assert.Equal((MatchMode.Title, UnknownYearPolicy.MatchAny, false), (set.Mode, set.UnknownYear, set.Normalization.StripEditionSuffixes));
        Assert.Equal(["redux"], set.Normalization.EditionPhrases);
        Assert.Equal(["En", "Fr"], set.Normalization.ArticleLanguages);

        // A name a newer build wrote, a number (even one that is a value) or a list takes the default.
        var unknown = GroupingOptions.FromConfiguration(new PluginConfiguration { DefaultMatchMode = "Fuzzy", UnknownYearPolicy = "7" }, new DateTime(2026, 11, 1));
        Assert.Equal((MatchMode.TitleAndYear, UnknownYearPolicy.MatchUnknownOnly), (unknown.Mode, unknown.UnknownYear));
        var numbers = GroupingOptions.FromConfiguration(new PluginConfiguration { DefaultMatchMode = "1", UnknownYearPolicy = "MatchAny, MatchUnknownOnly" }, new DateTime(2026, 11, 1));
        Assert.Equal((MatchMode.TitleAndYear, UnknownYearPolicy.MatchUnknownOnly), (numbers.Mode, numbers.UnknownYear));
        Assert.True(GroupingOptions.TryParseName<MatchMode>(" Title ", out var trimmed));
        Assert.Equal(MatchMode.Title, trimmed);
        Assert.False(GroupingOptions.TryParseName<MatchMode>(null, out _));
        Assert.False(GroupingOptions.TryParseName<MatchMode>("  ", out _));
    }

    // ------------------------------------------------------------------ helpers

    private static GroupingResult Group(GroupingOptions options, params MovieRecord[] movies)
        => DuplicateGrouper.Group(movies.Select(m => Entry(m)).ToList(), options);

    private static GroupingResult Group(GroupingOptions options, IReadOnlyList<VersionEntry> entries)
        => DuplicateGrouper.Group(entries, options);

    private static DuplicateGroup One(GroupingOptions options, params MovieRecord[] movies)
        => Assert.Single(Group(options, movies).Groups);

    // The better keep comes second, so that the input's order can't make it the keep.
    private static void KeepIs(MovieRecord worse, MovieRecord better) => Assert.Equal(better.Id, Keep(Entry(worse), Entry(better)));

    private static Guid Keep(VersionEntry a, VersionEntry b)
    {
        var group = Assert.Single(Group(GroupingOptions.Default, [a, b]).Groups);
        Assert.Equal(-1, Math.Sign(DuplicateGrouper.CompareKeep(group.Entries[0], group.Entries[1])));
        Assert.Equal(1, Math.Sign(DuplicateGrouper.CompareKeep(group.Entries[1], group.Entries[0])));
        return group.SuggestedKeep.Id;
    }

    private static VersionEntry Entry(MovieRecord representative, params MovieRecord[] others)
    {
        MovieRecord[] members = [representative, .. others];
        return new VersionEntry(
            representative,
            members,
            members.Length,
            VersionSetState.None,
            [],
            [],
            members.SelectMany(m => m.LibraryIds).Distinct().ToList(),
            members.Select(m => m.Id).ToHashSet(),
            representative.Id,
            []);
    }

    private MovieRecord Movie(string name, int? year = null, string? tmdb = null, string? imdb = null, long? size = 1000)
    {
        var id = new Guid(string.Create(CultureInfo.InvariantCulture, $"00000000-0000-0000-0001-{++_next:D12}"));
        return new MovieRecord
        {
            Id = id,
            Name = name,
            ProductionYear = year,
            Path = $"/movies/{_next}/{_next}.mkv",
            ContainingFolder = $"/movies/{_next}",
            VideoType = VideoType.VideoFile,
            Size = size,
            DateCreated = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Tmdb = tmdb,
            Imdb = imdb,
            LibraryIds = [LibraryA]
        };
    }
}
