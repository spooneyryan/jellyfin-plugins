using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move;

/// <summary>
/// <c>MovePlanService</c>: an item's source, a plan for one item and one destination with its hash,
/// predicted ids, volumes, "Kept" summary, warnings and blockers, kept 15 minutes; the request's checks (one item, a
/// library the caller can open and the item may move to).
/// </summary>
public sealed class MovePlanServiceTests : IDisposable
{
    private readonly PlannerWorld _w = new();
    private readonly PlanServices _s;

    public MovePlanServiceTests() => _s = new PlanServices(_w);

    public void Dispose()
    {
        _s.Dispose();
        _w.Dispose();
    }

    [Fact]
    public async Task ALooseFile_ToAnotherLooseRoot_IsARename_WithItsSidecars_PredictedIds_AndAHash()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true, "Heat.1995.srt", "Heat.1995.nfo");
        _w.Movie(Path.Join(_w.Cult, "Planet.Terror.2007.mkv"), "Planet Terror", 2007, true);

        var plan = await Plan(movie, _w.CultLibrary);

        Assert.Empty(plan.Blockers);
        Assert.Equal("Rename", plan.Method);
        Assert.Matches("^[0-9a-f]{32}$", plan.PlanId);
        Assert.Matches("^[0-9a-f]{64}$", plan.PlanHash);
        Assert.Equal(_s.Clock.Now.UtcDateTime + MovePlanService.Lifetime, plan.ExpiresUtc);
        Assert.Null(plan.EstimatedSeconds);
        var unit = Assert.Single(plan.Units);
        Assert.Equal((movie.Id, "Heat (1995)", "FileSet", _w.Movies, _w.Cult, 3, 1020L), (unit.ItemId, unit.Title, unit.Kind, unit.From, unit.To, unit.Files, unit.Bytes));
        Assert.Null(unit.WrapFolderName);
        var primary = Assert.Single(unit.PredictedIds);
        Assert.Equal(("Primary", movie.Id, Path.Join(_w.Cult, "Heat.1995.mkv")), (primary.Role, primary.OldId, primary.To));
        Assert.Equal(PlannerWorld.IdOf(primary.To, typeof(Movie)), primary.NewId);
        var volume = Assert.Single(plan.Volumes);
        Assert.Equal(0, volume.RequiredBytes);
        Assert.Contains(plan.Regenerated, r => r.StartsWith("Media info", StringComparison.Ordinal));
        Assert.Same(plan.PlanHash, _s.Plans.FindPlan(plan.PlanId)?.PlanHash);
    }

    [Fact]
    public async Task TheHash_IsTheSameForTheSamePlan_AndChangesWithTheFiles()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true, "Heat.1995.srt");

        var first = await Plan(movie, _w.CultLibrary);
        var second = await Plan(movie, _w.CultLibrary);
        Assert.NotEqual(first.PlanId, second.PlanId);
        Assert.Equal(first.PlanHash, second.PlanHash);

        // A sidecar grows, its time kept: the plan is another one; and one touched, its size kept.
        var srt = Path.Join(_w.Movies, "Heat.1995.srt");
        var written = File.GetLastWriteTimeUtc(srt);
        await File.AppendAllTextAsync(srt, "more", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(srt, written);
        var grown = await Plan(movie, _w.CultLibrary);
        Assert.NotEqual(first.PlanHash, grown.PlanHash);
        File.SetLastWriteTimeUtc(srt, written.AddMinutes(1));
        var touched = await Plan(movie, _w.CultLibrary);
        Assert.NotEqual(grown.PlanHash, touched.PlanHash);

        // The same files to another folder.
        _w.Folder("Cult", "Crime");
        Assert.NotEqual(touched.PlanHash, (await Plan(movie, _w.CultLibrary, "Crime")).PlanHash);
    }

    [Fact]
    public async Task APlan_IsKeptFifteenMinutes()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        var plan = await Plan(movie, _w.CultLibrary);

        _s.Clock.Now += MovePlanService.Lifetime - TimeSpan.FromSeconds(1);
        Assert.NotNull(_s.Plans.FindPlan(plan.PlanId));
        _s.Clock.Now += TimeSpan.FromSeconds(1);
        Assert.Null(_s.Plans.FindPlan(plan.PlanId));
        Assert.Null(_s.Plans.FindPlan(null));
        Assert.Null(_s.Plans.FindPlan("nothing"));
    }

    [Fact]
    public async Task AtMostMaxPlansAreKept_TheOldestGoingFirst()
    {
        // The cap, pinned.
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        var ids = new List<string>();
        for (var i = 0; i <= MovePlanService.MaxPlans; i++)
        {
            ids.Add((await Plan(movie, _w.CultLibrary)).PlanId);
            _s.Clock.Now += TimeSpan.FromSeconds(1);
        }

        Assert.Null(_s.Plans.FindPlan(ids[0]));
        Assert.All(ids.Skip(1), id => Assert.NotNull(_s.Plans.FindPlan(id)));
    }

    [Fact]
    public async Task APlan_WritesNothing_NoEntryNoWriteTimeNoWatcherEvent()
    {
        // Planning once made and deleted a probe file in the destination, and the library watcher's
        // Changed event on that folder started a FileRefresher. A location root, a plain folder and a new folder's parent.
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true, "Heat.1995.srt");
        var crime = _w.Folder("Cult", "Crime");
        var before = Directory.EnumerateFileSystemEntries(_w.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        var written = (Directory.GetLastWriteTimeUtc(_w.Cult), Directory.GetLastWriteTimeUtc(crime));
        var events = new List<string>();
        using (var watcher = new FileSystemWatcher(_w.Root) { IncludeSubdirectories = true })
        {
            watcher.NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes;
            void Seen(object sender, FileSystemEventArgs e)
            {
                lock (events)
                {
                    events.Add(e.ChangeType + " " + e.FullPath);
                }
            }

            watcher.Created += Seen;
            watcher.Changed += Seen;
            watcher.Deleted += Seen;
            watcher.Renamed += Seen;
            watcher.EnableRaisingEvents = true;

            Assert.Empty((await Plan(movie, _w.CultLibrary)).Blockers);
            Assert.Empty((await Plan(movie, _w.CultLibrary, "Crime")).Blockers);
            Assert.Empty((await Plan(movie, _w.CultLibrary, "Crime", "Heat (1995)")).Blockers);
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        Assert.Equal(before, Directory.EnumerateFileSystemEntries(_w.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        Assert.Equal(written, (Directory.GetLastWriteTimeUtc(_w.Cult), Directory.GetLastWriteTimeUtc(crime)));
        lock (events)
        {
            Assert.Empty(events);
        }
    }

    [Fact]
    public async Task AnotherVolume_IsCopyVerifyDelete_AndBlockedInThisVersion()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        _w.OnVolume(_w.Cult, "vol-d");

        var plan = await Plan(movie, _w.CultLibrary);

        Assert.Equal("CopyVerifyDelete", plan.Method);
        Assert.Contains(plan.Blockers, b => b.Code == "move.cross-volume-later");
        Assert.Equal(1000, Assert.Single(plan.Volumes).RequiredBytes);
    }

    [Fact]
    public async Task AnItemPlaying_IsABlocker_InThePlan()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        _s.Sessions.Add(new SessionInfo(_s.SessionManager.Object, NullLogger.Instance) { DeviceName = "TV", NowPlayingItem = new BaseItemDto { Id = movie.Id } });

        var plan = await Plan(movie, _w.CultLibrary);

        Assert.Equal("move.playing", Assert.Single(plan.Blockers).Code);
    }

    [Fact]
    public async Task TheRequest_IsOneItem_AndADestination()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        var destination = new LibraryMaintenanceMovePlanDestinationDto { LibraryId = _w.CultLibrary.Id };

        foreach (var request in new LibraryMaintenanceMovePlanRequestDto?[]
        {
            null,
            new() { Destination = destination },
            new() { ItemIds = [], Destination = destination },
            new() { ItemIds = [movie.Id, movie.Id], Destination = destination },
            new() { ItemIds = [movie.Id] },
            new() { ItemIds = [Guid.Empty], Destination = destination },
        })
        {
            var e = await Assert.ThrowsAsync<PluginException>(() => _s.Plans.PlanAsync(request, null, TestContext.Current.CancellationToken));
            Assert.Equal(PluginErrorKind.Invalid, e.Kind);
        }
    }

    [Fact]
    public async Task AnItemTheCallerCantSee_Is404_AndOneThatCantMove_Is409WithItsCode()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Alien (1979)", "Alien (1979).mkv"), "Alien", 1979, false);
        var trailer = _w.Trailer(movie, Path.Join(_w.Movies, "Alien (1979)", "trailers", "Teaser.mkv"));
        _w.Library.Hide(_w.Bob, movie);

        var hidden = await Assert.ThrowsAsync<PluginException>(() => Plan(movie, _w.CultLibrary, user: _w.Bob));
        var extra = await Assert.ThrowsAsync<PluginException>(() => Plan(trailer, _w.CultLibrary));
        var source = Assert.Throws<PluginException>(() => _s.Plans.GetSource(Guid.NewGuid(), null));

        Assert.Equal(PluginErrorKind.NotFound, hidden.Kind);
        Assert.Equal(PluginErrorKind.NotFound, source.Kind);
        Assert.Equal((PluginErrorKind.Conflict, "move.not-eligible"), (extra.Kind, extra.Code));
    }

    [Fact]
    public async Task TheDestinationLibrary_MustBeOneTheCallerOpens_OfTheItemsType()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        var bobs = _w.Movie(Path.Join(_w.Movies, "Ran.1985.mkv"), "Ran", 1985, true);

        var hidden = await Assert.ThrowsAsync<PluginException>(() => Plan(bobs, _w.PrivateLibrary, user: _w.Bob));
        var missing = await Assert.ThrowsAsync<PluginException>(() => Plan(movie, _w.PrivateLibrary with { Id = Guid.NewGuid() }));
        var tv = await Assert.ThrowsAsync<PluginException>(() => Plan(movie, _w.TvLibrary));
        Assert.All([hidden, missing, tv], e => Assert.Equal((PluginErrorKind.Invalid, "move.destination-invalid"), (e.Kind, e.Code)));
        Assert.Equal(hidden.Message, missing.Message);

        // An API key opens every library.
        Assert.Empty((await Plan(movie, _w.PrivateLibrary)).Blockers);

        // Cross-library moves off: only the source library.
        _s.Configuration.AllowCrossLibraryMoves = false;
        var off = await Assert.ThrowsAsync<PluginException>(() => Plan(movie, _w.CultLibrary));
        Assert.Equal("move.destination-invalid", off.Code);
        _w.Folder("Movies", "Crime");
        Assert.Empty((await Plan(movie, _w.MoviesLibrary, "Crime")).Blockers);
    }

    [Fact]
    public async Task ABadDestinationPath_IsDestinationInvalid()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);

        foreach (var (relative, folder, index) in new[] { ("..", (string?)null, 0), ("Nope", null, 0), (string.Empty, "CON", 0), (string.Empty, null, 3) })
        {
            var e = await Assert.ThrowsAsync<PluginException>(() => Plan(movie, _w.CultLibrary, relative, folder, index));
            Assert.Equal((PluginErrorKind.Invalid, "move.destination-invalid"), (e.Kind, e.Code));
        }
    }

    [Fact]
    public async Task Preserved_CountsUsersCollectionsPlaylistsLockedFieldsAndInternalImages()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Alien (1979)", "Alien (1979).mkv"), "Alien", 1979, false, "poster.jpg");
        movie.DateCreated = new DateTime(2021, 3, 4, 0, 0, 0, DateTimeKind.Utc);
        movie.LockedFields = [MetadataField.Overview, MetadataField.Cast];
        movie.ImageInfos =
        [
            new ItemImageInfo { Path = Path.Join(_w.Movies, "Alien (1979)", "poster.jpg"), Type = ImageType.Primary },
            new ItemImageInfo { Path = Path.Join(_w.Root, "metadata", "library", "al", "backdrop.jpg"), Type = ImageType.Backdrop },
            new ItemImageInfo { Path = "https://image.tmdb.org/logo.png", Type = ImageType.Logo },
        ];
        var alice = _s.AddUser("alice");
        var carol = _s.AddUser("carol");
        var dave = _s.AddUser("dave");
        _s.Data[(alice.Id, movie.Id)] = new UserItemData { Key = "k", IsFavorite = true, PlaybackPositionTicks = 10 };
        _s.Data[(carol.Id, movie.Id)] = new UserItemData { Key = "k", Played = true, PlayCount = 1 };
        _s.Data[(dave.Id, movie.Id)] = new UserItemData { Key = "k", Played = true, PlayCount = 2 };
        _w.Library.Add(new BoxSet { Name = "Sci-fi", LinkedChildren = [new LinkedChild { ItemId = movie.Id }] });
        // A link by path alone (10.11's older links); Jellyfin 12's links carry the id alone.
        _w.Library.Add(new BoxSet { Name = "Classics", LinkedChildren = [JellyfinLine.Is12 ? JellyfinLine.Link(movie.Id) : JellyfinLine.Link(null, movie.Path)] });
        _w.Library.Add(new BoxSet { Name = "Other", LinkedChildren = [new LinkedChild { ItemId = Guid.NewGuid() }] });
        _w.Library.Add(new Playlist { Name = "Friday", LinkedChildren = [new LinkedChild { ItemId = movie.Id }] });

        var kept = (await Plan(movie, _w.CultLibrary)).Preserved;

        Assert.Equal((3, 1, 1), (kept.UserDataUsers, kept.Favorites, kept.ResumePoints));
        Assert.Equal(movie.DateCreated, kept.DateCreated);
        Assert.Equal(["Classics", "Sci-fi"], kept.Collections);
        Assert.Equal(["Friday"], kept.Playlists);
        Assert.Equal("AllEditableFields", kept.CarryOverMode);
        Assert.Equal(["Cast", "Overview"], kept.LockedFields);
        Assert.Equal(1, kept.InternalImages);
        Assert.Null(kept.InternalSubtitles);
        Assert.Null(kept.TrickplayResolutions);
        Assert.Empty(kept.HookConsumers);
    }

    [Fact]
    public void Source_OfAnOwnFolderMovie_IsItsFolder_WithVersionsAndExtras()
    {
        var folder = _w.Folder("Movies", "Alien (1979)");
        var movie = _w.Movie(Path.Join(folder, "Alien (1979) - 1080p.mkv"), "Alien", 1979, false, "poster.jpg");
        var version = _w.File(Path.Join(folder, "Alien (1979) - 720p.mkv"));
        movie.LocalAlternateVersions = [version];
        var trailer = _w.Trailer(movie, Path.Join(folder, "trailers", "Teaser.mkv"));

        var source = _s.Plans.GetSource(movie.Id, null);

        Assert.Equal((movie.Id, "Alien", 1979, "Movie"), (source.Item.Id, source.Item.Name, source.Item.Year, source.Item.Kind));
        Assert.Equal((_w.MoviesLibrary.Id, "Movies", "movies"), (source.Library.Id, source.Library.Name, source.Library.CollectionType));
        Assert.Equal(_w.Movies, source.LocationPath);
        Assert.Equal(("Folder", folder), (source.Unit.Kind, source.Unit.Root));
        Assert.Equal(["Alien (1979) - 1080p.mkv", "Alien (1979) - 720p.mkv", "poster.jpg", Path.Join("trailers", "Teaser.mkv")], source.Unit.Files.Select(f => f.Rel).Order(StringComparer.Ordinal));
        Assert.Equal(["trailers"], source.Unit.Dirs);
        Assert.Equal(source.Unit.Files.Sum(f => f.Bytes), source.Unit.TotalBytes);
        Assert.Equal(movie.Path, source.Unit.Primary);
        Assert.Equal([version], source.Unit.Versions);
        Assert.Equal([trailer.Path], source.Unit.Extras);
        Assert.Empty(source.Unit.Parts);
        Assert.Null(source.Retargeted);
    }

    [Fact]
    public void Source_OfALooseFile_IsItsFileSet_AndAVersionSaysWhoseItIs()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true, "Heat.1995.srt", "Heat.1995-poster.jpg");
        var path = _w.File(Path.Join(_w.Movies, "Heat.1995 - Directors Cut.mkv"));
        movie.LocalAlternateVersions = [path];
        var version = _w.Library.Add(new Video { Id = PlannerWorld.IdOf(path, typeof(Video)), Path = path, OwnerId = movie.Id });

        var own = _s.Plans.GetSource(movie.Id, null);
        var asked = _s.Plans.GetSource(version.Id, null);

        Assert.Equal(("FileSet", _w.Movies), (own.Unit.Kind, own.Unit.Root));
        Assert.Contains(own.Unit.Files, f => f.Rel == "Heat.1995.srt");
        Assert.Equal(movie.Id, asked.Item.Id);
        Assert.Equal(movie.Path, own.Unit.Primary);
        Assert.Equal(movie.Path, asked.Unit.Primary);
        Assert.Equal("This version belongs to Heat (1995); its files move together.", asked.Retargeted);
    }

    [Fact]
    public void Source_OfAVersion_NamesItsMovieWithTheYearOnce_WhenTheNameHasIt()
    {
        // Jellyfin keeps the year in the name of a movie whose file name it didn't parse: "Heat (1995)", year 1995.
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat (1995).mkv"), "Heat (1995)", 1995, true);
        var path = _w.File(Path.Join(_w.Movies, "Heat (1995) - Directors Cut.mkv"));
        movie.LocalAlternateVersions = [path];
        var version = _w.Library.Add(new Video { Id = PlannerWorld.IdOf(path, typeof(Video)), Path = path, OwnerId = movie.Id });

        var asked = _s.Plans.GetSource(version.Id, null);

        Assert.Equal("This version belongs to Heat (1995); its files move together.", asked.Retargeted);
    }

    [Fact]
    public async Task AnEpisode_IntoAnotherSeasonOfItsSeries_WarnsAboutTheSeason()
    {
        var series = _w.Series("Lost");
        var episode = _w.Episode(series, _w.Season(series, 1), "Lost - 01.mkv", 1);
        _w.Season(series, 2);

        var plan = await Plan(episode, _w.TvLibrary, Path.Join("Lost", "Season 2"));

        Assert.Empty(plan.Blockers);
        Assert.Contains(plan.Warnings, w => w.Code == "move.episode-other-season");
        Assert.Equal(Path.Join(_w.Tv, "Lost", "Season 2"), plan.Units[0].To);
    }

    private Task<LibraryMaintenanceMovePlanDto> Plan(BaseItem item, LibraryMaintenance.Move.Paths.CatalogLibrary library, string? relative = null, string? newFolder = null, int index = 0, Jellyfin.Database.Implementations.Entities.User? user = null)
        => _s.Plans.PlanAsync(
            new LibraryMaintenanceMovePlanRequestDto
            {
                ItemIds = [item.Id],
                Destination = new LibraryMaintenanceMovePlanDestinationDto { LibraryId = library.Id, LocationIndex = index, RelativePath = relative, NewFolderName = newFolder },
            },
            user,
            TestContext.Current.CancellationToken);
}
