using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;
using static Jellyfin.Plugin.Kit.Tests.Library.MoveJournalTests;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// <see cref="IdentityResolver"/> against a mocked library: id, then journal (Committed lines
/// only, chained), then path, then provider ids; ambiguous and weak matches are not applied; a policy that allows Exact
/// only, and a failing runtime validator, give Ambiguous; episodes and seasons through the series' ids and numbers.
/// </summary>
public sealed class IdentityResolverTests : IDisposable
{
    private static readonly DateTime Monday = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeLibrary _library = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(Monday.AddHours(1)));
    private readonly TempDirectory _directory = new();

    [Fact]
    public void TheSameId_IsExact_AndNothingElseIsAsked()
    {
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), null, ("Tmdb", "949"));
        var snapshot = Capture(movie);

        var result = Resolve(snapshot, RelinkPolicy.Default, Parse(Header(), Line(1, movie.Id, B)));

        AssertRelink(result, movie, MatchStrength.Exact, MatchSource.Id);
        Assert.Null(result.Chain);
        Assert.Empty(result.Candidates);
        Assert.Empty(_library.PathLookups);
        Assert.Empty(_library.Queries);
    }

    [Fact]
    public void TheJournal_ComesBeforeThePathAndTheProviderIds()
    {
        var moved = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\new\Heat.mkv", ("Tmdb", "949"));
        var atOldPath = _library.AddMovie("Other", 2001, TimeSpan.FromMinutes(90), @"Q:\old\Heat.mkv");
        _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\copy\Heat.mkv", ("Tmdb", "949"));
        var snapshot = Gone(Capture(moved)) with { Path = atOldPath.Path };

        var result = Resolve(snapshot, RelinkPolicy.Default, Parse(Header(), Line(1, snapshot.ItemId, moved.Id)));

        AssertRelink(result, moved, MatchStrength.Exact, MatchSource.Journal);
        Assert.Equal(new long[] { 1 }, result.Chain!.Steps.Select(s => s.Seq));
        Assert.Empty(_library.PathLookups);
        Assert.Empty(_library.Queries);
    }

    [Fact]
    public void ThePath_ComesBeforeTheProviderIds()
    {
        var atPath = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\Movies\Heat.mkv");
        _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\copy\Heat.mkv", ("Tmdb", "949"));
        var snapshot = Gone(Capture(atPath)) with { Path = atPath.Path, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "949" } };

        var result = Resolve(snapshot, RelinkPolicy.Default);

        AssertRelink(result, atPath, MatchStrength.Exact, MatchSource.Path);
        Assert.Equal(new[] { atPath.Path }, _library.PathLookups);
        Assert.Empty(_library.Queries);
    }

    [Fact]
    public void OnlyCommittedLines_AreFollowed()
    {
        var elsewhere = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\new\Heat.mkv");
        var snapshot = Gone(Capture(elsewhere)) with { Path = @"Q:\old\Heat.mkv" };

        var result = Resolve(snapshot, RelinkPolicy.Default, Parse(Header(), Line(1, snapshot.ItemId, elsewhere.Id, status: "Planned")));

        Assert.Equal(ResolveOutcome.Unmatched, result.Outcome);
        Assert.Null(result.Chain);
    }

    [Fact]
    public void TheJournal_IsChained_ThroughACorrectionLine()
    {
        // Library Maintenance predicted B; the scan created the movie under another id, and a correction line says so.
        var actual = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\new\Heat.mkv");
        var snapshot = new IdentitySnapshot { ItemId = A, Kind = "Movie", Name = "Heat", Path = @"Q:\old\Heat.mkv" };

        var result = Resolve(snapshot, RelinkPolicy.Default, Parse(Header(), Line(1, A, B), Line(2, B, actual.Id, extra: ",\"Correction\":true")));

        AssertRelink(result, actual, MatchStrength.Exact, MatchSource.Journal);
        Assert.Equal(2, result.Chain!.Steps.Count);
    }

    [Fact]
    public void TheJournal_AMoveBack_LeadsHome()
    {
        var home = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\Movies\Heat.mkv");

        // A record made while the movie was at B follows B→A.
        var atB = new IdentitySnapshot { ItemId = B, Kind = "Movie", Name = "Heat" };
        var result = Resolve(atB, RelinkPolicy.Default, Parse(Header(), Line(1, home.Id, B), Line(2, B, home.Id)));

        AssertRelink(result, home, MatchStrength.Exact, MatchSource.Journal);
        Assert.Equal(2, Assert.Single(result.Chain!.Steps).Seq);
    }

    [Fact]
    public void TheJournal_ANewIdNotScannedYet_IsAPendingRelink()
    {
        var snapshot = new IdentitySnapshot { ItemId = A, Kind = "Movie", Name = "Heat", Path = @"Q:\old\Heat.mkv" };
        var journal = Parse(Header(), Line(1, A, B, extra: ",\"NewPath\":\"Q:\\\\new\\\\Heat.mkv\""));

        var result = Resolve(snapshot, RelinkPolicy.Default, journal);

        Assert.Equal(ResolveOutcome.Relink, result.Outcome);
        Assert.Equal(MatchStrength.Exact, result.Strength);
        Assert.Equal(MatchSource.Journal, result.Source);
        Assert.True(result.IsPending);
        Assert.Equal(B, result.ItemId);
        Assert.Null(result.Item);
        Assert.True(journal.IsPendingRelink(B, _clock.GetUtcNow().UtcDateTime));
        Assert.Equal(new[] { @"Q:\new\Heat.mkv" }, _library.PathLookups);
    }

    [Fact]
    public void TheJournal_ANewIdNotScannedAfterSevenDays_FallsThroughToTheOtherSteps()
    {
        var copy = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\copy\Heat.mkv", ("Tmdb", "949"));
        var snapshot = new IdentitySnapshot { ItemId = A, Kind = "Movie", Name = "Heat", Path = @"Q:\old\Heat.mkv", ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "949" } };
        _clock.Set(new DateTimeOffset(Monday.AddDays(7)));

        var result = Resolve(snapshot, RelinkPolicy.Default, Parse(Header(), Line(1, A, B)));

        AssertRelink(result, copy, MatchStrength.Strong, MatchSource.ProviderIds);
        Assert.False(result.IsPending);
        Assert.Equal(B, result.Chain!.EndId);
    }

    [Fact]
    public void TheJournal_TheLastLinesNewPath_FindsAMispredictedId()
    {
        var actual = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\new\Heat.mkv");
        var snapshot = new IdentitySnapshot { ItemId = A, Kind = "Movie", Name = "Heat" };

        var result = Resolve(snapshot, RelinkPolicy.Default, Parse(Header(), Line(1, A, B, extra: ",\"NewPath\":\"Q:\\\\new\\\\Heat.mkv\"")));

        AssertRelink(result, actual, MatchStrength.Exact, MatchSource.Journal);
    }

    [Fact]
    public void TheJournalReader_IsReadForEachResolve()
    {
        var moved = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        var file = _directory.Combine("moves.jsonl");
        File.WriteAllText(file, Header() + "\n" + Line(1, A, moved.Id) + "\n");
        var resolver = new IdentityResolver(_library.Object, new MoveJournalReader(file, new ListLogger()), _clock);

        var result = resolver.Resolve(new IdentitySnapshot { ItemId = A, Kind = "Movie" }, RelinkPolicy.Default);

        AssertRelink(result, moved, MatchStrength.Exact, MatchSource.Journal);
    }

    [Fact]
    public void AnEmptyId_SkipsTheIdAndJournalSteps()
    {
        // A JE import or an adopted clip record: GetItemById(Guid.Empty) throws in 10.11.5.
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), null, ("Tmdb", "949"));
        var snapshot = new IdentitySnapshot { Kind = "Movie", Name = "Heat", ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "949" } };

        var result = Resolve(snapshot, RelinkPolicy.Default, Parse(Header(), Line(1, A, B)));

        AssertRelink(result, movie, MatchStrength.Strong, MatchSource.ProviderIds);
        Assert.Empty(_library.IdLookups);
    }

    [Fact]
    public void OneStrongCandidate_Relinks_WithTheDefaultPolicy()
    {
        var other = _library.AddMovie("Same Id Remake", 2005, TimeSpan.FromSeconds(60), null, ("Tmdb", "900201"));
        var snapshot = Snapshot("Same Id Remake", 1999, 60, ("Tmdb", "900201"));

        var result = Resolve(snapshot, RelinkPolicy.Default);

        AssertRelink(result, other, MatchStrength.Strong, MatchSource.ProviderIds);
        var candidate = Assert.Single(result.Candidates);
        Assert.True(candidate.PassesValidation);

        // The provider-id query: assigned HasAnyProviderId, the kind, no virtual items, provider ids loaded, no grouping.
        var query = _library.Queries[0];
        Assert.Equal(new[] { BaseItemKind.Movie }, query.IncludeItemTypes);
        Assert.Equal("900201", query.HasAnyProviderId!["Tmdb"]);
        Assert.False(query.IsVirtualItem);
        Assert.False(query.GroupByPresentationUniqueKey);
        Assert.Contains(ItemFields.ProviderIds, query.DtoOptions.Fields);
        Assert.False(query.DtoOptions.EnableUserData);
        Assert.Null(query.User);
    }

    [Fact]
    public void TwoStrongCandidates_AreAmbiguous()
    {
        _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\Movies\Heat.mkv", ("Tmdb", "949"));
        _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\Movies 2\Heat.mkv", ("Imdb", "tt0113277"));
        var snapshot = Snapshot("Heat", 1995, 170 * 60, ("Tmdb", "949"), ("Imdb", "tt0113277"));

        var result = Resolve(snapshot, RelinkPolicy.Default);

        AssertNotApplied(result, ResolveOutcome.Ambiguous, MatchStrength.Strong, AmbiguousReason.SeveralStrongCandidates);
        Assert.Equal(2, result.Candidates.Count(c => c.Strength == MatchStrength.Strong));
    }

    [Fact]
    public void ExactOnly_LeavesASingleStrongCandidateAmbiguous()
    {
        var copy = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), null, ("Tmdb", "949"));

        var result = Resolve(Snapshot("Heat", 1995, 170 * 60, ("Tmdb", "949")), RelinkPolicy.ExactOnly);

        AssertNotApplied(result, ResolveOutcome.Ambiguous, MatchStrength.Strong, AmbiguousReason.StrongRelinkNotAllowed);
        Assert.Equal(copy.Id, Assert.Single(result.Candidates).ItemId);
    }

    [Fact]
    public void AFailingRuntimeValidator_LeavesItAmbiguous_TheOtherCutIsNotRelinked()
    {
        // The sandbox media's edition pair: the 60 s copy is deleted; its bookmark must not move to the 90 s copy.
        var extended = _library.AddMovie("Edition Movie Extended", 2001, TimeSpan.FromSeconds(90), null, ("Tmdb", "900301"));

        var result = Resolve(Snapshot("Edition Movie", 2001, 60, ("Tmdb", "900301")), RelinkPolicy.RuntimeWithin());

        AssertNotApplied(result, ResolveOutcome.Ambiguous, MatchStrength.Strong, AmbiguousReason.FailedValidation);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(extended.Id, candidate.ItemId);
        Assert.False(candidate.PassesValidation);
    }

    [Fact]
    public void ARecordWithoutARuntime_UnderAPolicyThatRequiresOne_IsLeftAmbiguous_AndOtherwiseRelinks()
    {
        // A record made on one file whose runtime isn't known (a Bookmarks 1.0 bookmark whose video was gone
        // before 1.1 looked it up) can't be checked against the one candidate, which may be another cut; a JE import can.
        var copy = _library.AddMovie("Edition Movie Extended", 2001, TimeSpan.FromSeconds(90), null, ("Tmdb", "900301"));
        var withoutRuntime = Snapshot("Edition Movie", 2001, 60, ("Tmdb", "900301")) with { RunTimeTicks = null };

        var strict = Resolve(withoutRuntime, RelinkPolicy.RuntimeWithin(requireStoredRuntime: true));
        var lenient = Resolve(withoutRuntime, RelinkPolicy.RuntimeWithin());

        AssertNotApplied(strict, ResolveOutcome.Ambiguous, MatchStrength.Strong, AmbiguousReason.FailedValidation);
        Assert.False(Assert.Single(strict.Candidates).PassesValidation);
        AssertRelink(lenient, copy, MatchStrength.Strong, MatchSource.ProviderIds);
    }

    [Fact]
    public void APassingRuntimeValidator_Relinks()
    {
        var copy = _library.AddMovie("Same Id Remake", 2005, TimeSpan.FromSeconds(60), null, ("Tmdb", "900201"));

        var result = Resolve(Snapshot("Same Id Remake", 1999, 60, ("Tmdb", "900201")), RelinkPolicy.RuntimeWithin());

        AssertRelink(result, copy, MatchStrength.Strong, MatchSource.ProviderIds);
    }

    [Fact]
    public void AContradictingId_MakesACandidateWeak_AndWeakIsNeverApplied()
    {
        // The same TMDb id but another IMDb id: one of them is wrong, or it is another film.
        var other = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), null, ("Tmdb", "949"), ("Imdb", "tt9999999"));

        var result = Resolve(Snapshot("Heat", 1995, 170 * 60, ("Tmdb", "949"), ("Imdb", "tt0113277")), RelinkPolicy.Default);

        AssertNotApplied(result, ResolveOutcome.Unmatched, MatchStrength.Weak, AmbiguousReason.None);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(other.Id, candidate.ItemId);
        Assert.Equal(MatchStrength.Weak, candidate.Strength);
        Assert.Null(candidate.PassesValidation);
    }

    [Fact]
    public void ACollectionIdSharedByManyFilms_IsNoMatch()
    {
        _library.AddMovie("Another Part", 2002, TimeSpan.FromMinutes(100), null, ("TmdbCollection", "1241"));

        var result = Resolve(Snapshot("Some Part", 2001, 6000, ("TmdbCollection", "1241")), RelinkPolicy.Default);

        AssertNotApplied(result, ResolveOutcome.Unmatched, MatchStrength.None, AmbiguousReason.None);
        Assert.DoesNotContain(_library.Queries, q => q.HasAnyProviderId is not null);
    }

    [Fact]
    public void TheSameNameAndYear_IsOnlyWeak()
    {
        // The duplicate of the sandbox media without provider ids: suggested, never applied.
        var other = _library.AddMovie("Big Buck Test", 2008, TimeSpan.FromSeconds(60), @"C:\media\Movies 2\Big Buck Test (2008).mkv");
        _library.AddMovie("Big Buck Test", 2011, TimeSpan.FromSeconds(60));

        var result = Resolve(Snapshot("Big Buck Test", 2008, 60), RelinkPolicy.Default);

        AssertNotApplied(result, ResolveOutcome.Unmatched, MatchStrength.Weak, AmbiguousReason.None);
        Assert.Equal(MatchSource.Name, result.Source);
        Assert.Equal(other.Id, Assert.Single(result.Candidates).ItemId);
    }

    [Fact]
    public void Names_AreNotSearchedWhenAStrongCandidateExists()
    {
        _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), null, ("Tmdb", "949"));
        _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));

        var result = Resolve(Snapshot("Heat", 1995, 170 * 60, ("Tmdb", "949")), RelinkPolicy.Default);

        Assert.Equal(ResolveOutcome.Relink, result.Outcome);
        Assert.Single(result.Candidates);
        Assert.DoesNotContain(_library.Queries, q => q.Name is not null);
    }

    [Fact]
    public void WeakCandidates_AreCappedAtFive()
    {
        for (var i = 0; i < 8; i++)
        {
            _library.AddMovie("Untitled", 2020, TimeSpan.FromSeconds(60), $@"C:\media\{i}.mkv");
        }

        var result = Resolve(Snapshot("Untitled", 2020, 60), RelinkPolicy.Default);

        Assert.Equal(IdentityResolver.MaxWeakCandidates, result.Candidates.Count);
    }

    [Fact]
    public void AnUnknownKind_SkipsTheProviderAndNameSteps()
    {
        _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), null, ("Tmdb", "949"));

        foreach (var kind in new[] { string.Empty, "Hologram", "5" })
        {
            var result = Resolve(Snapshot("Heat", 1995, 170 * 60, ("Tmdb", "949")) with { Kind = kind }, RelinkPolicy.Default);
            Assert.Equal(ResolveOutcome.Unmatched, result.Outcome);
        }

        Assert.Empty(_library.Queries);
    }

    [Fact]
    public void Episodes_RelinkThroughTheSeriesIdsAndNumbers_AfterTheSeriesFolderWasRenamed()
    {
        // A sandbox media case: the series folder is renamed, so the series, its seasons and episodes all get new ids.
        var (_, _, oldEpisodes) = AddSeries("Test Series", ("Tvdb", "900401"));
        var snapshots = oldEpisodes.Select(Capture).ToList();
        _library.Remove(_library.Items.OfType<Series>().Single(), raiseEvent: false);
        var (_, _, newEpisodes) = AddSeries("Test Series Renamed", ("Tvdb", "900401"));

        for (var i = 0; i < snapshots.Count; i++)
        {
            var result = Resolve(snapshots[i], RelinkPolicy.RuntimeWithin());
            AssertRelink(result, newEpisodes[i], MatchStrength.Strong, MatchSource.SeriesNumbers);
        }

        var query = _library.Queries.First(q => q.IncludeItemTypes.Contains(BaseItemKind.Episode));
        Assert.Single(query.AncestorIds);
        Assert.NotNull(query.ParentIndexNumber);
        Assert.NotNull(query.IndexNumber);
    }

    [Fact]
    public void Episodes_OfADeletedSeasonFolder_StayUnmatched()
    {
        var (series, seasons, episodes) = AddSeries("Test Series", ("Tvdb", "900401"));
        var gone = episodes.Where(e => e.ParentIndexNumber == 2).Select(Capture).ToList();
        _library.Remove(seasons[1], raiseEvent: false);

        Assert.All(gone, s => Assert.Equal(ResolveOutcome.Unmatched, Resolve(s, RelinkPolicy.Default).Outcome));
        Assert.Contains(series, _library.Items);
    }

    [Fact]
    public void Seasons_RelinkThroughTheSeriesIdsAndTheirNumber()
    {
        var (_, oldSeasons, _) = AddSeries("Test Series", ("Tvdb", "900401"));
        var snapshot = Capture(oldSeasons[1]);
        _library.Remove(_library.Items.OfType<Series>().Single(), raiseEvent: false);
        var (_, newSeasons, _) = AddSeries("Test Series Renamed", ("Tvdb", "900401"));

        var result = Resolve(snapshot, RelinkPolicy.Default);

        AssertRelink(result, newSeasons[1], MatchStrength.Strong, MatchSource.SeriesNumbers);
    }

    [Fact]
    public void Episodes_UnderTwoSeriesWithTheSameIds_AreAmbiguous()
    {
        var (_, _, episodes) = AddSeries("Test Series", ("Tvdb", "900401"));
        AddSeries("Test Series (copy)", ("Tvdb", "900401"));
        var snapshot = Gone(Capture(episodes[0]));

        var result = Resolve(snapshot, RelinkPolicy.Default);

        AssertNotApplied(result, ResolveOutcome.Ambiguous, MatchStrength.Strong, AmbiguousReason.SeveralStrongCandidates);
    }

    [Fact]
    public void Episodes_AnEpisodeWithAContradictingOwnId_IsOnlyWeak()
    {
        var (_, _, oldEpisodes) = AddSeries("Test Series", ("Tvdb", "900401"));
        oldEpisodes[0].ProviderIds["Tvdb"] = "7000001";
        var snapshot = Capture(oldEpisodes[0]);
        _library.Remove(_library.Items.OfType<Series>().Single(), raiseEvent: false);
        var (_, _, newEpisodes) = AddSeries("Test Series Renamed", ("Tvdb", "900401"));
        newEpisodes[0].ProviderIds["Tvdb"] = "7000099";

        var result = Resolve(snapshot, RelinkPolicy.Default);

        AssertNotApplied(result, ResolveOutcome.Unmatched, MatchStrength.Weak, AmbiguousReason.None);
        Assert.Equal(newEpisodes[0].Id, Assert.Single(result.Candidates).ItemId);
    }

    [Fact]
    public void Episodes_TheirOwnIds_AlsoFindThem()
    {
        var (_, _, episodes) = AddSeries("Test Series");
        episodes[3].ProviderIds["Tvdb"] = "7000004";
        var snapshot = Gone(Capture(episodes[3])) with { Season = null, Episode = null };

        var result = Resolve(snapshot, RelinkPolicy.Default);

        AssertRelink(result, episodes[3], MatchStrength.Strong, MatchSource.ProviderIds);
    }

    [Fact]
    public void VirtualEpisodes_AreNeverCandidates()
    {
        var series = _library.AddSeries("Test Series", null, ("Tvdb", "900401"));
        var season = _library.AddSeason(series, 1);
        _library.AddEpisode(series, season, 1, isVirtual: true);
        var snapshot = new IdentitySnapshot
        {
            ItemId = A,
            Kind = "Episode",
            Season = 1,
            Episode = 1,
            SeriesName = "Test Series",
            SeriesProviderIds = new Dictionary<string, string> { ["Tvdb"] = "900401" }
        };

        var result = Resolve(snapshot, RelinkPolicy.Default);

        Assert.Equal(ResolveOutcome.Unmatched, result.Outcome);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Episodes_OnlyTheSeriesName_IsWeak()
    {
        var (_, _, episodes) = AddSeries("Test Series");
        var snapshot = new IdentitySnapshot { ItemId = A, Kind = "Episode", SeriesName = "Test Series", Season = 1, Episode = 2 };

        var result = Resolve(snapshot, RelinkPolicy.Default);

        AssertNotApplied(result, ResolveOutcome.Unmatched, MatchStrength.Weak, AmbiguousReason.None);
        Assert.Equal(episodes[1].Id, Assert.Single(result.Candidates).ItemId);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData(" \r\n")]
    public void ABlankPath_IsNeverLookedUp(string path)
    {
        // The review's sandbox case: on 10.11.5 FindByPath(" ") has no path filter and answers with the newest item of the
        // whole library (FakeLibrary does the same), which must never become an Exact relink. The newest item is a movie,
        // so the kind check alone wouldn't stop it.
        AddSeries("Test Series");
        var newest = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        var snapshot = new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie", Name = "Nothing like this", Path = path };
        Assert.Same(newest, _library.Object.FindByPath(path, null));

        var result = Resolve(snapshot, RelinkPolicy.RuntimeWithin());

        AssertNotApplied(result, ResolveOutcome.Unmatched, MatchStrength.None, AmbiguousReason.None);
        Assert.Empty(_library.PathLookups);
    }

    [Fact]
    public void ABlankNewPath_IsNeverLookedUp()
    {
        _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        var snapshot = new IdentitySnapshot { ItemId = A, Kind = "Movie", Name = "Heat" };
        var journal = Parse(Header(), Line(1, A, B, extra: ",\"Kind\":\"Movie\",\"NewPath\":\" \\t\""));

        var result = Resolve(snapshot, RelinkPolicy.Default, journal);

        // Nothing at B yet and nothing looked up by path: a pending relink to B, as without a NewPath.
        Assert.True(result.IsPending);
        Assert.Equal(B, result.ItemId);
        Assert.Empty(_library.PathLookups);
    }

    [Fact]
    public void AnItemOfAnotherKindAtThePath_IsIgnored()
    {
        // A season folder, and a movie of another kind, at the snapshot's path: neither is the record's movie.
        var (_, seasons, _) = AddSeries("Test Series");
        var video = _library.Add(new Video { Name = "Heat", Path = @"Q:\Movies\Heat.mkv" });
        var atSeason = new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie", Name = "Nothing like this", Path = seasons[1].Path };
        var atVideo = atSeason with { Name = "Heat", Year = 1995, Path = video.Path };

        AssertNotApplied(Resolve(atSeason, RelinkPolicy.Default), ResolveOutcome.Unmatched, MatchStrength.None, AmbiguousReason.None);
        Assert.Equal(new[] { seasons[1].Path }, _library.PathLookups);
        AssertNotApplied(Resolve(atVideo, RelinkPolicy.Default), ResolveOutcome.Unmatched, MatchStrength.None, AmbiguousReason.None);

        // The same path with the item's own kind is Exact; without a kind the path finds nothing.
        AssertRelink(Resolve(atVideo with { Kind = "Video" }, RelinkPolicy.Default), video, MatchStrength.Exact, MatchSource.Path);
        AssertNotApplied(Resolve(atVideo with { Kind = string.Empty }, RelinkPolicy.Default), ResolveOutcome.Unmatched, MatchStrength.None, AmbiguousReason.None);
    }

    [Fact]
    public void TheJournalsNewPath_CountsOnlyAnItemOfTheLinesKind()
    {
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\new\Heat.mkv");
        var snapshot = new IdentitySnapshot { ItemId = A, Kind = "Movie", Name = "Heat" };
        var newPath = ",\"NewPath\":\"Q:\\\\new\\\\Heat.mkv\"";

        // The line says an episode moved there: the movie at that path isn't it, so the relink stays pending on B.
        var episodeLine = Resolve(snapshot, RelinkPolicy.Default, Parse(Header(), Line(1, A, B, extra: ",\"Kind\":\"Episode\"" + newPath)));
        Assert.True(episodeLine.IsPending);
        Assert.Equal(B, episodeLine.ItemId);

        // The line's kind wins over the snapshot's, and without one the snapshot's counts.
        AssertRelink(Resolve(snapshot with { Kind = "Episode" }, RelinkPolicy.Default, Parse(Header(), Line(1, A, B, extra: ",\"Kind\":\"Movie\"" + newPath))), movie, MatchStrength.Exact, MatchSource.Journal);
        AssertRelink(Resolve(snapshot, RelinkPolicy.Default, Parse(Header(), Line(1, A, B, extra: newPath))), movie, MatchStrength.Exact, MatchSource.Journal);
    }

    [Fact]
    public void TheJournal_ALineCommittedBeforeTheRecordWasAttached_IsNotFollowed()
    {
        // Library Maintenance moved A to B; later a new file at A's old path got the id A again, and the record was written
        // on that item. When the new A goes, the record must not follow the old A→B line to a file it never belonged to.
        var old = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\new\Heat.mkv", ("Tmdb", "949"));
        var snapshot = new IdentitySnapshot { ItemId = A, Kind = "Movie", Name = "Heat Director's Cut", Path = @"Q:\old\Heat.mkv" };
        var journal = Parse(Header(), Line(1, A, old.Id, utc: Monday));

        AssertRelink(Resolve(snapshot, RelinkPolicy.Default, journal), old, MatchStrength.Exact, MatchSource.Journal);

        var attachedLater = Resolve(snapshot, RelinkPolicy.Default, journal, attachedUtc: Monday.AddMinutes(30));
        AssertNotApplied(attachedLater, ResolveOutcome.Unmatched, MatchStrength.None, AmbiguousReason.None);
        Assert.Null(attachedLater.Chain);

        // A move after the record was attached is followed as before.
        var moved = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\newer\Heat.mkv");
        var later = Parse(Header(), Line(1, A, old.Id, utc: Monday), Line(2, A, moved.Id, utc: Monday.AddMinutes(45)));
        var result = Resolve(snapshot, RelinkPolicy.Default, later, attachedUtc: Monday.AddMinutes(30));
        AssertRelink(result, moved, MatchStrength.Exact, MatchSource.Journal);
        Assert.Equal(2, Assert.Single(result.Chain!.Steps).Seq);
    }

    [Fact]
    public void Visibility_AStrongCandidateTheOwnerCantSee_IsNeitherCountedNorListed()
    {
        // The same film in Movies and in Movies 2, which the owner can't open.
        var visible = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\Movies\Heat.mkv", ("Tmdb", "949"));
        var hidden = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\Movies 2\Heat.mkv", ("Tmdb", "949"));
        var owner = CreateUser();
        _library.Hide(owner, hidden);
        var snapshot = Snapshot("Heat", 1995, 170 * 60, ("Tmdb", "949"));

        AssertNotApplied(Resolve(snapshot, RelinkPolicy.Default), ResolveOutcome.Ambiguous, MatchStrength.Strong, AmbiguousReason.SeveralStrongCandidates);

        var result = Resolve(snapshot, RelinkPolicy.Default, isVisible: IdentityResolver.VisibleTo(_library.Object, owner));
        AssertRelink(result, visible, MatchStrength.Strong, MatchSource.ProviderIds);
        Assert.Equal(visible.Id, Assert.Single(result.Candidates).ItemId);
        Assert.Contains(hidden.Id, _library.UserLookups);

        // Only the hidden copy left: nothing to relink to, nothing listed, not even as a name suggestion.
        _library.Remove(visible, raiseEvent: false);
        var alone = Resolve(snapshot, RelinkPolicy.Default, isVisible: IdentityResolver.VisibleTo(_library.Object, owner));
        AssertNotApplied(alone, ResolveOutcome.Unmatched, MatchStrength.None, AmbiguousReason.None);
        Assert.Empty(alone.Candidates);
    }

    [Fact]
    public void Visibility_ExactTargetsTheOwnerCantSee_AreNotApplied()
    {
        var owner = CreateUser();
        var isVisible = IdentityResolver.VisibleTo(_library.Object, owner);
        var hidden = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\Movies 2\Heat.mkv");
        _library.Hide(owner, hidden);

        // The same id, the journal's end, the journal's NewPath and the snapshot's path: each is the hidden item.
        var byId = Resolve(Capture(hidden), RelinkPolicy.Default, isVisible: isVisible);
        var byJournal = Resolve(new IdentitySnapshot { ItemId = A, Kind = "Movie", Name = "Heat" }, RelinkPolicy.Default, Parse(Header(), Line(1, A, hidden.Id)), isVisible: isVisible);
        var byNewPath = Resolve(
            new IdentitySnapshot { ItemId = A, Kind = "Movie", Name = "Heat" },
            RelinkPolicy.Default,
            Parse(Header(), Line(1, A, B, extra: ",\"NewPath\":\"Q:\\\\Movies 2\\\\Heat.mkv\"")),
            isVisible: isVisible);
        var byPath = Resolve(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie", Name = "Heat", Path = hidden.Path }, RelinkPolicy.Default, isVisible: isVisible);

        // Not a pending relink either: the library has an item there, only not one the owner can see.
        Assert.All(new[] { byId, byJournal, byNewPath, byPath }, r => AssertNotApplied(r, ResolveOutcome.Unmatched, MatchStrength.None, AmbiguousReason.None));
        Assert.All(new[] { byId, byJournal, byNewPath, byPath }, r => Assert.Empty(r.Candidates));

        // Without the check (a server-wide record) each one is Exact.
        AssertRelink(Resolve(Capture(hidden), RelinkPolicy.Default), hidden, MatchStrength.Exact, MatchSource.Id);
        AssertRelink(Resolve(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie", Path = hidden.Path }, RelinkPolicy.Default), hidden, MatchStrength.Exact, MatchSource.Path);
    }

    [Fact]
    public void Visibility_APendingRelink_HasNoItemToCheck()
    {
        var owner = CreateUser();

        var result = Resolve(new IdentitySnapshot { ItemId = A, Kind = "Movie" }, RelinkPolicy.Default, Parse(Header(), Line(1, A, B)), isVisible: IdentityResolver.VisibleTo(_library.Object, owner));

        Assert.True(result.IsPending);
        Assert.Equal(B, result.ItemId);
        Assert.Empty(_library.UserLookups);
    }

    [Fact]
    public void VisibleTo_AsksTheLibraryAsTheOwner()
    {
        var owner = CreateUser();
        var other = CreateUser("other");
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        _library.Hide(owner, movie);

        Assert.False(IdentityResolver.VisibleTo(_library.Object, owner)(movie));
        Assert.True(IdentityResolver.VisibleTo(_library.Object, other)(movie));
        _library.Mock.Verify(l => l.GetItemById<BaseItem>(movie.Id, owner), Times.Once);
        Assert.Throws<ArgumentNullException>(() => IdentityResolver.VisibleTo(null!, owner));
        Assert.Throws<ArgumentNullException>(() => IdentityResolver.VisibleTo(_library.Object, null!));
    }

    [Fact]
    public void CacheGhost_TheSameIdUnderADeletedArtistFolder_IsNotExact_AndTheRenamedAlbumIsStrong()
    {
        // On 10.11.5 a deleted music artist folder's albums stay in the item cache (GetItemById finds them, with their
        // parent link), while no query lists them.
        var (root, artist, album) = AddAlbum("Artist A", "Album A", ("MusicBrainzAlbum", "f1e2d3c4-0000-4000-8000-00000000000a"));
        var snapshot = Capture(album);
        _library.Remove(artist, raiseEvent: false, cacheKeepsDescendants: true);
        Assert.Same(album, _library.Object.GetItemById(album.Id));

        AssertNotApplied(Resolve(snapshot, RelinkPolicy.Default), ResolveOutcome.Unmatched, MatchStrength.None, AmbiguousReason.None);
        Assert.Contains(artist.Id, _library.IdLookups);

        // The artist folder renamed: the new album (another id, the same MusicBrainz id) is the one Strong candidate.
        var renamed = _library.Add(new MusicArtist { Name = "Artist A", Path = @"Q:\Music\Artist A (renamed)" }, root);
        var album2 = _library.Add(new MusicAlbum { Name = "Album A", Path = @"Q:\Music\Artist A (renamed)\Album A" }, renamed);
        album2.ProviderIds["MusicBrainzAlbum"] = "f1e2d3c4-0000-4000-8000-00000000000a";
        AssertRelink(Resolve(snapshot, RelinkPolicy.Default), album2, MatchStrength.Strong, MatchSource.ProviderIds);

        // Its owner's view says the same: the ghost is not an item the owner sees.
        Assert.False(IdentityResolver.VisibleTo(_library.Object, CreateUser())(album));
        Assert.True(IdentityResolver.VisibleTo(_library.Object, CreateUser())(album2));
    }

    [Fact]
    public void CacheGhost_AtTheJournalsEnd_IsNotExact()
    {
        var (_, artist, album) = AddAlbum("Artist A", "Album A");
        _library.Remove(artist, raiseEvent: false, cacheKeepsDescendants: true);
        var snapshot = new IdentitySnapshot { ItemId = A, Kind = "MusicAlbum", Name = "Album A" };

        // Within the pending window the end id is a pending relink (as for an id no scan has picked up), never an Exact
        // match on the ghost; after it, nothing.
        var result = Resolve(snapshot, RelinkPolicy.Default, Parse(Header(), Line(1, A, album.Id, utc: Monday)));
        Assert.True(result.IsPending);
        Assert.Null(result.Item);
        _clock.Advance(MoveJournal.PendingWindow);
        AssertNotApplied(Resolve(snapshot, RelinkPolicy.Default, Parse(Header(), Line(1, A, album.Id, utc: Monday))), ResolveOutcome.Unmatched, MatchStrength.None, AmbiguousReason.None);
    }

    [Fact]
    public void LibraryPresence_WalksTheParents_StopsAtALibrary_AndCachesItsAnswers()
    {
        var (root, artist, album) = AddAlbum("Artist A", "Album A");
        var track = _library.Add(new Audio { Name = "Track 1", Path = @"Q:\Music\Artist A\Album A\01.flac" }, album);
        var known = new Dictionary<Guid, bool>();

        Assert.True(LibraryPresence.IsInLibrary(_library.Object, track.Id, known));
        Assert.Equal(new[] { track.Id, album.Id, artist.Id, root.Id }, known.Keys);
        Assert.Same(album, LibraryPresence.Find(_library.Object, album.Id));
        Assert.Null(LibraryPresence.Find(_library.Object, Guid.Empty));
        Assert.False(LibraryPresence.IsInLibrary(_library.Object, Guid.Empty));

        _library.Remove(artist, raiseEvent: false, cacheKeepsDescendants: true);
        Assert.True(LibraryPresence.IsInLibrary(_library.Object, track.Id, known));
        Assert.False(LibraryPresence.IsInLibrary(_library.Object, track.Id));
        Assert.Null(LibraryPresence.Find(_library.Object, album.Id));
        Assert.False(LibraryPresence.IsInLibrary(_library.Object, album));

        // A library folder ends the walk (its parent, the root folder, isn't asked).
        var library = _library.Add(new CollectionFolder { Name = "Movies", Path = @"Q:\Movies", ParentId = Guid.NewGuid() });
        var movie = _library.Add(new Movie { Name = "Heat", Path = @"Q:\Movies\Heat.mkv" }, library);
        Assert.True(LibraryPresence.IsInLibrary(_library.Object, movie));
    }

    [Fact]
    public void RefuseConflictingExact_AnotherFilmAtTheSameIdOrPath_IsAmbiguous_WithTheItemListed()
    {
        // Another film put at a removed film's path gets the same id back; a rating names a title.
        var policy = RelinkPolicy.Default with { RefuseConflictingExact = true };
        var other = _library.AddMovie("Other", 2001, TimeSpan.FromMinutes(90), @"Q:\Movies\Heat.mkv", ("Tmdb", "111"));
        var byId = new IdentitySnapshot { ItemId = other.Id, Kind = "Movie", Name = "Heat", Year = 1995, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "949" }, Path = other.Path };
        var byPath = byId with { ItemId = Guid.NewGuid() };

        foreach (var (snapshot, source) in new[] { (byId, MatchSource.Id), (byPath, MatchSource.Path) })
        {
            var result = Resolve(snapshot, policy);
            AssertNotApplied(result, ResolveOutcome.Ambiguous, MatchStrength.Weak, AmbiguousReason.ConflictingProviderIds);
            Assert.Equal(source, result.Source);
            var listed = Assert.Single(result.Candidates);
            Assert.Equal((other.Id, MatchStrength.Weak), (listed.ItemId, listed.Strength));

            // Without the flag (Bookmarks, the policies of 1.1): Exact, as before.
            AssertRelink(Resolve(snapshot, RelinkPolicy.Default), other, MatchStrength.Exact, source);
        }

        // The journal's end too.
        var byJournal = byId with { ItemId = A };
        AssertNotApplied(Resolve(byJournal, policy, Parse(Header(), Line(1, A, other.Id))), ResolveOutcome.Ambiguous, MatchStrength.Weak, AmbiguousReason.ConflictingProviderIds);
        AssertRelink(Resolve(byJournal, RelinkPolicy.Default, Parse(Header(), Line(1, A, other.Id))), other, MatchStrength.Exact, MatchSource.Journal);
    }

    [Fact]
    public void RefuseConflictingExact_TheFilmItselfElsewhere_StillRelinksStrong()
    {
        var policy = RelinkPolicy.Default with { RefuseConflictingExact = true };
        var other = _library.AddMovie("Other", 2001, TimeSpan.FromMinutes(90), @"Q:\Movies\Heat.mkv", ("Tmdb", "111"));
        var moved = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\Movies 2\Heat.mkv", ("Tmdb", "949"));
        var snapshot = new IdentitySnapshot { ItemId = other.Id, Kind = "Movie", Name = "Heat", Year = 1995, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "949" }, Path = other.Path };

        AssertRelink(Resolve(snapshot, policy), moved, MatchStrength.Strong, MatchSource.ProviderIds);
    }

    [Fact]
    public void RefuseConflictingExact_AnItemWithoutStrongIds_OrWithOneThatAgrees_IsExact()
    {
        var policy = RelinkPolicy.Default with { RefuseConflictingExact = true };
        var bare = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\Movies\Heat.mkv");
        var ids = new Dictionary<string, string> { ["Tmdb"] = "949", ["Imdb"] = "tt0113277" };
        AssertRelink(Resolve(new IdentitySnapshot { ItemId = bare.Id, Kind = "Movie", ProviderIds = ids }, policy), bare, MatchStrength.Exact, MatchSource.Id);

        // One agrees and one doesn't (a wrong scrape on one provider): still the film.
        var half = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\Movies\Heat 2.mkv", ("Tmdb", "949"), ("Imdb", "tt9999999"));
        AssertRelink(Resolve(new IdentitySnapshot { ItemId = half.Id, Kind = "Movie", ProviderIds = ids }, policy), half, MatchStrength.Exact, MatchSource.Id);

        // A snapshot without strong ids contradicts nothing.
        var other = _library.AddMovie("Other", 2001, TimeSpan.FromMinutes(90), @"Q:\Movies\Other.mkv", ("Tmdb", "111"));
        AssertRelink(Resolve(new IdentitySnapshot { ItemId = other.Id, Kind = "Movie" }, policy), other, MatchStrength.Exact, MatchSource.Id);
        Assert.False(IdentityResolver.ConflictsWith(new IdentitySnapshot { ItemId = other.Id, Kind = "Movie", ProviderIds = ids }, half));
        Assert.True(IdentityResolver.ConflictsWith(new IdentitySnapshot { ItemId = other.Id, Kind = "Movie", ProviderIds = ids }, other));
        Assert.False(RelinkPolicy.Default.RefuseConflictingExact);
        Assert.False(RelinkPolicy.RuntimeWithin().RefuseConflictingExact);
    }

    [Fact]
    public void Arguments_AreChecked()
    {
        var resolver = CreateResolver();

        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!, RelinkPolicy.Default, MoveJournal.Missing));
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(new IdentitySnapshot(), null!, MoveJournal.Missing));
        Assert.Throws<ArgumentNullException>(() => new IdentityResolver(null!, new MoveJournalReader(_directory.Combine("m.jsonl"), new ListLogger())));
        Assert.Throws<ArgumentNullException>(() => new IdentityResolver(Mock.Of<MediaBrowser.Controller.Library.ILibraryManager>(), null!));

        // Without a journal the current one is read (none here).
        Assert.Equal(ResolveOutcome.Unmatched, resolver.Resolve(new IdentitySnapshot { ItemId = A, Kind = "Movie" }, RelinkPolicy.Default).Outcome);
    }

    public void Dispose() => _directory.Dispose();

    private static IdentitySnapshot Snapshot(string name, int year, long seconds, params (string Provider, string Value)[] ids) => new()
    {
        ItemId = Guid.NewGuid(),
        Kind = "Movie",
        Name = name,
        Year = year,
        RunTimeTicks = TimeSpan.FromSeconds(seconds).Ticks,
        ProviderIds = ids.ToDictionary(i => i.Provider, i => i.Value),
        Path = $@"C:\gone\{name}.mkv"
    };

    // The snapshot of an item that is gone: another id and path, so only the provider ids and names can find anything.
    private static IdentitySnapshot Gone(IdentitySnapshot snapshot) => snapshot with { ItemId = Guid.NewGuid(), Path = @"C:\gone\" + snapshot.Name };

    private static void AssertRelink(ResolveResult result, BaseItem item, MatchStrength strength, MatchSource source)
    {
        Assert.Equal(ResolveOutcome.Relink, result.Outcome);
        Assert.True(result.ShouldRelink);
        Assert.Equal(strength, result.Strength);
        Assert.Equal(source, result.Source);
        Assert.Equal(item.Id, result.ItemId);
        Assert.Same(item, result.Item);
        Assert.False(result.IsPending);
        Assert.Equal(AmbiguousReason.None, result.Reason);
    }

    private static void AssertNotApplied(ResolveResult result, ResolveOutcome outcome, MatchStrength strength, AmbiguousReason reason)
    {
        Assert.Equal(outcome, result.Outcome);
        Assert.False(result.ShouldRelink);
        Assert.Equal(strength, result.Strength);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.ItemId);
        Assert.Null(result.Item);
    }

    private (Series Series, List<Season> Seasons, List<Episode> Episodes) AddSeries(string name, params (string Provider, string Value)[] ids)
    {
        var series = _library.AddSeries(name, null, ids);
        var seasons = new List<Season> { _library.AddSeason(series, 1), _library.AddSeason(series, 2) };
        var episodes = seasons.SelectMany(s => new[] { _library.AddEpisode(series, s, 1), _library.AddEpisode(series, s, 2) }).ToList();
        return (series, seasons, episodes);
    }

    private static User CreateUser(string name = "owner") => new(name, "DefaultAuthenticationProvider", "DefaultPasswordResetProvider");

    // A Music library folder with an artist folder and one album in it.
    private (Folder Root, MusicArtist Artist, MusicAlbum Album) AddAlbum(string artistName, string albumName, params (string Provider, string Value)[] ids)
    {
        var root = _library.Add(new Folder { Name = "Music", Path = @"Q:\Music" });
        var artist = _library.Add(new MusicArtist { Name = artistName, Path = $@"Q:\Music\{artistName}" }, root);
        var album = _library.Add(new MusicAlbum { Name = albumName, Path = $@"Q:\Music\{artistName}\{albumName}" }, artist);
        foreach (var (provider, value) in ids)
        {
            album.ProviderIds[provider] = value;
        }

        return (root, artist, album);
    }

    private IdentitySnapshot Capture(BaseItem item) => ItemIdentity.Capture(item, _library.Object);

    private IdentityResolver CreateResolver()
        => new(_library.Object, new MoveJournalReader(_directory.Combine("none.jsonl"), new ListLogger()), _clock);

    private ResolveResult Resolve(
        IdentitySnapshot snapshot,
        RelinkPolicy policy,
        MoveJournal? journal = null,
        DateTime? attachedUtc = null,
        Func<BaseItem, bool>? isVisible = null)
    {
        _library.IdLookups.Clear();
        _library.PathLookups.Clear();
        _library.UserLookups.Clear();
        _library.Queries.Clear();
        return CreateResolver().Resolve(snapshot, policy, journal ?? MoveJournal.Missing, attachedUtc, isVisible);
    }
}
