using System;
using System.IO;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using Jellyfin.Plugin.LibraryMaintenance.Move.Planning;
using Jellyfin.Plugin.LibraryMaintenance.Operations;
using Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move;

/// <summary>
/// Which items can move, each check on its own, resolved for the caller (an item in a library the
/// caller can't open is the same as none); a version or part retargets to its owner, an extra doesn't.
/// </summary>
public sealed class MoveEligibilityTests : IDisposable
{
    private readonly PlannerWorld _w = new();
    private readonly PluginConfiguration _configuration = new();
    private readonly LibraryOperationGate _gate;
    private readonly MoveEligibility _eligibility;

    public MoveEligibilityTests()
    {
        _gate = new LibraryOperationGate(_w.Library.Object);
        _eligibility = new MoveEligibility(_w.Access, _w.Catalog, _gate, new MoveFeature(new TestConfiguration(_configuration), () => true), static root => char.ToUpperInvariant(root[0]) == 'Y');
    }

    public void Dispose()
    {
        _gate.Dispose();
        _w.Dispose();
    }

    [Fact]
    public void AnOwnFolderMovie_IsEligible_AsAFolderUnit_InItsLibraryAndLocation()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Alien (1979)", "Alien (1979).mkv"), "Alien", 1979, false);

        var result = _eligibility.Check(movie.Id, null);

        Assert.True(result.Eligible);
        Assert.True(result.Found);
        Assert.Same(movie, result.Item);
        Assert.Equal(MoveUnitKind.Folder, result.UnitKind);
        Assert.Equal(_w.MoviesLibrary.Id, result.Library?.Id);
        Assert.Equal(0, result.LocationIndex);
        Assert.Null(result.OwnerItemId);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void ALooseFile_AnEpisode_AndADiscFolder_GetTheirUnitKinds()
    {
        var loose = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        var series = _w.Series("Lost");
        var episode = _w.Episode(series, _w.Season(series, 1), "Lost S01E01.mkv", 1);
        episode.IsInMixedFolder = false;
        var disc = _w.Movie(Path.Join(_w.Movies, "Ran (1985)", "VIDEO_TS", "VIDEO_TS.IFO"), "Ran", 1985, true);
        disc.VideoType = VideoType.Dvd;

        Assert.Equal(MoveUnitKind.FileSet, _eligibility.Check(loose.Id, null).UnitKind);
        Assert.Equal(MoveUnitKind.FileSet, _eligibility.Check(episode.Id, null).UnitKind);
        Assert.Equal(MoveUnitKind.Folder, _eligibility.Check(disc.Id, null).UnitKind);
        Assert.Equal(_w.TvLibrary.Id, _eligibility.Check(episode.Id, null).Library?.Id);
    }

    [Fact]
    public void AnItemThatIsntThere_OrIsHiddenFromTheCaller_IsNotFound_TheSameWay()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        _w.Library.Hide(_w.Bob, movie);

        var missing = _eligibility.Check(Guid.NewGuid(), _w.Bob);
        var hidden = _eligibility.Check(movie.Id, _w.Bob);

        Assert.False(missing.Found);
        Assert.Equal(MoveEligibility.NotFoundCode, hidden.Code);
        Assert.Equal(missing.Reason, hidden.Reason);
        Assert.Null(hidden.Item);
        Assert.Null(hidden.Library);
        Assert.False(_eligibility.Check(Guid.Empty, null).Found);
        Assert.True(_eligibility.Check(movie.Id, null).Eligible);
    }

    [Fact]
    public void AnItemTheCallerSees_InALibraryTheyCantOpen_IsntInALibraryFolder_AndNamesNone()
    {
        // GetItemById(id, user) already refuses this on the server; the catalog is a second check over the caller's libraries.
        var movie = _w.Movie(Path.Join(_w.Private, "Secret.2020.mkv"), "Secret", 2020, true);

        var result = _eligibility.Check(movie.Id, _w.Bob);

        Assert.Equal(MoveErrorCodes.NotEligible, result.Code);
        Assert.Equal("This item isn't inside a library folder.", result.Reason);
        Assert.Null(result.Library);
        Assert.Equal(_w.PrivateLibrary.Id, _eligibility.Check(movie.Id, null).Library?.Id);
    }

    [Fact]
    public void AVersionOrPart_RetargetsToItsOwner()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Alien (1979)", "Alien (1979) - 1080p.mkv"), "Alien", 1979, false);
        var path = _w.File(Path.Join(_w.Movies, "Alien (1979)", "Alien (1979) - 720p.mkv"));
        var version = _w.Library.Add(new Video { Id = PlannerWorld.IdOf(path, typeof(Video)), Path = path, OwnerId = movie.Id });

        var result = _eligibility.Check(version.Id, null);

        Assert.True(result.Eligible);
        Assert.Equal(version.Id, result.ItemId);
        Assert.Equal(movie.Id, result.OwnerItemId);
        Assert.Same(movie, result.Item);

        // An owner the caller can't see is not found, though the version was.
        _w.Library.Hide(_w.Bob, movie);
        Assert.False(_w.Access.GetItem<BaseItem>(version.Id, _w.Bob) is null);
        Assert.False(_eligibility.Check(version.Id, _w.Bob).Found);

        // A check on the owner that fails still says which item moves.
        _configuration.AllowedItemKinds = ["Episode"];
        var off = _eligibility.Check(version.Id, null);
        Assert.Equal((MoveErrorCodes.NotEligible, movie.Id), (off.Code, off.OwnerItemId));
    }

    [Fact]
    public void AnExtra_IsNotEligible_ItMovesWithItsMovie()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Alien (1979)", "Alien (1979).mkv"), "Alien", 1979, false);
        var trailer = _w.Trailer(movie, Path.Join(_w.Movies, "Alien (1979)", "trailers", "Teaser.mkv"));

        var result = _eligibility.Check(trailer.Id, null);

        Assert.Equal(MoveErrorCodes.NotEligible, result.Code);
        Assert.Equal("This is an extra; extras move with their movie.", result.Reason);
        Assert.Null(result.OwnerItemId);
    }

    [Fact]
    public void AKindMoveDoesntSupport_IsNotEligible()
    {
        var series = _w.Series("Lost");
        var video = _w.Library.Add(new Video { Path = _w.File(Path.Join(_w.Movies, "Clip.mkv")) });

        foreach (var item in new BaseItem[] { series, _w.Season(series, 1), video })
        {
            var result = _eligibility.Check(item.Id, null);
            Assert.Equal(("move.not-eligible", "Move works with movies, episodes and music videos."), (result.Code, result.Reason));
        }
    }

    [Theory]
    [InlineData("Episode", "Moving movies is turned off in Library Maintenance's settings.")]
    [InlineData("Movie,MusicVideo", null)]
    public void AllowedItemKinds_TurnsAKindOff(string kinds, string? reason)
    {
        _configuration.AllowedItemKinds = kinds.Split(',');
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        Assert.Equal(reason, _eligibility.Check(movie.Id, null).Reason);
    }

    [Fact]
    public void AnEpisodeTurnedOff_SaysEpisodes()
    {
        _configuration.AllowedItemKinds = ["Movie"];
        var series = _w.Series("Lost");
        var episode = _w.Episode(series, _w.Season(series, 1), "Lost S01E01.mkv", 1);
        Assert.Equal("Moving episodes is turned off in Library Maintenance's settings.", _eligibility.Check(episode.Id, null).Reason);
    }

    [Fact]
    public void AChannelItem_AVirtualEpisode_APlaceholder_AndAShortcut_AreNotEligible()
    {
        var channel = _w.Movie(Path.Join(_w.Movies, "Channel.mkv"), "Channel", 2001, true);
        channel.ChannelId = Guid.NewGuid();
        var series = _w.Series("Lost");
        var missing = _w.Library.Add(new Episode { Path = Path.Join(series.Path, "Season 1", "Lost S01E09.mkv"), IsVirtualItem = true, SeriesId = series.Id });
        var placeholder = _w.Movie(Path.Join(_w.Movies, "Disc.2001.mkv"), "Disc", 2001, true);
        placeholder.IsPlaceHolder = true;
        var shortcut = _w.Movie(Path.Join(_w.Movies, "Stream.2001.strm"), "Stream", 2001, true);
        shortcut.IsShortcut = true;

        Assert.Equal("This isn't a library item (a channel's or Live TV's).", _eligibility.Check(channel.Id, null).Reason);
        Assert.Equal("This item has no file (a missing episode or a placeholder).", _eligibility.Check(missing.Id, null).Reason);
        Assert.Equal("This item has no file (a missing episode or a placeholder).", _eligibility.Check(placeholder.Id, null).Reason);
        Assert.Equal("This item is a shortcut (.strm); only real files move.", _eligibility.Check(shortcut.Id, null).Reason);
    }

    [Fact]
    public void AFileBeingRecorded_IsNotEligible()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Recording " + Guid.NewGuid().ToString("N") + ".ts"), "News", 2026, true);
        Mock.Get(Video.RecordingsManager).Setup(r => r.GetActiveRecordingInfo(movie.Path)).Returns(new ActiveRecordingInfo());

        Assert.Equal("This item is being recorded.", _eligibility.Check(movie.Id, null).Reason);
    }

    [Theory]
    [InlineData(@"\\nas\video\Movies\Heat.1995.mkv")]
    [InlineData(@"\\?\C:\Video\Movies\Heat.1995.mkv")]
    [InlineData(@"Y:\Movies\Heat.1995.mkv")]
    [InlineData("Movies\\Heat.1995.mkv")]
    [InlineData("https://example.invalid/Heat.1995.mkv")]
    public void AnItemNotOnALocalDrive_IsNotEligible_AndIsntOpened(string path)
    {
        var movie = _w.Library.Add(new Movie { Path = path, IsInMixedFolder = true });
        Assert.Equal("This item isn't on a local drive.", _eligibility.Check(movie.Id, null).Reason);
    }

    [Fact]
    public void AnItemOutsideEveryLibraryFolder_OrInALibraryOfAnotherType_IsNotEligible()
    {
        var outside = _w.Movie(_w.File(Path.Join(_w.Root, "Elsewhere", "Heat.1995.mkv")), "Heat", 1995, true);
        var home = _w.Movie(Path.Join(_w.Home, "Birthday.2019.mkv"), "Birthday", 2019, true);

        Assert.Equal("This item isn't inside a library folder.", _eligibility.Check(outside.Id, null).Reason);
        Assert.Equal("Move works in Movies, Shows and Music Videos libraries.", _eligibility.Check(home.Id, null).Reason);
    }

    [Fact]
    public void AnItemOrVersionPartnerInAnotherOperation_IsItemBusy_NamingTheFeature()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Alien (1979)", "Alien (1979).mkv"), "Alien", 1979, false);
        var partner = _w.Movie(Path.Join(_w.Movies, "Alien (1979) Directors Cut", "Alien (1979) Directors Cut.mkv"), "Alien", 1979, false);
        partner.PrimaryVersionId = movie.Id.ToString("N");

        Assert.True(_gate.TryMarkBusy(OperationOwner.Duplicates, "merge-1", [movie.Id], out _));
        var self = _eligibility.Check(movie.Id, null);
        var linked = _eligibility.Check(partner.Id, null);

        Assert.Equal((MoveErrorCodes.ItemBusy, "This item is being changed on the Duplicates tab."), (self.Code, self.Reason));
        Assert.Equal(MoveErrorCodes.ItemBusy, linked.Code);
        Assert.True(self.Found);

        _gate.Release("merge-1");
        Assert.True(_gate.TryMarkBusy(OperationOwner.Move, "job-1", [partner.Id], out _));
        Assert.Equal("This item is being moved.", _eligibility.Check(partner.Id, null).Reason);
        Assert.True(_eligibility.Check(movie.Id, null).Eligible);
    }
}
