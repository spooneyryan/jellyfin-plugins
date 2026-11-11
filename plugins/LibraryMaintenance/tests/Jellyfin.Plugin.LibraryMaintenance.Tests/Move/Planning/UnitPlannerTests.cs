using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using Jellyfin.Plugin.LibraryMaintenance.Move.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Planning;

/// <summary>
/// The <c>UnitPlanner</c> cases for movies on generated temp trees and a fake library: file sets and folder
/// units, sidecars, foreign media, collisions, movie folders, wrapping, cross-volume, predicted ids.
/// </summary>
public sealed class UnitPlannerTests : IDisposable
{
    private readonly PlannerWorld _w = new();

    public void Dispose() => _w.Dispose();

    [Fact]
    public void LooseFile_InARootFullOfLooseFiles_MovesWithItsSidecars_AndStaysLoose()
    {
        // The loose-file shape: Movies holds loose files; the move to Cult (loose files too) keeps it loose, no warning.
        var movie = _w.Movie(Path.Join(_w.Movies, "2.Fast.2.Furious.2003.mp4"), "2 Fast 2 Furious", 2003, true, "2.Fast.2.Furious.2003.YIFY+HI.eng.srt", "2.Fast.2.Furious.2003.nfo", "2.Fast.2.Furious.2003-poster.jpg");
        _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true, "Heat.1995.srt");
        _w.File(Path.Join(_w.Movies, "2.Fast.2.Furious.2003 notes.txt"));
        _w.Movie(Path.Join(_w.Cult, "Planet.Terror.2007.mkv"), "Planet Terror", 2007, true);

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);

        Assert.Equal(MoveUnitKind.FileSet, unit.Kind);
        Assert.Empty(unit.Blockers);
        Assert.Empty(unit.Warnings);
        Assert.Null(unit.WrapFolderName);
        Assert.Empty(unit.CreatedDirectories);
        Assert.Equal(_w.Cult, unit.TargetDirectory);
        Assert.Equal(
            ["2.Fast.2.Furious.2003-poster.jpg", "2.Fast.2.Furious.2003.YIFY+HI.eng.srt", "2.Fast.2.Furious.2003.mp4", "2.Fast.2.Furious.2003.nfo"],
            unit.Entries.Select(e => e.RelativePath).Order(StringComparer.Ordinal));
        Assert.All(unit.Renames, r => Assert.Equal(Path.Join(_w.Cult, Path.GetFileName(r.From)), r.To));
        Assert.Equal(1030, unit.TotalBytes);
        Assert.Equal(MoveMethod.Rename, unit.Method);
        Assert.Equal("vol-c", unit.SourceVolumeId);

        var primary = Assert.Single(unit.Items);
        Assert.Equal(UnitItemRole.Primary, primary.Role);
        Assert.Equal(Path.Join(_w.Cult, "2.Fast.2.Furious.2003.mp4"), primary.NewPath);
        Assert.Equal(PlannerWorld.IdOf(primary.NewPath, typeof(Movie)), primary.PredictedId);
        Assert.Equal(movie.Id, primary.OldId);
    }

    [Fact]
    public void OwnFolderMovie_WithVersionsPartsAndExtras_MovesAsOneFolder_WithEachItemsPredictedId()
    {
        var folder = _w.Folder("Movies", "Alien (1979)");
        var movie = _w.Movie(Path.Join(folder, "Alien (1979) - 1080p.mkv"), "Alien", 1979, false, "Alien (1979) - 1080p.nfo", "poster.jpg");
        var version = _w.File(Path.Join(folder, "Alien (1979) - 720p.mkv"));
        var part = _w.File(Path.Join(folder, "Alien (1979) - 1080p-cd2.mkv"));
        movie.LocalAlternateVersions = [version];
        movie.AdditionalParts = [part];
        var trailer = _w.Trailer(movie, Path.Join(folder, "trailers", "Teaser.mkv"));
        var featurette = _w.Extra(movie, Path.Join(folder, "extras", "Making of.mkv"), ExtraType.Featurette);
        _w.File(Path.Join(folder, "sample", "sample.mkv"));

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);

        Assert.Equal(MoveUnitKind.Folder, unit.Kind);
        Assert.Empty(unit.Blockers);
        Assert.Equal(folder, unit.SourceRoot);
        var target = Path.Join(_w.Cult, "Alien (1979)");
        Assert.Equal(target, unit.TargetRoot);
        var rename = Assert.Single(unit.Renames);
        Assert.Equal((folder, target, true), (rename.From, rename.To, rename.IsDirectory));
        Assert.Contains(unit.Entries, e => e.RelativePath == Path.Join("trailers", "Teaser.mkv"));
        Assert.Contains(unit.Entries, e => e.RelativePath == "extras" && e.IsDirectory);
        Assert.Equal(11, unit.Entries.Count);

        Assert.Equal([UnitItemRole.Primary, UnitItemRole.Version, UnitItemRole.Part, UnitItemRole.Extra, UnitItemRole.Extra], unit.Items.Select(i => i.Role));
        var byRole = unit.Items.ToLookup(i => i.Role);
        Assert.Equal(PlannerWorld.IdOf(Path.Join(target, "Alien (1979) - 1080p.mkv"), typeof(Movie)), byRole[UnitItemRole.Primary].Single().PredictedId);
        Assert.Equal(PlannerWorld.IdOf(Path.Join(target, "Alien (1979) - 720p.mkv"), typeof(Video)), byRole[UnitItemRole.Version].Single().PredictedId);
        Assert.Equal(PlannerWorld.IdOf(version, typeof(Video)), byRole[UnitItemRole.Version].Single().OldId);
        Assert.Equal(PlannerWorld.IdOf(Path.Join(target, "Alien (1979) - 1080p-cd2.mkv"), typeof(Video)), byRole[UnitItemRole.Part].Single().PredictedId);

        // Extras keep their own type (typeof(Video) misses a trailer).
        var trailerItem = unit.Items.Single(i => i.OldId == trailer.Id);
        Assert.Equal(typeof(Trailer), trailerItem.Type);
        Assert.Equal(PlannerWorld.IdOf(Path.Join(target, "trailers", "Teaser.mkv"), typeof(Trailer)), trailerItem.PredictedId);
        Assert.Equal(PlannerWorld.IdOf(Path.Join(target, "extras", "Making of.mkv"), typeof(Video)), unit.Items.Single(i => i.OldId == featurette.Id).PredictedId);
    }

    [Fact]
    public void DvdFolder_IsAFolderUnit_WhoseItemPathIsTheFolder()
    {
        var folder = _w.Folder("Movies", "Brazil (1985)");
        _w.File(Path.Join(folder, "VIDEO_TS", "VTS_01_1.VOB"));
        _w.File(Path.Join(folder, "VIDEO_TS", "VIDEO_TS.IFO"));
        var movie = _w.Library.Add(new Movie { Id = PlannerWorld.IdOf(folder, typeof(Movie)), Name = "Brazil", ProductionYear = 1985, Path = folder, VideoType = VideoType.Dvd, IsInMixedFolder = false });

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);

        Assert.Equal(MoveUnitKind.Folder, unit.Kind);
        Assert.Empty(unit.Blockers);
        Assert.Equal(Path.Join(_w.Cult, "Brazil (1985)"), Assert.Single(unit.Items).NewPath);
        Assert.Equal(3, unit.Entries.Count);
    }

    [Fact]
    public void FolderUnit_WithAnotherMoviesVideoOrAFolderItemInside_IsForeignMedia()
    {
        var folder = _w.Folder("Movies", "Alien (1979)");
        var movie = _w.Movie(Path.Join(folder, "Alien (1979).mkv"), "Alien", 1979, false);
        var other = _w.Movie(Path.Join(folder, "Aliens (1986)", "Aliens (1986).mkv"), "Aliens", 1986, false);
        var nested = _w.Folder("Movies", "Alien (1979)", "Collection");
        _w.Library.Add(new Folder { Id = Guid.NewGuid(), Name = "Collection", Path = nested });
        _w.File(Path.Join(folder, "unclaimed sample.mkv"));

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);

        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal(MoveErrorCodes.ForeignMedia, blocker.Code);
        Assert.Equal([other.Path, nested], blocker.Paths!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FolderUnit_WithAJunctionInside_IsBlocked()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var folder = _w.Folder("Movies", "Alien (1979)");
        var movie = _w.Movie(Path.Join(folder, "Alien (1979).mkv"), "Alien", 1979, false);
        Paths.Junction.Create(Path.Join(folder, "extras"), _w.Folder("Elsewhere"));

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);

        Assert.Equal([MoveErrorCodes.ReparsePoint], unit.Blockers.Select(b => b.Code));
        Assert.Equal([Path.Join(folder, "extras")], unit.Blockers[0].Paths);
        Assert.Equal([Path.Join(folder, "extras")], unit.Links);
    }

    [Fact]
    public void FolderUnit_NothingBelowAnInnerJunction_IsListedOrLookedUp()
    {
        // The walk once went on below a link: what is behind the junction is
        // another place's, never the unit's, so it is neither an entry nor asked of the library.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var folder = _w.Folder("Movies", "Alien (1979)");
        var movie = _w.Movie(Path.Join(folder, "Alien (1979).mkv"), "Alien", 1979, false);
        var outside = _w.Folder("Outside", "deep");
        _w.File(Path.Join(outside, "inner.mkv"));
        _w.File(Path.Join(outside, "Sub", "inner.srt"));
        var link = Path.Join(folder, "extras");
        Paths.Junction.Create(link, outside);

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);

        Assert.Contains(unit.Links, l => PathRules.SamePath(l, link));
        Assert.DoesNotContain(unit.Entries, e => e.RelativePath.StartsWith("extras" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_w.Library.PathLookups, p => PathRules.IsUnder(p, link) || PathRules.IsSameOrUnder(p, outside));
    }

    [Fact]
    public void FolderUnit_WhoseRootIsALink_IsBlocked_NeverWalked_NorAskedItsVolume()
    {
        // A link is never followed (it may point at another drive or a share).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var root = Path.Join(_w.Movies, "Alien (1979)");
        Paths.Junction.Create(root, _w.Folder("Outside"));
        var movie = _w.Movie(Path.Join(root, "Alien (1979).mkv"), "Alien", 1979, false);
        _w.Movie(Path.Join(root, "Ronin (1998).mkv"), "Ronin", 1998, false);

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);

        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal((MoveErrorCodes.ReparsePoint, root), (blocker.Code, Assert.Single(blocker.Paths!)));
        Assert.Equal([root], unit.Links);
        Assert.Empty(unit.Entries);
        Assert.DoesNotContain(_w.Volumes.Asked, p => PathRules.IsSameOrUnder(p, root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileSet_AnExistingWrapFolderThatIsALink_IsBlocked_AndNothingBelowItIsRead(bool symbolic)
    {
        // A wrap folder that is a junction or a folder symlink: it was once used as it was, and the write probe
        // landed in the link's target.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Links are tested on Windows.");
        var heat = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true, "Heat.1995.srt");
        _w.Movie(Path.Join(_w.Cult, "Planet Terror (2007)", "Planet Terror (2007).mkv"), "Planet Terror", 2007, false);
        var outside = _w.Folder("Outside");
        var link = Path.Join(_w.Cult, "Heat (1995)");
        if (symbolic)
        {
            Paths.SymbolicLink.ToFolder(link, outside);
        }
        else
        {
            Paths.Junction.Create(link, outside);
        }

        // What a look below the link would trip over: a same-named file (a collision) and an own-folder movie.
        _w.File(Path.Join(outside, "Heat.1995.srt"));
        _w.Movie(Path.Join(link, "Ronin (1998).mkv"), "Ronin", 1998, false);

        var unit = _w.Plan(heat, _w.MoviesLibrary, _w.CultLibrary);

        Assert.Equal("Heat (1995)", unit.WrapFolderName);
        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal((MoveErrorCodes.ReparsePoint, link), (blocker.Code, Assert.Single(blocker.Paths!)));
        Assert.Equal([link], unit.Links);
        Assert.True(unit.IsLinked(unit.TargetDirectory));
        Assert.DoesNotContain(_w.Volumes.Asked, p => PathRules.IsSameOrUnder(p, link));
    }

    [Fact]
    public void FolderUnit_ALinkOfItsNameAtTheTarget_IsACollision_AndALink()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var folder = _w.Folder("Movies", "Alien (1979)");
        var movie = _w.Movie(Path.Join(folder, "Alien (1979).mkv"), "Alien", 1979, false);
        var link = Path.Join(_w.Cult, "Alien (1979)");
        Paths.Junction.Create(link, _w.Folder("Outside"));

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);

        Assert.Equal([MoveErrorCodes.Collision, MoveErrorCodes.ReparsePoint], unit.Blockers.Select(b => b.Code));
        Assert.Equal([link], unit.Links);
        Assert.DoesNotContain(_w.Volumes.Asked, p => PathRules.IsSameOrUnder(p, link));
    }

    [Fact]
    public void FileSet_ASymlinkedSidecar_IsBlocked_AndRecordedAsALink()
    {
        var heat = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        var link = Path.Join(_w.Movies, "Heat.1995.srt");
        Paths.SymbolicLink.ToFile(link, _w.File(Path.Join(_w.Folder("Outside"), "elsewhere.srt")));

        var unit = _w.Plan(heat, _w.MoviesLibrary, _w.CultLibrary);

        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal((MoveErrorCodes.ReparsePoint, link), (blocker.Code, Assert.Single(blocker.Paths!)));
        Assert.Equal([link], unit.Links);
    }

    [Fact]
    public void ExtrasNamedDestinations_AreRefusedWhereJellyfinIgnoresThem()
    {
        // An existing or new folder named like an extras folder below another folder.
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        _w.Movie(Path.Join(_w.Cult, "Collection", "A.2001.mkv"), "A", 2001, true);
        foreach (var name in new[] { "extras", "Shorts", "Other", "trailers", "behind the scenes" })
        {
            _w.Folder("Cult", "Collection", name);
            var e = Assert.Throws<Jellyfin.Plugin.Kit.Api.PluginException>(() => _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary, relativePath: "Collection\\" + name, wrap: false));
            Assert.Equal(MoveErrorCodes.ExtrasFolderName, e.Code);
        }

        var made = Assert.Throws<Jellyfin.Plugin.Kit.Api.PluginException>(() => _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary, relativePath: "Collection", newFolder: "Featurettes", wrap: false));
        Assert.Equal(MoveErrorCodes.ExtrasFolderName, made.Code);

        // Directly in the location, Jellyfin reads it as an ordinary folder.
        _w.Folder("Cult", "Extras");
        Assert.Empty(_w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary, relativePath: "Extras", wrap: false).Blockers);
    }

    [Fact]
    public void AWrapFolderOrAFolderUnit_NamedLikeAnExtrasFolder_IsBlockedBelowAnotherFolder()
    {
        var extras = _w.Movie(Path.Join(_w.Movies, "Extras.mkv"), "Extras", null, true);
        _w.Folder("Cult", "Collection");
        var wrapped = _w.Plan(extras, _w.MoviesLibrary, _w.CultLibrary, relativePath: "Collection", wrap: true);
        Assert.Equal("Extras", wrapped.WrapFolderName);
        Assert.Equal([MoveErrorCodes.ExtrasFolderName], wrapped.Blockers.Select(b => b.Code));
        Assert.Empty(_w.Plan(extras, _w.MoviesLibrary, _w.CultLibrary, wrap: true).Blockers);

        var folder = _w.Folder("Movies", "Trailers");
        var movie = _w.Movie(Path.Join(folder, "Trailers.mkv"), "Trailers", null, false);
        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary, relativePath: "Collection");
        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal((MoveErrorCodes.ExtrasFolderName, Path.Join(_w.Cult, "Collection", "Trailers")), (blocker.Code, Assert.Single(blocker.Paths!)));
        Assert.Empty(_w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary).Blockers);
    }

    [Fact]
    public void FolderUnit_ThatIsAnotherLibrarysLocation_OrHoldsOne_IsBlocked_AndNeverWalked()
    {
        // A library location inside the movie's folder: MoviesKids, another library's location, planned as a movie's own folder.
        var kids = _w.Folder("Movies", "Kids");
        _w.OtherLibraryLocation(kids);
        var film = _w.Movie(Path.Join(kids, "Film.mkv"), "Film", 2000, false);
        _w.Movie(Path.Join(kids, "Other.mkv"), "Other", 2001, false);

        var unit = _w.Plan(film, _w.MoviesLibrary, _w.CultLibrary);
        Assert.Equal((MoveErrorCodes.NotEligible, kids), (Assert.Single(unit.Blockers).Code, Assert.Single(unit.Blockers[0].Paths!)));
        Assert.Empty(unit.Entries);

        // A movie folder that holds a location (MoviesBoxKids2) would move that library's media.
        var box = _w.Folder("Movies", "Box");
        var kids2 = _w.Folder("Movies", "Box", "Kids2");
        _w.OtherLibraryLocation(kids2.ToUpperInvariant() + "\\");
        var boxed = _w.Movie(Path.Join(box, "Boxed.mkv"), "Boxed", 2002, false);
        var held = _w.Plan(boxed, _w.MoviesLibrary, _w.CultLibrary);
        var blocker = Assert.Single(held.Blockers);
        Assert.Equal(MoveErrorCodes.ForeignMedia, blocker.Code);
        Assert.True(PathRules.SamePath(kids2, Assert.Single(blocker.Paths!)));
        Assert.Empty(held.Entries);
    }

    [Fact]
    public void FolderUnit_WithAVersionOrPartOutsideItsFolder_IsBlocked()
    {
        // A version outside the movie's folder was once dropped without a word; a file set blocks the same case.
        var folder = _w.Folder("Movies", "Alien (1979)");
        var movie = _w.Movie(Path.Join(folder, "Alien (1979).mkv"), "Alien", 1979, false);
        var stray = _w.File(Path.Join(_w.Movies, "Alien (1979) - 720p.mkv"));
        var part = _w.File(Path.Join(_w.Movies, "Elsewhere", "Alien (1979) - cd2.mkv"));
        movie.LocalAlternateVersions = [stray];
        movie.AdditionalParts = [part];

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);

        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal(MoveErrorCodes.NotEligible, blocker.Code);
        Assert.Equal([stray, part], blocker.Paths);
    }

    [Fact]
    public void FolderUnit_ThatIsALibraryLocation_IsBlocked()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Alien (1979).mkv"), "Alien", 1979, false);
        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);
        Assert.Equal([MoveErrorCodes.NotEligible], unit.Blockers.Select(b => b.Code));
        Assert.Empty(unit.Entries);
    }

    [Fact]
    public void MixedFolder_MatchesSidecarsByStem_NeverEp10ForEp1()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Ep 1.mkv"), "Ep 1", null, true, "Ep 1.en.forced.srt", "Ep 1.ass", "Ep 1.mka", "Ep 1-poster.jpg", "Ep 1_thumb.png", "ep 1.NFO", "Ep 1.trickplay/320 - 10x10/0.jpg");
        _w.Movie(Path.Join(_w.Movies, "Ep 10.mkv"), "Ep 10", null, true, "Ep 10.srt", "Ep 10-poster.jpg", "Ep 10.trickplay/320 - 10x10/0.jpg");
        _w.File(Path.Join(_w.Movies, "Ep 1-notes.txt"));
        _w.File(Path.Join(_w.Movies, "Ep 1 - Part 2.mkv"));

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);

        Assert.Empty(unit.Blockers);
        // "Ep 1_thumb.png" isn't a name 10.11.5 reads for Ep 1 (LocalImageProvider takes "<stem>-thumb"): it stays.
        Assert.Equal(
            ["Ep 1-poster.jpg", "Ep 1.ass", "Ep 1.en.forced.srt", "Ep 1.mka", "Ep 1.mkv", "Ep 1.trickplay", "ep 1.NFO"],
            unit.Renames.Select(r => Path.GetFileName(r.From)).Order(StringComparer.Ordinal));
        Assert.True(unit.Renames.Single(r => r.From.EndsWith(".trickplay", StringComparison.Ordinal)).IsDirectory);
        Assert.Contains(unit.Entries, e => e.RelativePath == Path.Join("Ep 1.trickplay", "320 - 10x10", "0.jpg"));
        Assert.DoesNotContain(unit.Entries, e => e.RelativePath.StartsWith("Ep 10", StringComparison.Ordinal));
    }

    [Fact]
    public void FileSet_ATrickplayFolderThatIsAJunction_IsALink_NeverWalked()
    {
        // A linked trickplay member was once walked: the member is blocked as a
        // link, and nothing behind it (another movie, as it happens) is listed or asked of the library.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var outside = _w.Folder("Outside", "tp");
        _w.File(Path.Join(outside, "320 - 10x10", "0.jpg"));
        _w.Movie(Path.Join(outside, "Other.mkv"), "Other", 2000, true);
        var heat = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        var trickplay = Path.Join(_w.Movies, "Heat.1995.trickplay");
        Paths.Junction.Create(trickplay, outside);

        var unit = _w.Plan(heat, _w.MoviesLibrary, _w.CultLibrary);

        Assert.Equal((MoveErrorCodes.ReparsePoint, trickplay), (Assert.Single(unit.Blockers).Code, Assert.Single(unit.Blockers[0].Paths!)));
        Assert.Contains(unit.Links, l => PathRules.SamePath(l, trickplay));
        Assert.DoesNotContain(unit.Entries, e => e.RelativePath.StartsWith("Heat.1995.trickplay" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_w.Library.PathLookups, p => PathRules.IsUnder(p, trickplay) || PathRules.IsSameOrUnder(p, outside));
    }

    [Fact]
    public void FileSet_AFolderNamedLikeTheStem_IsNeverAMember_OnlyItsTrickplayFolder()
    {
        // Another movie's own folder: Movies\Alien.mkv once took Movies\Alien.Resurrection.1997\, another movie's own folder.
        var alien = _w.Movie(Path.Join(_w.Movies, "Alien.mkv"), "Alien", 1979, true, "Alien.srt", "Alien.trickplay/320 - 10x10/0.jpg");
        var other = _w.Movie(Path.Join(_w.Movies, "Alien.Resurrection.1997", "Alien.Resurrection.1997.mkv"), "Alien Resurrection", 1997, false, "movie.nfo");
        _w.Folder("Movies", "Alien.extras");
        _w.Folder("Movies", "ALIEN.TRICKPLAY.old");

        var unit = _w.Plan(alien, _w.MoviesLibrary, _w.CultLibrary);

        Assert.Empty(unit.Blockers);
        Assert.Equal(["Alien.mkv", "Alien.srt", "Alien.trickplay"], unit.Renames.Select(r => Path.GetFileName(r.From)).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(unit.Entries, e => e.RelativePath.StartsWith("Alien.Resurrection", StringComparison.Ordinal));
        Assert.DoesNotContain(unit.Items, i => i.OldId == other.Id);
        var warning = Assert.Single(unit.Warnings);
        Assert.Equal(MoveErrorCodes.SidecarLeftBehind, warning.Code);
        Assert.Equal(
            [Path.Join(_w.Movies, "ALIEN.TRICKPLAY.old"), Path.Join(_w.Movies, "Alien.Resurrection.1997"), Path.Join(_w.Movies, "Alien.extras")],
            warning.Paths!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FileSet_EachSidecarGoesToTheLongestStem_NeverAnotherVideosFiles()
    {
        // The longest stem wins: Movie.2003.mkv once took Movie.2003.Extended.Cut's nfo, subtitles and poster.
        var movie = _w.Movie(Path.Join(_w.Movies, "Movie.2003.mkv"), "Movie", 2003, true, "Movie.2003.srt", "Movie.2003.nfo", "Movie.2003-poster.jpg");
        var cut = _w.Movie(Path.Join(_w.Movies, "Movie.2003.Extended.Cut.mkv"), "Movie Extended", 2003, true, "Movie.2003.Extended.Cut.nfo", "Movie.2003.Extended.Cut.en.srt", "Movie.2003.Extended.Cut-poster.jpg");

        var plain = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);
        Assert.Empty(plain.Blockers);
        Assert.Equal(
            ["Movie.2003-poster.jpg", "Movie.2003.mkv", "Movie.2003.nfo", "Movie.2003.srt"],
            plain.Entries.Select(e => e.RelativePath).Order(StringComparer.Ordinal));
        Assert.Equal([cut.Path], Assert.Single(plain.Warnings).Paths);

        var longer = _w.Plan(cut, _w.MoviesLibrary, _w.CultLibrary);
        Assert.Empty(longer.Blockers);
        Assert.Empty(longer.Warnings);
        Assert.Equal(
            ["Movie.2003.Extended.Cut-poster.jpg", "Movie.2003.Extended.Cut.en.srt", "Movie.2003.Extended.Cut.mkv", "Movie.2003.Extended.Cut.nfo"],
            longer.Entries.Select(e => e.RelativePath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FileSet_AnOrphansNfoAndImages_NeedJellyfinsExactForms_ItsSubtitlesThePrefix()
    {
        // With no Movie.2003.Extended.Cut video, 10.11.5 reads its .nfo and poster for no item (exact "<stem>.nfo",
        // "<stem>-poster.jpg"), but takes its .en.srt as Movie.2003's ("Movie.2003" and a dot; MediaInfoResolver.cs:235-241).
        var movie = _w.Movie(Path.Join(_w.Movies, "Movie.2003.mkv"), "Movie", 2003, true, "Movie.2003.Extended.Cut.nfo", "Movie.2003.Extended.Cut.en.srt", "Movie.2003.Extended.Cut-poster.jpg", "Movie.2003.fanart.jpg", "Movie.2003_thumb.png", "Movie.2003-fanart-2.jpg", "Movie.2003-backdrop3.jpg", "Movie.2003.idx", "Movie.2003.jpg");

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);

        Assert.Empty(unit.Blockers);
        Assert.Equal(
            ["Movie.2003-backdrop3.jpg", "Movie.2003-fanart-2.jpg", "Movie.2003.Extended.Cut.en.srt", "Movie.2003.idx", "Movie.2003.jpg", "Movie.2003.mkv"],
            unit.Entries.Select(e => e.RelativePath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FileSet_AnExtrasOwnSidecars_MoveWithIt()
    {
        var heat = _w.Movie(Path.Join(_w.Movies, "Heat.mkv"), "Heat", 1995, true, "Heat.srt");
        _w.Extra(heat, Path.Join(_w.Movies, "Heat-trailer.mkv"), ExtraType.Trailer);
        _w.File(Path.Join(_w.Movies, "Heat-trailer.srt"));
        _w.File(Path.Join(_w.Movies, "Heat-trailer.nfo"));

        var unit = _w.Plan(heat, _w.MoviesLibrary, _w.CultLibrary);

        Assert.Empty(unit.Blockers);
        Assert.Equal(
            ["Heat-trailer.mkv", "Heat-trailer.nfo", "Heat-trailer.srt", "Heat.mkv", "Heat.srt"],
            unit.Entries.Select(e => e.RelativePath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FileSet_ATrickplayFolderWithAnotherItemsMedia_IsForeignMedia()
    {
        var heat = _w.Movie(Path.Join(_w.Movies, "Heat.mkv"), "Heat", 1995, true, "Heat.trickplay/320 - 10x10/0.jpg");
        var other = _w.Movie(Path.Join(_w.Movies, "Heat.trickplay", "Ronin.mkv"), "Ronin", 1998, true);
        var nested = _w.Folder("Movies", "Heat.trickplay", "Collection");
        _w.Library.Add(new Folder { Id = Guid.NewGuid(), Name = "Collection", Path = nested });
        _w.File(Path.Join(_w.Movies, "Heat.trickplay", "unclaimed.mkv"));

        var unit = _w.Plan(heat, _w.MoviesLibrary, _w.CultLibrary);

        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal(MoveErrorCodes.ForeignMedia, blocker.Code);
        Assert.Equal([nested, other.Path], blocker.Paths!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FileSet_ATrickplayFolderWithItsOwnExtraInside_IsFine_AndATooLargeOneIsBlocked()
    {
        var heat = _w.Movie(Path.Join(_w.Movies, "Heat.mkv"), "Heat", 1995, true, "Heat.trickplay/320 - 10x10/0.jpg", "Heat.trickplay/320 - 10x10/1.jpg");
        _w.Extra(heat, Path.Join(_w.Movies, "Heat.trickplay", "odd extra.mkv"), ExtraType.Clip);
        Assert.Empty(_w.Plan(heat, _w.MoviesLibrary, _w.CultLibrary).Blockers);

        var small = new UnitPlanner(_w.Library.Object, _w.Volumes, PlannerWorld.ValidFilename) { WalkLimit = 3 };
        var unit = _w.Plan(heat, _w.MoviesLibrary, _w.CultLibrary, planner: small);

        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal(MoveErrorCodes.ForeignMedia, blocker.Code);
        Assert.Equal([Path.Join(_w.Movies, "Heat.trickplay")], blocker.Paths);
        Assert.Contains("more than 3 files and folders", blocker.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FolderUnit_TooLarge_IsBlocked()
    {
        var folder = _w.Folder("Movies", "Alien (1979)");
        var movie = _w.Movie(Path.Join(folder, "Alien (1979).mkv"), "Alien", 1979, false, "poster.jpg", "movie.nfo", "a.srt");
        var small = new UnitPlanner(_w.Library.Object, _w.Volumes, PlannerWorld.ValidFilename) { WalkLimit = 3 };

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary, planner: small);

        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal((MoveErrorCodes.ForeignMedia, folder), (blocker.Code, Assert.Single(blocker.Paths!)));
        Assert.Empty(_w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary).Blockers);
    }

    [Fact]
    public void MixedFolder_AVideoNamedLikeTheItem_IsLeftBehindWithAWarning()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.mkv"), "Heat", 1995, true, "Heat.srt");
        var other = _w.Movie(Path.Join(_w.Movies, "Heat.Extended.mkv"), "Heat Extended", 1995, true);
        var unknown = _w.File(Path.Join(_w.Movies, "Heat.sample.mkv"));

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);

        Assert.Empty(unit.Blockers);
        var warning = Assert.Single(unit.Warnings);
        Assert.Equal(MoveErrorCodes.SidecarLeftBehind, warning.Code);
        Assert.Equal([other.Path, unknown], warning.Paths!.Order(StringComparer.Ordinal));
        Assert.Equal(["Heat.mkv", "Heat.srt"], unit.Renames.Select(r => Path.GetFileName(r.From)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void MixedFolder_ExtrasNamedAfterTheStemMove_SharedFolderExtrasStay()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.mkv"), "Heat", 1995, true);
        var trailer = _w.Trailer(movie, Path.Join(_w.Movies, "Heat-trailer.mkv"));
        var shared = _w.Trailer(movie, Path.Join(_w.Movies, "trailers", "Heat.mkv"));

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);

        Assert.Empty(unit.Blockers);
        Assert.Contains(unit.Items, i => i.Role == UnitItemRole.Extra && i.OldId == trailer.Id && i.NewPath == Path.Join(_w.Cult, "Heat-trailer.mkv"));
        Assert.DoesNotContain(unit.Items, i => i.OldId == shared.Id);
        Assert.Equal([shared.Path], Assert.Single(unit.Warnings, w => w.Code == MoveErrorCodes.SidecarLeftBehind).Paths);
    }

    [Fact]
    public void StackedParts_InAMixedFolder_MoveWithTheirSidecars()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat-cd1.mkv"), "Heat", 1995, true, "Heat-cd1.srt");
        movie.AdditionalParts = [_w.File(Path.Join(_w.Movies, "Heat-cd2.mkv"))];
        _w.File(Path.Join(_w.Movies, "Heat-cd2.srt"));

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);

        Assert.Empty(unit.Blockers);
        Assert.Equal(["Heat-cd1.mkv", "Heat-cd1.srt", "Heat-cd2.mkv", "Heat-cd2.srt"], unit.Renames.Select(r => Path.GetFileName(r.From)).Order(StringComparer.Ordinal));
        Assert.Equal(PlannerWorld.IdOf(Path.Join(_w.Cult, "Heat-cd2.mkv"), typeof(Video)), unit.Items.Single(i => i.Role == UnitItemRole.Part).PredictedId);
    }

    [Theory]
    [InlineData("Heat.mkv")]
    [InlineData("HEAT.MKV")]
    [InlineData("heat.srt")]
    public void FileSet_AnExistingTarget_IsACollision_IgnoringCase(string existing)
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.mkv"), "Heat", 1995, true, "Heat.srt");
        _w.File(Path.Join(_w.Cult, existing));

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary, wrap: false);

        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal(MoveErrorCodes.Collision, blocker.Code);
        Assert.Equal([Path.Join(_w.Cult, existing)], blocker.Paths);
    }

    [Fact]
    public void FolderUnit_AnExistingEmptyFolderOfTheSameName_IsACollision()
    {
        var movie = _w.Movie(Path.Join(_w.Folder("Movies", "Alien (1979)"), "Alien (1979).mkv"), "Alien", 1979, false);
        _w.Folder("Cult", "ALIEN (1979)");

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);

        Assert.Equal([MoveErrorCodes.Collision], unit.Blockers.Select(b => b.Code));
    }

    [Fact]
    public void Destination_TheCurrentFolder_OrInsideTheUnit_IsBlocked()
    {
        var loose = _w.Movie(Path.Join(_w.Movies, "Heat.mkv"), "Heat", 1995, true);
        Assert.Equal([MoveErrorCodes.DestinationInvalid], _w.Plan(loose, _w.MoviesLibrary, _w.MoviesLibrary, wrap: false).Blockers.Select(b => b.Code));

        var folder = _w.Folder("Movies", "Alien (1979)");
        var own = _w.Movie(Path.Join(folder, "Alien (1979).mkv"), "Alien", 1979, false);
        _w.Folder("Movies", "Alien (1979)", "Stills");
        Assert.Equal([MoveErrorCodes.DestinationInvalid], _w.Plan(own, _w.MoviesLibrary, _w.MoviesLibrary).Blockers.Select(b => b.Code));
        Assert.Contains(MoveErrorCodes.DestinationInvalid, _w.Plan(own, _w.MoviesLibrary, _w.MoviesLibrary, "Alien (1979)/Stills").Blockers.Select(b => b.Code));
    }

    [Fact]
    public void FileSet_IntoAnotherMoviesOwnFolder_WouldMakeItMixed_AndIsBlocked()
    {
        var loose = _w.Movie(Path.Join(_w.Movies, "Heat.mkv"), "Heat", 1995, true);
        _w.Movie(Path.Join(_w.Folder("Cult", "Alien (1979)"), "Alien (1979).mkv"), "Alien", 1979, false);

        var unit = _w.Plan(loose, _w.MoviesLibrary, _w.CultLibrary, "Alien (1979)", wrap: false);

        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal(MoveErrorCodes.IntoMovieFolder, blocker.Code);
        Assert.Contains("mixed folder", blocker.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnyUnit_IntoAFolderBelowAMoviesOwnFolder_IsBlocked()
    {
        var own = _w.Movie(Path.Join(_w.Folder("Movies", "Heat (1995)"), "Heat (1995).mkv"), "Heat", 1995, false);
        var loose = _w.Movie(Path.Join(_w.Movies, "Ronin.mkv"), "Ronin", 1998, true);
        _w.Movie(Path.Join(_w.Folder("Cult", "Alien (1979)"), "Alien (1979).mkv"), "Alien", 1979, false);
        _w.Folder("Cult", "Alien (1979)", "Stills");

        Assert.Equal([MoveErrorCodes.IntoMovieFolder], _w.Plan(own, _w.MoviesLibrary, _w.CultLibrary, "Alien (1979)").Blockers.Select(b => b.Code));
        Assert.Equal([MoveErrorCodes.IntoMovieFolder], _w.Plan(own, _w.MoviesLibrary, _w.CultLibrary, "Alien (1979)/Stills").Blockers.Select(b => b.Code));
        Assert.Equal([MoveErrorCodes.IntoMovieFolder], _w.Plan(loose, _w.MoviesLibrary, _w.CultLibrary, "Alien (1979)", newFolder: "Ronin (1998)").Blockers.Select(b => b.Code));
    }

    [Fact]
    public void MatchDestination_WrapsALooseFile_IntoAFolderOfOwnFolderMovies_WithTheCleanedName()
    {
        var loose = _w.Movie(Path.Join(_w.Movies, "Alien.Covenant.2017.mkv"), "Alien: Covenant", 2017, true, "Alien.Covenant.2017.srt");
        _w.Movie(Path.Join(_w.Folder("Cult", "Heat (1995)"), "Heat (1995).mkv"), "Heat", 1995, false);

        var unit = _w.Plan(loose, _w.MoviesLibrary, _w.CultLibrary);

        Assert.Empty(unit.Blockers);
        Assert.Equal("Alien - Covenant (2017)", unit.WrapFolderName);
        var wrap = Path.Join(_w.Cult, "Alien - Covenant (2017)");
        Assert.Equal([wrap], unit.CreatedDirectories);
        Assert.Equal(wrap, unit.TargetDirectory);
        Assert.Equal(Path.Join(wrap, "Alien.Covenant.2017.mkv"), unit.PrimaryItem.NewPath);
        Assert.Equal(PlannerWorld.IdOf(Path.Join(wrap, "Alien.Covenant.2017.mkv"), typeof(Movie)), unit.PrimaryItem.PredictedId);
    }

    [Fact]
    public void MatchDestination_ARootWithLooseVideos_StaysLoose_EvenWithOwnFolderMoviesToo()
    {
        // Such a root is "mixed" already (MovieResolver.cs:284), so the file joins the loose ones, without a warning.
        var loose = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        _w.Movie(Path.Join(_w.Folder("Cult", "Alien (1979)"), "Alien (1979).mkv"), "Alien", 1979, false);
        _w.Movie(Path.Join(_w.Cult, "Ronin.1998.mkv"), "Ronin", 1998, true);

        var unit = _w.Plan(loose, _w.MoviesLibrary, _w.CultLibrary);

        Assert.Null(unit.WrapFolderName);
        Assert.Empty(unit.Warnings);
        Assert.Equal(_w.Cult, unit.TargetDirectory);
    }

    [Fact]
    public void Wrapping_FollowsTheModeAndTheDialogsChoice()
    {
        var loose = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        _w.Movie(Path.Join(_w.Folder("Cult", "Alien (1979)"), "Alien (1979).mkv"), "Alien", 1979, false);
        _w.Movie(Path.Join(_w.Folder("Cult", "Mixed"), "Ronin.mkv"), "Ronin", 1998, true);

        Assert.Null(_w.Plan(loose, _w.MoviesLibrary, _w.CultLibrary, settings: new MoveSettings { LooseFileFolderMode = LooseFileFolderMode.Never }).WrapFolderName);
        Assert.Null(_w.Plan(loose, _w.MoviesLibrary, _w.CultLibrary, wrap: false).WrapFolderName);
        Assert.Equal("Heat (1995)", _w.Plan(loose, _w.MoviesLibrary, _w.CultLibrary, "Mixed", settings: new MoveSettings { LooseFileFolderMode = LooseFileFolderMode.Always }).WrapFolderName);
        Assert.Equal("Heat (1995)", _w.Plan(loose, _w.MoviesLibrary, _w.CultLibrary, "Mixed", wrap: true).WrapFolderName);
        Assert.Equal("Heat Director's Cut", _w.Plan(loose, _w.MoviesLibrary, _w.CultLibrary, wrap: true, wrapName: " Heat Director's Cut ").WrapFolderName);

        // A folder with loose videos stays loose, and a new folder is the movie's own folder already.
        Assert.Null(_w.Plan(loose, _w.MoviesLibrary, _w.CultLibrary, "Mixed").WrapFolderName);
        Assert.Null(_w.Plan(loose, _w.MoviesLibrary, _w.CultLibrary, newFolder: "Heat (1995)").WrapFolderName);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("CON")]
    [InlineData("a:b")]
    [InlineData("..")]
    public void Wrapping_ANameMoveMayNotCreate_IsBlocked(string name)
    {
        var loose = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        Assert.Contains(MoveErrorCodes.DestinationInvalid, _w.Plan(loose, _w.MoviesLibrary, _w.CultLibrary, wrap: true, wrapName: name).Blockers.Select(b => b.Code));
    }

    [Fact]
    public void Wrapping_IntoAnExistingFolderOfThatName_UsesIt_FileByFile()
    {
        var loose = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        var existing = _w.Folder("Cult", "heat (1995)");

        var unit = _w.Plan(loose, _w.MoviesLibrary, _w.CultLibrary, wrap: true);

        Assert.Empty(unit.Blockers);
        Assert.Equal("heat (1995)", unit.WrapFolderName);
        Assert.Equal(existing, unit.TargetDirectory);
        Assert.Empty(unit.CreatedDirectories);

        _w.File(Path.Join(_w.Cult, "Ronin (1998)"));
        var ronin = _w.Movie(Path.Join(_w.Movies, "Ronin.mkv"), "Ronin", 1998, true);
        Assert.Contains(MoveErrorCodes.Collision, _w.Plan(ronin, _w.MoviesLibrary, _w.CultLibrary, wrap: true).Blockers.Select(b => b.Code));
    }

    [Fact]
    public void AnotherVolume_IsCopyVerifyDelete_AndBlockedInV1()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.mkv"), "Heat", 1995, true);
        _w.OnVolume(_w.Cult, "vol-d");

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);

        Assert.Equal(MoveMethod.CopyVerifyDelete, unit.Method);
        Assert.Equal(("vol-c", "vol-d"), (unit.SourceVolumeId, unit.DestinationVolumeId));
        Assert.Equal([MoveErrorCodes.CrossVolumeLater], unit.Blockers.Select(b => b.Code));
    }

    [Fact]
    public void NewFolders_AreAskedAboutAtTheirNearestExistingFolder()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.mkv"), "Heat", 1995, true);
        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary, newFolder: "Heat (1995)");
        Assert.Empty(unit.Blockers);
        Assert.Equal([Path.Join(_w.Cult, "Heat (1995)")], unit.CreatedDirectories);
        Assert.Contains(Path.Join(_w.Cult, "Heat (1995)"), _w.Volumes.Asked);
    }

    [Theory]
    [InlineData("Alien: Covenant", 2017, "Alien - Covenant (2017)")]
    [InlineData("AC/DC: Live at River Plate", 2011, "AC DC - Live at River Plate (2011)")]
    [InlineData("What?", null, "What")]
    [InlineData("  Spaced   Out  ", 2000, "Spaced Out (2000)")]
    [InlineData("Trailing...", null, "Trailing")]
    [InlineData("Star Wars: Episode IV: A New Hope", 1977, "Star Wars - Episode IV - A New Hope (1977)")]
    [InlineData("Re:Zero", 2016, "Re Zero (2016)")]
    [InlineData("\"Quoted\" <Title> | Pipe*", null, "Quoted Title Pipe")]
    [InlineData("", null, "")]
    [InlineData(null, 1999, "(1999)")]
    [InlineData("Folder Movie (2008)", 2008, "Folder Movie (2008)")]
    [InlineData("Folder Movie (2008)", 2009, "Folder Movie (2008) (2009)")]
    public void WrapName_CleansTheTitleAndYear(string? title, int? year, string expected)
        => Assert.Equal(expected, WrapName.Clean(title, year, PlannerWorld.ValidFilename));
}
