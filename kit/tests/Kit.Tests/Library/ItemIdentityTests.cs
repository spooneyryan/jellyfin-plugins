using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// <see cref="ItemIdentity"/>: snapshots of movies, episodes, seasons and tracks with container ids and
/// runtime, the removal rule for cascading removals, the strong provider ids per kind, the lookup keys and a journal line
/// applied to a snapshot.
/// </summary>
public sealed class ItemIdentityTests
{
    private readonly FakeLibrary _library = new();

    [Fact]
    public void Capture_Movie_TakesIdKindNameYearIdsRuntimeAndPath()
    {
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\Video\Movies\Heat (1995)\Heat (1995).mkv", ("Tmdb", "949"), ("Imdb", "tt0113277"), ("TmdbCollection", "1"));

        var snapshot = ItemIdentity.Capture(movie, _library.Object);

        Assert.Equal(movie.Id, snapshot.ItemId);
        Assert.Equal("Movie", snapshot.Kind);
        Assert.Equal("Heat", snapshot.Name);
        Assert.Equal(1995, snapshot.Year);
        Assert.Equal(TimeSpan.FromMinutes(170).Ticks, snapshot.RunTimeTicks);
        Assert.Equal(@"Q:\Video\Movies\Heat (1995)\Heat (1995).mkv", snapshot.Path);
        Assert.Equal(3, snapshot.ProviderIds.Count);
        Assert.Equal("949", snapshot.ProviderIds["Tmdb"]);
        Assert.Null(snapshot.SeriesProviderIds);
        Assert.Null(snapshot.SeriesId);
        Assert.Null(snapshot.SeasonId);
        Assert.Null(snapshot.AlbumId);
        Assert.Null(snapshot.Season);

        // A movie has no container to look up.
        Assert.Empty(_library.IdLookups);
    }

    [Fact]
    public void Capture_NoRuntimeAndNoPath_AreNull()
    {
        var movie = _library.AddMovie("Heat", null, TimeSpan.Zero);
        movie.Path = " ";

        var snapshot = ItemIdentity.Capture(movie, _library.Object);

        Assert.Null(snapshot.RunTimeTicks);
        Assert.Null(snapshot.Path);
        Assert.Null(snapshot.Year);
    }

    [Fact]
    public void Capture_Episode_TakesTheSeriesIdsAndNumbers_ThroughTheLibraryPassedIn()
    {
        var series = _library.AddSeries("Test Series", null, ("Tvdb", "900401"), ("Tmdb", "1396"));
        var season = _library.AddSeason(series, 2);
        var episode = _library.AddEpisode(series, season, 5, TimeSpan.FromSeconds(30));
        episode.ProviderIds["Tvdb"] = "7000001";

        var snapshot = ItemIdentity.Capture(episode, _library.Object);

        Assert.Equal("Episode", snapshot.Kind);
        Assert.Equal(series.Id, snapshot.SeriesId);
        Assert.Equal(season.Id, snapshot.SeasonId);
        Assert.Equal("Test Series", snapshot.SeriesName);
        Assert.Equal(2, snapshot.Season);
        Assert.Equal(5, snapshot.Episode);
        Assert.Equal("900401", snapshot.SeriesProviderIds!["Tvdb"]);
        Assert.Equal("1396", snapshot.SeriesProviderIds["Tmdb"]);
        Assert.Equal("7000001", snapshot.ProviderIds["Tvdb"]);
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, snapshot.RunTimeTicks);
        Assert.Equal(new[] { series.Id }, _library.IdLookups);
    }

    [Fact]
    public void Capture_EpisodeWhoseSeriesIsGone_KeepsTheContainerIds()
    {
        // Inside an ItemRemoved handler of a cascading removal the series is gone already.
        var series = _library.AddSeries("Test Series", null, ("Tvdb", "900401"));
        var season = _library.AddSeason(series, 1);
        var episode = _library.AddEpisode(series, season, 1);
        _library.Remove(series, raiseEvent: false);

        var snapshot = ItemIdentity.Capture(episode, _library.Object);

        Assert.Equal(series.Id, snapshot.SeriesId);
        Assert.Equal(season.Id, snapshot.SeasonId);
        Assert.Null(snapshot.SeriesProviderIds);
        Assert.Equal("Test Series", snapshot.SeriesName);
    }

    [Fact]
    public void Capture_EpisodeWithoutSeries_NeverLooksUpAnEmptyId()
    {
        // GetItemById(Guid.Empty) throws in 10.11.5, as it does in FakeLibrary.
        var episode = _library.Add(new MediaBrowser.Controller.Entities.TV.Episode { Name = "Loose", IndexNumber = 1 });

        var snapshot = ItemIdentity.Capture(episode, _library.Object);

        Assert.Null(snapshot.SeriesId);
        Assert.Null(snapshot.SeasonId);
        Assert.Null(snapshot.SeriesProviderIds);
        Assert.Empty(_library.IdLookups);
    }

    [Fact]
    public void Capture_Season_TakesItsNumberAndTheSeries()
    {
        var series = _library.AddSeries("Test Series", null, ("Tvdb", "900401"));
        var season = _library.AddSeason(series, 3);

        var snapshot = ItemIdentity.Capture(season, _library.Object);

        Assert.Equal("Season", snapshot.Kind);
        Assert.Equal(3, snapshot.Season);
        Assert.Null(snapshot.Episode);
        Assert.Equal(series.Id, snapshot.SeriesId);
        Assert.Null(snapshot.SeasonId);
        Assert.Equal("900401", snapshot.SeriesProviderIds!["Tvdb"]);
    }

    [Fact]
    public void Capture_Track_FindsItsAlbum_AlsoThroughADiscFolder()
    {
        var album = _library.Add(new MusicAlbum { Name = "Album" });
        var disc = _library.Add(new Folder { Name = "CD1" }, album);
        var direct = _library.Add(new Audio { Name = "Track 1" }, album);
        var onDisc = _library.Add(new Audio { Name = "Track 2" }, disc);
        var loose = _library.Add(new Audio { Name = "Loose" }, _library.Add(new Folder { Name = "Singles" }));

        Assert.Equal(album.Id, ItemIdentity.Capture(direct, _library.Object).AlbumId);
        Assert.Equal(album.Id, ItemIdentity.Capture(onDisc, _library.Object).AlbumId);
        Assert.Null(ItemIdentity.Capture(loose, _library.Object).AlbumId);
        Assert.Equal("Audio", ItemIdentity.Capture(direct, _library.Object).Kind);
    }

    [Fact]
    public void IsRemovedBy_TheItemItsSeriesItsSeasonOrItsAlbum()
    {
        var snapshot = IdentitySnapshotTests.Episode() with { AlbumId = Guid.NewGuid() };

        Assert.True(ItemIdentity.IsRemovedBy(snapshot, snapshot.ItemId));
        Assert.True(ItemIdentity.IsRemovedBy(snapshot, snapshot.SeriesId!.Value));
        Assert.True(ItemIdentity.IsRemovedBy(snapshot, snapshot.SeasonId!.Value));
        Assert.True(ItemIdentity.IsRemovedBy(snapshot, snapshot.AlbumId!.Value));
        Assert.False(ItemIdentity.IsRemovedBy(snapshot, Guid.NewGuid()));
        Assert.False(ItemIdentity.IsRemovedBy(snapshot, Guid.Empty));
        Assert.False(ItemIdentity.IsRemovedBy(new IdentitySnapshot(), Guid.Empty));
    }

    [Theory]
    [InlineData("Movie", "Tmdb,Imdb,Tvdb")]
    [InlineData("Episode", "Tmdb,Imdb,Tvdb")]
    [InlineData("Series", "Tmdb,Imdb,Tvdb")]
    [InlineData("Video", "Tmdb,Imdb,Tvdb")]
    [InlineData("MusicAlbum", "MusicBrainzAlbum,MusicBrainzReleaseGroup,AudioDbAlbum")]
    [InlineData("Audio", "MusicBrainzTrack,MusicBrainzRecording")]
    [InlineData("MusicArtist", "MusicBrainzArtist,AudioDbArtist")]
    public void StrongProviders_PerKind(string kind, string providers)
        => Assert.Equal(providers.Split(','), ItemIdentity.StrongProviders(kind));

    [Fact]
    public void StrongIds_OnlyTheKindsIdentifyingIds_UnderCanonicalNames()
    {
        var movie = new IdentitySnapshot
        {
            Kind = "Movie",
            ProviderIds = new Dictionary<string, string> { ["tmdb"] = "603", ["TmdbCollection"] = "2344", ["Zap2It"] = "x" }
        };
        var track = new IdentitySnapshot
        {
            Kind = "Audio",
            ProviderIds = new Dictionary<string, string> { ["MusicBrainzAlbum"] = "album", ["MusicBrainzTrack"] = "track", ["MusicBrainzArtist"] = "artist" }
        };

        Assert.Equal(new[] { KeyValuePair.Create("Tmdb", "603") }, ItemIdentity.StrongIds(movie));
        Assert.Equal(new[] { KeyValuePair.Create("MusicBrainzTrack", "track") }, ItemIdentity.StrongIds(track));
        Assert.Empty(ItemIdentity.StrongSeriesIds(movie));
    }

    [Fact]
    public void StrongKeys_OwnIds_AndSeriesIdsWithNumbers()
    {
        var episode = IdentitySnapshotTests.Episode();
        var season = episode with { Kind = "Season", Episode = null, ProviderIds = new Dictionary<string, string>() };
        var movie = new IdentitySnapshot { Kind = "Movie", ProviderIds = new Dictionary<string, string> { ["Imdb"] = "TT0133093", ["Tmdb"] = "603" } };

        Assert.Equal(
            new[] { "Episode|Imdb|tt0000007", "Episode|Tvdb|4000007", "Episode|Tmdb|1396|S03E07", "Episode|Tvdb|81189|S03E07" },
            ItemIdentity.StrongKeys(episode).OrderBy(k => k.Count(c => c == '|')).ThenBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "Season|Tmdb|1396|S03", "Season|Tvdb|81189|S03" }, ItemIdentity.StrongKeys(season).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "Movie|Imdb|tt0133093", "Movie|Tmdb|603" }, ItemIdentity.StrongKeys(movie).Order(StringComparer.Ordinal));

        // Without numbers there is no series key; without a kind no key at all.
        Assert.Equal(2, ItemIdentity.StrongKeys(episode with { Episode = null }).Count);
        Assert.Empty(ItemIdentity.StrongKeys(movie with { Kind = string.Empty }));
        Assert.Equal("Episode|Tvdb|81189|S01E100", ItemIdentity.StrongKeys(episode with { Season = 1, Episode = 100 }).Last());
    }

    [Fact]
    public void StrongKeys_OfARecordAndOfTheItemThatReplacedIt_Meet()
    {
        // A consumer indexes its detached records by these keys and looks up the keys of each added or updated item.
        var series = _library.AddSeries("Renamed Series", null, ("Tvdb", "900401"));
        var season = _library.AddSeason(series, 1);
        var newEpisode = ItemIdentity.Capture(_library.AddEpisode(series, season, 2), _library.Object);
        var oldEpisode = newEpisode with { ItemId = Guid.NewGuid(), SeriesId = Guid.NewGuid(), SeasonId = Guid.NewGuid(), Path = @"C:\old" };

        Assert.NotEmpty(ItemIdentity.StrongKeys(newEpisode).Intersect(ItemIdentity.StrongKeys(oldEpisode)));
    }

    [Fact]
    public void ApplyMove_RewritesTheItemAndItsContainers()
    {
        var episode = IdentitySnapshotTests.Episode();
        var newId = Guid.NewGuid();

        var moved = ItemIdentity.ApplyMove(episode, Line(episode.ItemId, newId, @"Q:\TV\new.mkv"));
        Assert.Equal(newId, moved.ItemId);
        Assert.Equal(@"Q:\TV\new.mkv", moved.Path);
        Assert.Equal(episode.SeriesId, moved.SeriesId);

        var seriesMoved = ItemIdentity.ApplyMove(episode, Line(episode.SeriesId!.Value, newId, @"Q:\TV\Some Show"));
        Assert.Equal(episode.ItemId, seriesMoved.ItemId);
        Assert.Equal(episode.Path, seriesMoved.Path);
        Assert.Equal(newId, seriesMoved.SeriesId);

        var seasonMoved = ItemIdentity.ApplyMove(episode, Line(episode.SeasonId!.Value, newId, null));
        Assert.Equal(newId, seasonMoved.SeasonId);

        var withoutPath = ItemIdentity.ApplyMove(episode, Line(episode.ItemId, newId, null));
        Assert.Equal(episode.Path, withoutPath.Path);

        Assert.Same(episode, ItemIdentity.ApplyMove(episode, Line(Guid.NewGuid(), newId, null)));
    }

    [Fact]
    public void KindOf_UsesTheBaseItemKindName()
    {
        Assert.Equal("Movie", ItemIdentity.KindOf(new MediaBrowser.Controller.Entities.Movies.Movie()));
        Assert.Equal("Series", ItemIdentity.KindOf(new MediaBrowser.Controller.Entities.TV.Series()));
        Assert.Equal("MusicAlbum", ItemIdentity.KindOf(new MusicAlbum()));
    }

    private static MoveJournalLine Line(Guid oldId, Guid newId, string? newPath)
        => new(1, DateTime.UtcNow, oldId, newId, "Episode", null, newPath, null, null, false);
}
