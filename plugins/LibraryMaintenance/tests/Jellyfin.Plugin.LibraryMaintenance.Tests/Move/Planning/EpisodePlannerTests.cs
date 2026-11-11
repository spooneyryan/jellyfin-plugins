using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using Jellyfin.Plugin.LibraryMaintenance.Move.Planning;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Configuration;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Planning;

/// <summary>
/// The episode destinations: an existing series and season, a new season, a new series folder in the TV
/// location, outside any series (blocked), and the <c>episode-other-series</c>, <c>episode-other-season</c>,
/// <c>new-series</c> and "season from the file name" warnings.
/// </summary>
public sealed class EpisodePlannerTests : IDisposable
{
    private readonly PlannerWorld _w = new();
    private readonly Series _lost;
    private readonly Season _lost1;
    private readonly Season _lost2;

    public EpisodePlannerTests()
    {
        _lost = _w.Series("Lost");
        _lost1 = _w.Season(_lost, 1);
        _lost2 = _w.Season(_lost, 2);
    }

    public void Dispose() => _w.Dispose();

    [Fact]
    public void Episode_IsAFileSet_WithItsSidecars_NeverWrapped()
    {
        var episode = _w.Episode(_lost, _lost1, "Lost S01E05.mkv", 1, "Lost S01E05.en.srt", "Lost S01E05-thumb.jpg");
        _w.Episode(_lost, _lost1, "Lost S01E05b.mkv", 1);

        var unit = _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost/Season 2", wrap: true);

        Assert.Equal(MoveUnitKind.FileSet, unit.Kind);
        Assert.Null(unit.WrapFolderName);
        Assert.Equal(_lost2.Path, unit.TargetDirectory);
        Assert.Equal(["Lost S01E05-thumb.jpg", "Lost S01E05.en.srt", "Lost S01E05.mkv"], unit.Renames.Select(r => Path.GetFileName(r.From)).Order(StringComparer.Ordinal));
        Assert.Equal(PlannerWorld.IdOf(Path.Join(_lost2.Path, "Lost S01E05.mkv"), typeof(Episode)), unit.PrimaryItem.PredictedId);
        Assert.Empty(unit.Blockers);
    }

    [Fact]
    public void Episode_IntoAnotherSeasonOfItsSeries_WithTheSeasonInItsName_WarnsTheNameWins()
    {
        var episode = _w.Episode(_lost, _lost1, "Lost S01E05.mkv", 1);
        var unit = _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost/Season 2");
        Assert.Empty(unit.Blockers);
        Assert.Equal([MoveErrorCodes.EpisodeSeasonFromName], unit.Warnings.Select(w => w.Code));
    }

    [Fact]
    public void Episode_WithoutASeasonInItsName_IntoAnotherSeason_WarnsTheFolderWins()
    {
        var episode = _w.Episode(_lost, _lost1, "Ep 5.mkv", 1);
        Assert.Equal([MoveErrorCodes.EpisodeOtherSeason], _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost/Season 2").Warnings.Select(w => w.Code));

        // A new season folder of the same series: the number comes from its name.
        Assert.Equal([MoveErrorCodes.EpisodeOtherSeason], _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost", "Season 3").Warnings.Select(w => w.Code));
        var same = _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost", "Season 01 (extended)");
        Assert.Empty(same.Blockers);
        Assert.DoesNotContain(MoveErrorCodes.EpisodeOtherSeason, same.Warnings.Select(w => w.Code));
    }

    [Fact]
    public void Episode_IntoASeasonFolderWithoutAnItem_ReadsTheSeasonFromTheFolderName()
    {
        var episode = _w.Episode(_lost, _lost1, "Ep 5.mkv", 1);
        _w.Folder("TV", "Lost", "Season 1 Extras");
        var unit = _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost/Season 1 Extras");
        Assert.Empty(unit.Blockers);
        Assert.Empty(unit.Warnings);
    }

    [Fact]
    public void Episode_IntoAnotherSeries_WarnsItsMetadataFollows()
    {
        var other = _w.Series("Fringe");
        var otherSeason = _w.Season(other, 1);
        var episode = _w.Episode(_lost, _lost1, "Lost S01E05.mkv", 1);

        var unit = _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Fringe/Season 1");

        Assert.Empty(unit.Blockers);
        var warning = Assert.Single(unit.Warnings);
        Assert.Equal(MoveErrorCodes.EpisodeOtherSeries, warning.Code);
        Assert.Contains("Lost → Fringe", warning.Message, StringComparison.Ordinal);
        Assert.Equal(otherSeason.Path, unit.TargetDirectory);
    }

    [Theory]
    [InlineData(false, "is off")]
    [InlineData(true, "is on")]
    public void Episode_IntoANewSeriesFolderInTheTvLocation_WarnsAboutGrouping(bool grouping, string says)
    {
        _w.TvLibrary = _w.TvLibrary with { Options = new LibraryOptions { EnableAutomaticSeriesGrouping = grouping } };
        var episode = _w.Episode(_lost, _lost1, "Lost S01E05.mkv", 1);

        var unit = _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, newFolder: "Lost (2004)");

        Assert.Empty(unit.Blockers);
        var warning = Assert.Single(unit.Warnings);
        Assert.Equal(MoveErrorCodes.NewSeries, warning.Code);
        Assert.Contains(says, warning.Message, StringComparison.Ordinal);
        Assert.Equal([Path.Join(_w.Tv, "Lost (2004)")], unit.CreatedDirectories);
    }

    [Fact]
    public void Episode_OutsideAnySeries_IsBlocked()
    {
        var episode = _w.Episode(_lost, _lost1, "Lost S01E05.mkv", 1);
        _w.Folder("TV", "Unsorted");

        // The location itself, an existing folder that isn't a series, and a new folder inside one.
        Assert.Equal([MoveErrorCodes.EpisodeNeedsSeries], _w.Plan(episode, _w.TvLibrary, _w.TvLibrary).Blockers.Select(b => b.Code));
        Assert.Equal([MoveErrorCodes.EpisodeNeedsSeries], _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Unsorted").Blockers.Select(b => b.Code));
        Assert.Equal([MoveErrorCodes.EpisodeNeedsSeries], _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Unsorted", "Lost").Blockers.Select(b => b.Code));
    }

    [Fact]
    public void Episode_ToTheSeriesFolderItself_IsAllowed_AndWarnsWhenItsSeasonWouldChange()
    {
        var named = _w.Episode(_lost, _lost1, "Lost S01E05.mkv", 1);
        var unnamed = _w.Episode(_lost, _lost1, "Ep 6.mkv", 1);

        Assert.Empty(_w.Plan(named, _w.TvLibrary, _w.TvLibrary, "Lost").Warnings);
        Assert.Equal([MoveErrorCodes.EpisodeOtherSeason], _w.Plan(unnamed, _w.TvLibrary, _w.TvLibrary, "Lost").Warnings.Select(w => w.Code));
    }

    [Fact]
    public void Episode_InTheSameSeasonFolder_IsBlockedAsAlreadyThere()
    {
        var episode = _w.Episode(_lost, _lost1, "Lost S01E05.mkv", 1);
        Assert.Contains(MoveErrorCodes.DestinationInvalid, _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost/Season 1").Blockers.Select(b => b.Code));
    }

    [Fact]
    public void Episode_ACollisionInTheSeason_IsBlocked()
    {
        var episode = _w.Episode(_lost, _lost1, "Lost S01E05.mkv", 1);
        _w.File(Path.Join(_lost2.Path, "lost s01e05.MKV"));
        Assert.Contains(MoveErrorCodes.Collision, _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost/Season 2").Blockers.Select(b => b.Code));
    }

    [Fact]
    public void Episode_AcrossVolumes_IsBlockedInV1()
    {
        var episode = _w.Episode(_lost, _lost1, "Lost S01E05.mkv", 1);
        _w.OnVolume(_lost2.Path, "vol-e");
        var unit = _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost/Season 2");
        Assert.Contains(MoveErrorCodes.CrossVolumeLater, unit.Blockers.Select(b => b.Code));
        Assert.Equal(MoveMethod.CopyVerifyDelete, unit.Method);
    }
}
