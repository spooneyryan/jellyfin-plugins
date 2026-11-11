using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Paths;
using Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Planning;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move;

/// <summary>
/// <c>GET /Move/Destinations</c> and <c>GET /Move/Folders</c>: the libraries of the item's type the
/// caller can open, the source's first; each folder's drive, free space and whether it can be chosen (not on another
/// volume in this version; a network or device path listed but never opened); a folder's subfolders with their badges,
/// filtered, sorted and paged.
/// </summary>
public sealed class MoveDestinationsTests : IDisposable
{
    private readonly PlannerWorld _w = new();
    private readonly PlanServices _s;

    public MoveDestinationsTests() => _s = new PlanServices(_w);

    public void Dispose()
    {
        _s.Dispose();
        _w.Dispose();
    }

    [Fact]
    public void Destinations_AreTheLibrariesOfTheItemsType_TheCallerOpens_TheSourceFirst()
    {
        var movie = _w.Movie(Path.Join(_w.Cult, "Heat.1995.mkv"), "Heat", 1995, true);

        var forKey = _s.Plans.GetDestinations(movie.Id, null);
        var forBob = _s.Plans.GetDestinations(movie.Id, _w.Bob);

        Assert.Equal([_w.CultLibrary.Id, _w.MoviesLibrary.Id, _w.PrivateLibrary.Id], forKey.Select(l => l.LibraryId));
        Assert.Equal([_w.CultLibrary.Id, _w.MoviesLibrary.Id], forBob.Select(l => l.LibraryId));
        Assert.True(forKey[0].IsSource);
        Assert.False(forKey[1].IsSource);
        Assert.Equal("movies", forKey[1].CollectionType);
        var location = Assert.Single(forKey[1].Locations);
        Assert.Equal((0, _w.Movies, true, true, true, null), (location.Index, location.Path, location.Reachable, location.SameVolumeAsSource, location.Selectable, location.Reason));
        Assert.Equal(1L << 40, location.FreeBytes);

        _s.Configuration.AllowCrossLibraryMoves = false;
        Assert.Equal([_w.CultLibrary.Id], _s.Plans.GetDestinations(movie.Id, null).Select(l => l.LibraryId));
    }

    [Fact]
    public void ALocationOnAnotherVolume_IsListed_ButNotSelectable()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        _w.OnVolume(_w.Cult, "vol-d");

        var cult = _s.Plans.GetDestinations(movie.Id, null).Single(l => l.LibraryId == _w.CultLibrary.Id).Locations.Single();

        Assert.Equal((true, false, false, "cross-volume-later"), (cult.Reachable, cult.SameVolumeAsSource, cult.Selectable, cult.Reason));
    }

    [Fact]
    public void NetworkDeviceMissingAndIgnoredLocations_AreListed_NotSelectable_AndTheFirstTwoNeverAsked()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        var ignored = _w.Folder("Ignored");
        File.WriteAllText(Path.Join(ignored, ".ignore"), string.Empty);
        var locations = new[] { @"\\nas\video\Cult", @"\\?\C:\Cult", Path.Join(_w.Root, "Missing"), ignored };
        _w.Library.Mock.Setup(l => l.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { Name = "Movies", ItemId = _w.MoviesLibrary.Id.ToString("N"), CollectionType = CollectionTypeOptions.movies, Locations = [_w.Movies] },
            new VirtualFolderInfo { Name = "Cult", ItemId = _w.CultLibrary.Id.ToString("N"), CollectionType = CollectionTypeOptions.movies, Locations = locations },
        ]);

        var listed = _s.Plans.GetDestinations(movie.Id, null).Single(l => l.LibraryId == _w.CultLibrary.Id).Locations;

        Assert.Equal(["network-location", "device-path", "unreachable", "ignored"], listed.Select(l => l.Reason));
        Assert.All(listed, l => Assert.False(l.Selectable));
        Assert.Equal([false, false, false, true], listed.Select(l => l.Reachable));
        Assert.DoesNotContain(_w.Volumes.Asked, p => p.StartsWith(@"\\", StringComparison.Ordinal));
    }

    [Fact]
    public void Folders_HaveTheirBadges()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat (1995)", "Heat (1995).mkv"), "Heat", 1995, false);
        _w.Movie(Path.Join(_w.Movies, "Alien (1979)", "Alien (1979).mkv"), "Alien", 1979, false);
        _w.Folder("Movies", "Crime");
        _w.Folder("Movies", ".hidden");
        _w.Folder("Movies", "Skipped");
        File.WriteAllText(Path.Join(_w.Movies, "Skipped", ".ignore"), string.Empty);
        Junction.Create(Path.Join(_w.Movies, "Link"), _w.Cult);

        var page = _s.Destinations.GetFolders(_w.MoviesLibrary.Id, 0, null, null, 0, 200, _s.Plans.RequireEligible(movie.Id, null), null);

        Assert.Equal(_w.Movies, page.Path);
        Assert.Equal(
            [(".hidden", "Ignored", false), ("Alien (1979)", "MovieFolder", false), ("Crime", "Plain", true), ("Heat (1995)", "MovieFolder", false), ("Link", "ReparsePoint", false), ("Skipped", "Ignored", false)],
            page.Items.Select(f => (f.Name, f.Kind, f.Selectable)));
        Assert.Equal("This is the item's own folder.", page.Items[3].Reason);
        Assert.StartsWith("This is the folder of Alien", page.Items[1].Reason, StringComparison.Ordinal);
        Assert.All(page.Items.Where(f => !f.Selectable), f => Assert.NotNull(f.Reason));
    }

    [Fact]
    public void Folders_InATvLibrary_AreSeriesAndSeasons_WithTheirRelativePaths()
    {
        var series = _w.Series("Lost");
        _w.Season(series, 1);
        _w.Folder("TV", "Lost", "Extras");

        var root = _s.Destinations.GetFolders(_w.TvLibrary.Id, 0, string.Empty, null, 0, 200, null, null);
        var inside = _s.Destinations.GetFolders(_w.TvLibrary.Id, 0, "Lost", null, 0, 200, null, null);

        Assert.Equal([("Lost", "Lost", "Series")], root.Items.Select(f => (f.Name, f.RelativePath, f.Kind)));
        // Lost\Extras holds the series' extras: 10.11.5 ignores it for media (CoreResolutionIgnoreRule.cs:57-62).
        Assert.Equal([("Extras", @"Lost\Extras", "Ignored"), ("Season 1", @"Lost\Season 1", "Season")], inside.Items.Select(f => (f.Name, f.RelativePath, f.Kind)));
        Assert.False(inside.Items[0].Selectable);
        Assert.StartsWith("Jellyfin ignores a folder of this name here", inside.Items[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Folders_AreFilteredSortedAndPaged()
    {
        foreach (var name in new[] { "b-crime", "A-Crime", "c-crime", "drama" })
        {
            _w.Folder("Cult", name);
        }

        var first = _s.Destinations.GetFolders(_w.CultLibrary.Id, 0, null, " CRIME ", 0, 2, null, null);
        var second = _s.Destinations.GetFolders(_w.CultLibrary.Id, 0, null, "crime", 2, 2, null, null);

        Assert.Equal((3, 0), (first.TotalRecordCount, first.StartIndex));
        Assert.Equal(["A-Crime", "b-crime"], first.Items.Select(f => f.Name));
        Assert.Equal(["c-crime"], second.Items.Select(f => f.Name));
    }

    [Fact]
    public void Folders_RefuseALibraryTheCallerCantOpen_ABadPage_ABadPath_AndALibraryOfAnotherType()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        var eligible = _s.Plans.RequireEligible(movie.Id, null);

        PluginException Refused(Action call) => Assert.Throws<PluginException>(call);
        Assert.Equal(PluginErrorKind.NotFound, Refused(() => _s.Destinations.GetFolders(_w.PrivateLibrary.Id, 0, null, null, 0, 200, null, _w.Bob)).Kind);
        Assert.Equal(PluginErrorKind.NotFound, Refused(() => _s.Destinations.GetFolders(Guid.NewGuid(), 0, null, null, 0, 200, null, null)).Kind);
        Assert.Equal(PluginErrorKind.NotFound, Refused(() => _s.Destinations.GetFolders(_w.MoviesLibrary.Id, 1, null, null, 0, 200, null, null)).Kind);
        Assert.Equal(PluginErrorKind.Invalid, Refused(() => _s.Destinations.GetFolders(_w.MoviesLibrary.Id, 0, null, null, 0, 201, null, null)).Kind);
        Assert.Equal(PluginErrorKind.Invalid, Refused(() => _s.Destinations.GetFolders(_w.MoviesLibrary.Id, 0, null, null, -1, 10, null, null)).Kind);
        Assert.Equal("move.destination-invalid", Refused(() => _s.Destinations.GetFolders(_w.MoviesLibrary.Id, 0, @"..\TV", null, 0, 10, null, null)).Code);
        Assert.Equal("move.destination-invalid", Refused(() => _s.Destinations.GetFolders(_w.TvLibrary.Id, 0, null, null, 0, 10, eligible, null)).Code);
    }
}
