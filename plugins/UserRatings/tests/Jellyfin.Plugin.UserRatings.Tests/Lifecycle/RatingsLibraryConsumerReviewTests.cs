using Jellyfin.Plugin.Kit.TestSupport;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.UserRatings.Api;
using Jellyfin.Plugin.UserRatings.Api.Models;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Api;
using Jellyfin.Plugin.UserRatings.Tests.Services;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Lifecycle;

/// <summary>
/// The fixes of 0.3.8.0, on the same fixture: a deleted music artist folder's albums, which 10.11.5's item cache
/// keeps, are never taken for there (the rating stays detached through the daily maintenance, and follows a renamed folder's
/// album by its MusicBrainz id); every rated item of a library folder gone at once is held (the mass-detach guard), and still
/// follows a renamed folder; another title or another kind at a held or detached rating's path never takes it; the startup
/// re-resolution; an admin's Detach now; the batch's counts of the sweep's holds; a rating back on its own id logged as
/// reattached.
/// </summary>
public sealed partial class RatingsLibraryConsumerTests
{
    private const string AlbumMbid = "f1e2d3c4-0000-4000-8000-00000000000a";

    // ------------------------------------------------------------------------------------------------ the cache ghost

    [Fact]
    public async Task ADeletedArtistFolder_TheDailyMaintenance_LeavesTheAlbumsRatingDetached_NotOnTheCachedGhost()
    {
        // The sweep detached the rating (its parent walk), and the daily re-resolution took it back to the same id, which the
        // item cache still had, flip-flopping every day.
        var root = _library.Add(new Folder { Name = "Music", Path = _music });
        var (artist, album) = AddArtistAlbum(root, "Artist A", "Album A", null);
        AddArtistAlbum(root, "Artist B", "Album B", null);
        var rating = await RateAsync(album, _alice, 8);
        var monitor = Monitor();

        _library.Remove(artist, cacheKeepsDescendants: true);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        Assert.Equal(RatingValues.DetachReason.Missing, _fx.Store.Get(rating.Id)!.DetachReason);
        Assert.Same(album, _library.Object.GetItemById(album.Id));

        // Two days of the maintenance's steps (VerifySweep, then ResolveDetached: KitMaintenanceOrder).
        for (var day = 0; day < 2; day++)
        {
            _fx.Clock.Advance(TimeSpan.FromDays(1));
            await _verifier.SweepAsync(VerifySweepReason.Daily, Ct);
            await _detached.ResolveDetachedAsync(_consumer, Ct);
            var row = _fx.Store.Get(rating.Id)!;
            Assert.False(row.IsAttached, $"reattached to {row.ItemId}, the deleted album {album.Id} the item cache still has");
        }

        Assert.DoesNotContain(_logger.At(LogLevel.Information), e => e.Message.Contains($"Rating {rating.Id} reattached", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeletedArtistFolder_AFormerMembersRatingOfTheAlbum_StaysDetached_TooWithoutAViewToCheckIn()
    {
        // A former member's rating is resolved server-wide (no member to resolve it in), so no
        // view check walks the album's parents after the resolver's first step; that step's own walk keeps it off the ghost.
        var root = _library.Add(new Folder { Name = "Music", Path = _music });
        var (artist, album) = AddArtistAlbum(root, "Artist A", "Album A", null);
        var former = ServicesFixture.NewUser("former");
        var rating = await RateAsync(album, former, 8);
        var monitor = Monitor();

        _library.Remove(artist, cacheKeepsDescendants: true);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        Assert.Equal(RatingValues.DetachReason.Missing, _fx.Store.Get(rating.Id)!.DetachReason);
        Assert.Same(album, _library.Object.GetItemById(album.Id));
        Assert.Null(Assert.Single(_consumer.GetDetached(), r => r.Id == rating.Id.ToString()).UserId);

        for (var day = 0; day < 2; day++)
        {
            _fx.Clock.Advance(TimeSpan.FromDays(1));
            await _verifier.SweepAsync(VerifySweepReason.Daily, Ct);
            var resolved = await _detached.ResolveDetachedAsync(_consumer, Ct);
            Assert.Equal(0, resolved.Relinked);
            Assert.False(_fx.Store.Get(rating.Id)!.IsAttached, $"reattached to the deleted album {album.Id} the item cache still has");
        }
    }

    [Fact]
    public async Task ARenamedArtistFolder_TheAlbumsRatingFollowsTheNewAlbum_NotTheCachedGhost()
    {
        // The new album shares the MusicBrainz id; the old one is still in the item cache under the deleted artist.
        var root = _library.Add(new Folder { Name = "Music", Path = _music });
        var (artist, album) = AddArtistAlbum(root, "Artist A", "Album A", AlbumMbid);
        AddArtistAlbum(root, "Artist B", "Album B", null);
        var rating = await RateAsync(album, _alice, 8, "Synthetic.");
        var monitor = Monitor();
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));

        _library.Remove(artist, cacheKeepsDescendants: true);
        var (renamed, album2) = AddArtistAlbum(root, "Artist A (renamed)", "Album A", AlbumMbid);
        _library.RaiseAdded(renamed);
        _library.RaiseAdded(album2);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var row = _fx.Store.Get(rating.Id)!;
        Assert.Equal((album2.Id, true, Now), (row.ItemId, row.IsAttached, row.AttachedUtc));
        AssertMemberFieldsKept(rating, row);
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains($"Rating {rating.Id} moved from item {album.Id} to item {album2.Id} (Resolve: Strong, ProviderIds)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARatingBackOnItsOwnId_IsLoggedAsReattached_NotAsMovedFromTheItemToItself()
    {
        var movie = AddMovie("Heat (1995)", _movies, "949");
        var rating = await RateAsync(movie, _alice, 9);
        var monitor = Monitor();
        _library.Remove(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        _library.Add(movie);
        _library.RaiseAdded(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.True(_fx.Store.Get(rating.Id)!.IsAttached);
        var information = _logger.At(LogLevel.Information).Select(e => e.Message).ToList();
        Assert.Contains(information, m => m.Contains($"Rating {rating.Id} reattached to item {movie.Id} (Resolve: Exact, Id)", StringComparison.Ordinal));
        Assert.DoesNotContain(information, m => m.Contains($"moved from item {movie.Id} to item {movie.Id}", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------- the mass-detach guard

    [Fact]
    public async Task EveryRatedItemOfALibraryFolderGoneAtOnce_IsHeld_AsFolderUnavailable_UntilTheyComeBack()
    {
        // A drive letter given to a volume with other contents: the folder is there and readable, and every rated item is gone.
        var heat = AddMovie("Heat (1995)", _movies, "949");
        var ronin = AddMovie("Ronin (1998)", _movies, "8195");
        var onHeat = await RateAsync(heat, _alice, 9);
        var onRonin = await RateAsync(ronin, _bob, 7);
        var monitor = Monitor();
        var generation = _fx.Store.Generation;

        _library.Remove(heat);
        _library.Remove(ronin);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.True(_fx.Store.Get(onHeat.Id)!.IsAttached);
        Assert.True(_fx.Store.Get(onRonin.Id)!.IsAttached);
        Assert.Equal(generation, _fx.Store.Generation);
        var held = Held();
        Assert.Equal(new[] { heat.Id, ronin.Id }.Order(), held.Select(h => (Guid)h["ItemId"]!).Order());
        Assert.All(held, h => Assert.Equal((LibraryAvailability.FolderUnavailable, true), ((string)h["Reason"]!, (bool)h["MassDetach"]!)));
        Assert.Equal(2, (int)Lifecycle("LastBatch")["Held"]!);
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.Contains("2 rated items of one library folder would detach at once in a library change (2 rated items there in all, none left)", StringComparison.Ordinal));

        // The daily sweep finds them still gone: still held.
        await _verifier.SweepAsync(VerifySweepReason.Daily, Ct);
        Assert.True(_fx.Store.Get(onHeat.Id)!.IsAttached);
        Assert.All(Held(), h => Assert.True((bool)h["MassDetach"]!));

        // The drive back: the same paths give the same ids.
        _library.Add(heat);
        _library.Add(ronin);
        _library.RaiseAdded(heat);
        _library.RaiseAdded(ronin);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        Assert.Empty(Held());
        Assert.Equal(generation, _fx.Store.Generation);
    }

    [Fact]
    public async Task OneRatedItemGone_OrSomeWithAnotherRatedItemStillThere_Detach_AsBefore()
    {
        // The guard's threshold (two rated items) and its "none left" rule.
        var heat = AddMovie("Heat (1995)", _movies, "949");
        var onHeat = await RateAsync(heat, _alice, 9);
        var a = AddMovie("A (2001)", _movies2, "900001");
        var b = AddMovie("B (2002)", _movies2, "900002");
        var c = AddMovie("C (2003)", _movies2, "900003");
        var onA = await RateAsync(a, _alice);
        var onB = await RateAsync(b, _bob);
        var onC = await RateAsync(c, _bob);
        var monitor = Monitor();

        _library.Remove(heat);
        _library.Remove(a);
        _library.Remove(b);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.All(new[] { onHeat, onA, onB }, r => Assert.Equal(RatingValues.DetachReason.Removed, _fx.Store.Get(r.Id)!.DetachReason));
        Assert.True(_fx.Store.Get(onC.Id)!.IsAttached);
        Assert.Empty(Held());
        Assert.DoesNotContain(_logger.At(LogLevel.Warning), e => e.Message.Contains("would detach at once", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheVerifySweep_HoldsEveryRatedItemOfAFolderGoneAtOnce_UntilAnotherIsRatedThere()
    {
        var heat = AddMovie("Heat (1995)", _movies, "949");
        var ronin = AddMovie("Ronin (1998)", _movies, "8195");
        var onHeat = await RateAsync(heat, _alice, 9);
        var onRonin = await RateAsync(ronin, _bob, 7);
        _library.Remove(heat, raiseEvent: false);
        _library.Remove(ronin, raiseEvent: false);

        await _verifier.SweepAsync(VerifySweepReason.Startup, Ct);
        Assert.True(_fx.Store.Get(onHeat.Id)!.IsAttached);
        Assert.True(_fx.Store.Get(onRonin.Id)!.IsAttached);
        Assert.Equal(2, Held().Count(h => (bool)h["MassDetach"]!));

        // A rating on another film there: the folder has a rated item again, so the next sweep lets the two detach.
        await RateAsync(AddMovie("Other (2001)", _movies, "900001"), _alice);
        _fx.Clock.Advance(TimeSpan.FromDays(1));
        await _verifier.SweepAsync(VerifySweepReason.Daily, Ct);
        Assert.Equal((RatingValues.DetachReason.Missing, Now), (_fx.Store.Get(onHeat.Id)!.DetachReason, _fx.Store.Get(onHeat.Id)!.DetachedUtc));
        Assert.Equal(RatingValues.DetachReason.Missing, _fx.Store.Get(onRonin.Id)!.DetachReason);
        Assert.Empty(Held());
    }

    [Fact]
    public async Task ARenamedSeriesFolderThatHeldEveryRatedItem_IsHeldByTheGuard_AndStillFollowsTheRename()
    {
        // The guard must not cost the rename: the kit's resolver looks at its held ratings for the batch's added items.
        var series = _library.AddSeries("Test Series", Path.Combine(_shows, "Test Series"), ("Tvdb", "900401"));
        var season = _library.AddSeason(series, 1);
        var episode = _library.AddEpisode(series, season, 1);
        var onEpisode = await RateAsync(episode, _alice, 7, "Synthetic.");
        var onSeries = await RateAsync(series, _bob, 6);
        var monitor = Monitor();
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));

        _library.Remove(series);
        var renamed = _library.AddSeries("Test Series", Path.Combine(_shows, "Test Series (2020)"), ("Tvdb", "900401"));
        var season2 = _library.AddSeason(renamed, 1);
        var episode2 = _library.AddEpisode(renamed, season2, 1);
        _library.RaiseAdded(renamed);
        _library.RaiseAdded(season2);
        _library.RaiseAdded(episode2);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.Equal((renamed.Id, true), (_fx.Store.Get(onSeries.Id)!.ItemId, _fx.Store.Get(onSeries.Id)!.IsAttached));
        Assert.Equal((episode2.Id, true, renamed.Id), (_fx.Store.Get(onEpisode.Id)!.ItemId, _fx.Store.Get(onEpisode.Id)!.IsAttached, _fx.Store.Get(onEpisode.Id)!.Identity.SeriesId));
        AssertMemberFieldsKept(onEpisode, _fx.Store.Get(onEpisode.Id)!);
        Assert.Empty(Held());
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.Contains("would detach at once", StringComparison.Ordinal));
        var resolve = Assert.IsAssignableFrom<System.Collections.Generic.IDictionary<string, object?>>(Lifecycle("LastBatch")["Resolve"]);
        Assert.Equal(2, (int)resolve["Relinked"]!);
    }

    [Fact]
    public async Task AGuardHeldRename_AfterARestart_FollowsTheRename_AtTheStartupSweep()
    {
        // The server restarted between the scan that renamed the folder and the batch that would have
        // taken the ratings (the held list is in memory only, and the renamed copies' Added events are lost).
        var heat = AddMovie("Heat (1995)", _movies2, "949");
        var ronin = AddMovie("Ronin (1998)", _movies2, "8195");
        var onHeat = await RateAsync(heat, _alice, 9, "A great heist.");
        var onRonin = await RateAsync(ronin, _bob, 7);
        var monitor = Monitor();
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        _library.Remove(heat);
        _library.Remove(ronin);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        Assert.Equal(2, Held().Count(h => (bool)h["MassDetach"]!));
        var heat2 = AddMovie("Heat 1995", _movies2, "949");
        var ronin2 = AddMovie("Ronin 1998", _movies2, "8195");

        Create();
        await _verifier.SweepAsync(VerifySweepReason.Startup, Ct);

        Assert.Equal((heat2.Id, true), (_fx.Store.Get(onHeat.Id)!.ItemId, _fx.Store.Get(onHeat.Id)!.IsAttached));
        Assert.Equal((ronin2.Id, true), (_fx.Store.Get(onRonin.Id)!.ItemId, _fx.Store.Get(onRonin.Id)!.IsAttached));
        AssertMemberFieldsKept(onHeat, _fx.Store.Get(onHeat.Id)!);
        Assert.Empty(Held());
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains("2 of 2 ratings the mass-detach guard holds followed their items", StringComparison.Ordinal));

        // The next sweep holds nothing again (the guard's own rows were never ambiguous or detached).
        _fx.Clock.Advance(TimeSpan.FromDays(1));
        await _verifier.SweepAsync(VerifySweepReason.Daily, Ct);
        Assert.Empty(Held());
        Assert.Empty(_consumer.GetDetached());
    }

    [Fact]
    public async Task AGuardHeldRename_WhoseCopiesCameLater_FollowsAtTheDailyResolve_AndWithNoCopyStaysHeld()
    {
        // The held ratings in the daily resolve (and the startup one): the copies appear after the sweep held the ratings,
        // and no batch says so.
        var heat = AddMovie("Heat (1995)", _movies2, "949");
        var ronin = AddMovie("Ronin (1998)", _movies2, "8195");
        var onHeat = await RateAsync(heat, _alice, 9);
        var onRonin = await RateAsync(ronin, _bob, 7);
        _library.Remove(heat, raiseEvent: false);
        _library.Remove(ronin, raiseEvent: false);
        await _verifier.SweepAsync(VerifySweepReason.Startup, Ct);
        Assert.Equal(2, Held().Count(h => (bool)h["MassDetach"]!));

        // No copy anywhere: the resolve leaves them held, attached to their own ids.
        var none = await _detached.ResolveDetachedAsync(_consumer, Ct);
        Assert.Equal((2, 0, 0), (none.Checked, none.Relinked, none.Ambiguous));
        Assert.Equal((heat.Id, true), (_fx.Store.Get(onHeat.Id)!.ItemId, _fx.Store.Get(onHeat.Id)!.IsAttached));
        Assert.Equal(2, Held().Count);

        var heat2 = AddMovie("Heat 1995", _movies2, "949");
        var ronin2 = AddMovie("Ronin 1998", _movies2, "8195");
        _fx.Clock.Advance(TimeSpan.FromDays(1));
        await _verifier.SweepAsync(VerifySweepReason.Daily, Ct);
        Assert.Equal(heat.Id, _fx.Store.Get(onHeat.Id)!.ItemId);
        var result = await _detached.ResolveDetachedAsync(_consumer, Ct);

        Assert.Equal((2, 2), (result.Checked, result.Relinked));
        Assert.Equal((heat2.Id, true), (_fx.Store.Get(onHeat.Id)!.ItemId, _fx.Store.Get(onHeat.Id)!.IsAttached));
        Assert.Equal((ronin2.Id, true), (_fx.Store.Get(onRonin.Id)!.ItemId, _fx.Store.Get(onRonin.Id)!.IsAttached));
        Assert.Empty(Held());
    }

    [Fact]
    public async Task AtStartup_TheResolve_AlsoTakesTheRatingsTheGuardHoldsByThen()
    {
        var heat = AddMovie("Heat (1995)", _movies2, "949");
        var ronin = AddMovie("Ronin (1998)", _movies2, "8195");
        var onHeat = await RateAsync(heat, _alice, 9);
        await RateAsync(ronin, _bob, 7);
        _library.Remove(heat, raiseEvent: false);
        _library.Remove(ronin, raiseEvent: false);
        await _verifier.SweepAsync(VerifySweepReason.Startup, Ct);
        var heat2 = AddMovie("Heat 1995", _movies2, "949");

        await _consumer.StartAsync(Ct);
        await WaitUntilAsync(() => (bool)_consumer.Describe()["StartupDone"]!);
        await _consumer.StopAsync(Ct);

        Assert.Equal((heat2.Id, true), (_fx.Store.Get(onHeat.Id)!.ItemId, _fx.Store.Get(onHeat.Id)!.IsAttached));
        var resolved = Lifecycle("StartupResolve");
        Assert.Equal((2, 1), ((int)resolved["Checked"]!, (int)resolved["Relinked"]!));
        Assert.Equal(ronin.Id, (Guid)Assert.Single(Held())["ItemId"]!);
    }

    // ----------------------------------------------------------------------------- another title or kind at the path

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHeldFilmBackAsAVideoAtItsPath_IsLeftAmbiguous_UnlessAStrongIdAgrees(bool idAgrees)
    {
        var movie = AddMovie("Heat (1995)", _movies, "949");
        Directory.CreateDirectory(Path.GetDirectoryName(movie.Path)!);
        await File.WriteAllTextAsync(movie.Path, "synthetic", Ct);
        await RateAsync(AddMovie("Ronin (1998)", _movies, "8195"), _bob);
        var rating = await RateAsync(movie, _alice, 9, "A great heist.");
        var monitor = Monitor();

        // Its metadata lost (an NFO deleted): the scan drops the Movie and adds a Video at the same path. Before 0.3.8 the
        // rating stayed held for good (the kinds had to match).
        _library.Remove(movie);
        var video = _library.Add(new Video { Name = "Heat (1995)", Path = movie.Path });
        if (idAgrees)
        {
            video.ProviderIds["Tmdb"] = "949";
        }

        _library.RaiseAdded(video);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var row = _fx.Store.Get(rating.Id)!;
        AssertMemberFieldsKept(rating, row);
        Assert.Empty(Held());
        if (idAgrees)
        {
            Assert.Equal((video.Id, true, "Video"), (row.ItemId, row.IsAttached, row.Identity.Kind));
            Assert.Empty(_entries);
            return;
        }

        Assert.Equal((movie.Id, RatingValues.DetachReason.Ambiguous), (row.ItemId, row.DetachReason));
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains($"Rating {rating.Id} (item {movie.Id}) needs a manual relink: KindChanged, candidates {video.Id}", StringComparison.Ordinal));
        Assert.Equal(RatingsLibraryConsumer.AmbiguousActivityName(1), Assert.Single(_entries).Name);

        // The daily re-resolution leaves it so (the Video is no Movie, and shares no id).
        await _detached.ResolveDetachedAsync(_consumer, Ct);
        Assert.Equal(RatingValues.DetachReason.Ambiguous, _fx.Store.Get(rating.Id)!.DetachReason);
        Assert.Single(_entries);
    }

    [Fact]
    public async Task AVersionVideo_BackAsAMovieVersionAtItsPath_TakesTheRatingOnJellyfin12_WithoutIds()
    {
        // Jellyfin 12 deletes a local alternate version stored as a Video under a Movie and re-creates it as a Movie under a
        // new id (the id hashes the type): the same file, still a version of the same primary, and no ids of its own.
        var primary = AddMovie("Heat (1995)", _movies, "949");
        var path = Path.Combine(Path.GetDirectoryName(primary.Path)!, "Heat (1995) - 4K.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "synthetic", Ct);
        var version = _library.Add(new Video { Name = "Heat (1995) - 4K", Path = path });
        version.SetPrimaryVersion(primary.Id);
        await RateAsync(AddMovie("Ronin (1998)", _movies, "8195"), _bob);
        var rating = await RateAsync(version, _alice, 9, "The 4K cut.");
        var monitor = Monitor();

        _library.Remove(version);
        var again = _library.Add(new Movie { Name = "Heat (1995) - 4K", Path = path });
        again.SetPrimaryVersion(primary.Id);
        _library.RaiseAdded(again);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var row = _fx.Store.Get(rating.Id)!;
        AssertMemberFieldsKept(rating, row);
        if (JellyfinLine.Is12)
        {
            // It lands on the version's primary, as a rating on a merged version does (Land).
            Assert.True(row.IsAttached);
            Assert.Contains(row.ItemId, new[] { again.Id, primary.Id });
            Assert.Empty(_entries);
        }
        else
        {
            Assert.Equal((version.Id, RatingValues.DetachReason.Ambiguous), (row.ItemId, row.DetachReason));
        }
    }

    [Fact]
    public async Task TheSweep_TooLeavesAHeldRatingAmbiguous_WhenAnotherFilmOrAVideoIsAtItsPath()
    {
        var heat = AddMovie("Heat (1995)", _movies, "949");
        var ronin = AddMovie("Ronin (1998)", _movies, "8195");
        foreach (var movie in new[] { heat, ronin })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(movie.Path)!);
            await File.WriteAllTextAsync(movie.Path, "synthetic", Ct);
        }

        await RateAsync(AddMovie("Other (2001)", _movies, "900001"), _bob);
        var onHeat = await RateAsync(heat, _alice, 9);
        var onRonin = await RateAsync(ronin, _alice, 7);
        _library.Remove(heat, raiseEvent: false);
        _library.Remove(ronin, raiseEvent: false);
        var otherFilm = _library.Add(new Movie { Name = "Another Film", ProductionYear = 2001, Path = heat.Path });
        otherFilm.ProviderIds["Tmdb"] = "111";
        var video = _library.Add(new Video { Name = "Ronin", Path = ronin.Path });

        await _verifier.SweepAsync(VerifySweepReason.Daily, Ct);

        Assert.Equal((heat.Id, RatingValues.DetachReason.Ambiguous), (_fx.Store.Get(onHeat.Id)!.ItemId, _fx.Store.Get(onHeat.Id)!.DetachReason));
        Assert.Equal((ronin.Id, RatingValues.DetachReason.Ambiguous), (_fx.Store.Get(onRonin.Id)!.ItemId, _fx.Store.Get(onRonin.Id)!.DetachReason));
        var information = _logger.At(LogLevel.Information).Select(e => e.Message).ToList();
        Assert.Contains(information, m => m.Contains($"Rating {onHeat.Id} (item {heat.Id}) needs a manual relink: ConflictingProviderIds, candidates {otherFilm.Id}", StringComparison.Ordinal));
        Assert.Contains(information, m => m.Contains($"Rating {onRonin.Id} (item {ronin.Id}) needs a manual relink: KindChanged, candidates {video.Id}", StringComparison.Ordinal));
        Assert.Empty(Held());
        Assert.Equal(RatingsLibraryConsumer.AmbiguousActivityName(2), Assert.Single(_entries).Name);
    }

    [Fact]
    public async Task ADetachedRating_NeverReattachesByItsIdOrPath_ToAnotherFilmPutThere()
    {
        // Jellyfin's ids come from paths: another film put at a removed film's path gets the same id back, scraped as itself.
        var movie = AddMovie("Heat (1995)", _movies, "949");
        await RateAsync(AddMovie("Ronin (1998)", _movies, "8195"), _bob);
        var rating = await RateAsync(movie, _alice, 9);
        var monitor = Monitor();
        _library.Remove(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        Assert.Equal(RatingValues.DetachReason.Removed, _fx.Store.Get(rating.Id)!.DetachReason);

        var other = _library.Add(new Movie { Id = movie.Id, Name = "Another Film", ProductionYear = 2001, Path = movie.Path });
        other.ProviderIds["Tmdb"] = "111";
        _library.RaiseAdded(other);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var row = _fx.Store.Get(rating.Id)!;
        Assert.Equal((movie.Id, false, RatingValues.DetachReason.Ambiguous), (row.ItemId, row.IsAttached, row.DetachReason));
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains($"Rating {rating.Id} (item {movie.Id}) needs a manual relink: ConflictingProviderIds, candidates {movie.Id}", StringComparison.Ordinal));
        Assert.True(_consumer.RelinkPolicy.RefuseConflictingExact);
    }

    // ----------------------------------------------------------------------------------------------- at startup

    [Fact]
    public async Task AtStartup_TheDetachedRatingsAreResolvedOnce_AfterTheCatchUp()
    {
        // An item back while the server was off: its rating doesn't wait for the daily maintenance.
        var movie = AddMovie("Heat (1995)", _movies, "949");
        var rating = await UpsertAsync(Rating(_alice, movie.Id, "Movie") with
        {
            Identity = RatingsLibraryConsumer.Normalize(ItemIdentity.Capture(movie, _library.Object)),
            DetachedUtc = StoreFixture.T0.AddDays(-1),
            DetachReason = RatingValues.DetachReason.Removed
        });

        // The order: Ronin moved to a new id while the server was off, with Bob's current rating
        // on it, and Bob's older rating of Ronin, detached ten days ago, matches the new id too. The catch-up comes first, so
        // the current rating takes the item and the old one stays detached as it was (Ambiguous, its detach time kept);
        // resolved first, the old one would take the item for a moment and be detached again now.
        var oldRonin = Guid.NewGuid();
        var ronin = AddMovie("Ronin (1998)", _movies2, "8195");
        var current = await UpsertAsync(Rating(_bob, oldRonin, "Movie") with { UpdatedUtc = Created.AddDays(20) });
        var stale = await UpsertAsync(Rating(_bob, Guid.NewGuid(), "Movie") with
        {
            Identity = RatingsLibraryConsumer.Normalize(ItemIdentity.Capture(ronin, _library.Object)) with { ItemId = Guid.NewGuid() },
            DetachedUtc = StoreFixture.T0.AddDays(-10),
            DetachReason = RatingValues.DetachReason.Removed
        });
        WriteJournal(Line(1, oldRonin, ronin.Id, StoreFixture.T0.AddDays(-1)));

        await _consumer.StartAsync(Ct);
        await WaitUntilAsync(() => (bool)_consumer.Describe()["StartupDone"]!);
        await _consumer.StopAsync(Ct);

        Assert.Equal((movie.Id, true), (_fx.Store.Get(rating.Id)!.ItemId, _fx.Store.Get(rating.Id)!.IsAttached));
        Assert.Equal((ronin.Id, true), (_fx.Store.Get(current.Id)!.ItemId, _fx.Store.Get(current.Id)!.IsAttached));
        var old = _fx.Store.Get(stale.Id)!;
        Assert.Equal((RatingValues.DetachReason.Ambiguous, (DateTime?)StoreFixture.T0.AddDays(-10)), (old.DetachReason, old.DetachedUtc));
        Assert.DoesNotContain(_logger.At(LogLevel.Information), e => e.Message.Contains(stale.Id.ToString(), StringComparison.Ordinal) && e.Message.Contains("moved", StringComparison.OrdinalIgnoreCase));
        var resolved = Lifecycle("StartupResolve");
        Assert.Equal((2, 1), ((int)resolved["Checked"]!, (int)resolved["Relinked"]!));
    }

    // ------------------------------------------------------------------------------------- Detach now and Diagnostics

    [Fact]
    public async Task DetachNow_DetachesTheHeldRatings_LoggedById_AndLeavesWhatIsntHeldOrIsBack()
    {
        var movie = AddMovie("Heat (1995)", _movies, "949");
        var rating = await RateAsync(movie, _alice, 9, "A great heist.");
        var ronin = AddMovie("Ronin (1998)", _movies2, "8195");
        var onRonin = await RateAsync(ronin, _bob);
        Directory.Delete(_movies, recursive: true);
        Directory.Delete(_movies2, recursive: true);
        _library.Remove(movie, raiseEvent: false);
        _library.Remove(ronin, raiseEvent: false);

        // Not checked since the server started: nothing listed, and Diagnostics says so.
        Assert.Null(_consumer.Describe()["HeldCheckedUtc"]);
        Assert.Empty(Held());
        await _verifier.SweepAsync(VerifySweepReason.Startup, Ct);
        Assert.Equal(2, Held().Count);
        Assert.Equal(Now, (DateTime)_consumer.Describe()["HeldCheckedUtc"]!);

        // Ronin is back (the scan hasn't said so yet), and one id isn't held at all.
        _library.Add(ronin);
        var admin = ServicesFixture.NewUser("admin");
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        var result = await _consumer.DetachHeldAsync([movie.Id, ronin.Id, Guid.NewGuid()], admin.Id, Ct);

        Assert.Equal((1, 1, 2), result);
        var row = _fx.Store.Get(rating.Id)!;
        Assert.Equal((RatingValues.DetachReason.Missing, (DateTime?)Now), (row.DetachReason, row.DetachedUtc));
        AssertMemberFieldsKept(rating, row);
        Assert.True(_fx.Store.Get(onRonin.Id)!.IsAttached);
        Assert.Empty(Held());
        var log = Assert.Single(_fx.Store.GetModerationLog(0, 10), l => l.Action == RatingValues.ModerationAction.Detach);
        Assert.Equal(
            ((Guid?)admin.Id, (Guid?)rating.Id, (Guid?)_alice.Id, (Guid?)movie.Id, LibraryAvailability.FolderUnavailable),
            (log.ActorUserId, log.RatingId, log.TargetUserId, log.ItemId, log.Reason));
        Assert.Contains("\"HeldSinceUtc\":", log.Details, StringComparison.Ordinal);
        Assert.Contains("\"MassDetach\":false", log.Details, StringComparison.Ordinal);
        var entry = Assert.Single(_entries);
        Assert.Equal((RatingsLibraryConsumer.HeldDetachedActivityName(1), RatingsLibraryConsumer.HeldDetachedActivityType, admin.Id), (entry.Name, entry.Type, entry.UserId));
        Assert.Contains(rating.Id.ToString(), entry.Overview, StringComparison.Ordinal);
        Assert.Contains(movie.Id.ToString(), entry.Overview, StringComparison.Ordinal);
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains($"1 ratings on item {movie.Id} detached (Missing: an admin detached them from Data → Detached & ambiguous): {rating.Id}", StringComparison.Ordinal));

        // Again: nothing is held any more.
        Assert.Equal((0, 0, 1), await _consumer.DetachHeldAsync([movie.Id], admin.Id, Ct));
        Assert.Single(_entries);
        Assert.Equal("User Ratings: an admin detached 3 kept ratings", RatingsLibraryConsumer.HeldDetachedActivityName(3));
    }

    [Fact]
    public async Task DetachNow_AsksAgainUnderTheWrite_SoAnItemBackRightAfterTheFirstLook_KeepsItsRatings()
    {
        // The item comes back between Detach now's first look and its write.
        var movie = AddMovie("Heat (1995)", _movies, "949");
        var rating = await RateAsync(movie, _alice, 9, "A great heist.");
        var other = AddMovie("Ronin (1998)", _movies2, "8195");
        await RateAsync(other, _bob);
        Directory.Delete(_movies, recursive: true);
        _library.Remove(movie, raiseEvent: false);
        await _verifier.SweepAsync(VerifySweepReason.Startup, Ct);
        Assert.Single(Held());
        var generation = _fx.Store.Generation;

        var calls = 0;
        _library.Mock.Setup(l => l.GetItemById(movie.Id)).Returns(() =>
        {
            calls++;
            if (calls == 1)
            {
                _library.Add(movie);
                return null;
            }

            return movie;
        });
        var result = await _consumer.DetachHeldAsync([movie.Id], ServicesFixture.NewUser("admin").Id, Ct);

        Assert.Equal((0, 0, 1), result);
        Assert.True(calls >= 2, "asked again under the write");
        var row = _fx.Store.Get(rating.Id)!;
        Assert.Equal((movie.Id, true), (row.ItemId, row.IsAttached));
        AssertMemberFieldsKept(rating, row);
        Assert.Empty(Held());
        Assert.Empty(_entries);
        Assert.DoesNotContain(_fx.Store.GetModerationLog(0, 10), l => l.Action == RatingValues.ModerationAction.Detach);
        Assert.True(_fx.Store.Generation - generation <= 1, "at most the write's own transaction");
    }

    [Fact]
    public async Task TheBatchsVerifySweep_CountsTheRatingsItHolds_InHeld()
    {
        // An artist folder removed while its album's folder is still on disk: the batch's sweep holds the album's rating
        // (StillOnDisk), and LastBatch says so beside step 1's holds.
        var root = _library.Add(new Folder { Name = "Music", Path = _music });
        var (artist, album) = AddArtistAlbum(root, "Artist A", "Album A", null);
        AddArtistAlbum(root, "Artist B", "Album B", null);
        Directory.CreateDirectory(album.Path);
        await File.WriteAllTextAsync(Path.Combine(album.Path, "01.flac"), "synthetic", Ct);
        var rating = await RateAsync(album, _alice);
        var monitor = Monitor();

        _library.Remove(artist, cacheKeepsDescendants: true);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.True(_fx.Store.Get(rating.Id)!.IsAttached);
        var last = Lifecycle("LastBatch");
        Assert.Equal((0, 1, 1), ((int)last["Swept"]!, (int)last["SweptHeld"]!, (int)last["Held"]!));
        Assert.Equal(LibraryAvailability.StillOnDisk, (string)Assert.Single(Held())["Reason"]!);
    }

    [Fact]
    public async Task DetachNow_ThroughTheAdminRoute_TakesTheCallersId_RefusesWhatIsntAnAdmin_AndChecksTheIds()
    {
        var movie = AddMovie("Heat (1995)", _movies, "949");
        var rating = await RateAsync(movie, _alice, 9);
        var ronin = AddMovie("Ronin (1998)", _movies2, "8195");
        var onRonin = await RateAsync(ronin, _bob);
        Directory.Delete(_movies, recursive: true);
        Directory.Delete(_movies2, recursive: true);
        _library.Remove(movie, raiseEvent: false);
        _library.Remove(ronin, raiseEvent: false);
        await _verifier.SweepAsync(VerifySweepReason.Startup, Ct);
        Assert.Equal(2, Held().Count);
        await using var host = await StartAdminHostAsync(new UserRatingsAdminApi(_consumer, NoUsers, NoOwner));
        var admin = ServicesFixture.NewUser("admin");
        string Body(params Guid[] ids) => JsonSerializer.Serialize(new { ItemIds = ids });

        // Only an admin or an API key; nothing changes before.
        Assert.Equal(401, await StatusAsync(host, null, null, Body(movie.Id)));
        Assert.Equal(403, await StatusAsync(host, "user", _alice.Id, Body(movie.Id)));
        Assert.Equal(2, Held().Count);

        // No body (415, as on the member routes), no ids, an empty list, too many: 400, with the service's sentence for a list.
        Assert.Equal(415, await StatusAsync(host, "admin", admin.Id, null));
        Assert.Equal(400, await StatusAsync(host, "admin", admin.Id, "null"));
        Assert.Equal(400, await StatusAsync(host, "admin", admin.Id, "{}"));
        using (var empty = await PostHeldDetachAsync(host, "admin", admin.Id, Body()))
        {
            Assert.Equal(400, (int)empty.StatusCode);
            Assert.Contains(UserRatingsAdminApi.ItemIdsMessage, await empty.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        Assert.Equal(400, await StatusAsync(host, "admin", admin.Id, Body(Enumerable.Range(0, UserRatingsHeldDetachRequest.MaxItemIds + 1).Select(_ => Guid.NewGuid()).ToArray())));
        Assert.Equal(2, Held().Count);

        // An admin: their id in the moderation log.
        var answer = await JsonAsync(host, "admin", admin.Id, Body(movie.Id, Guid.NewGuid()));
        Assert.Equal((1, 1, 1), (answer.GetProperty("Items").GetInt32(), answer.GetProperty("Ratings").GetInt32(), answer.GetProperty("NotHeld").GetInt32()));
        Assert.Equal(RatingValues.DetachReason.Missing, _fx.Store.Get(rating.Id)!.DetachReason);
        Assert.Equal((Guid?)admin.Id, Assert.Single(_fx.Store.GetModerationLog(0, 10), l => l.RatingId == rating.Id).ActorUserId);

        // An API key: no actor.
        answer = await JsonAsync(host, "apikey", Guid.Empty, Body(ronin.Id));
        Assert.Equal((1, 1, 0), (answer.GetProperty("Items").GetInt32(), answer.GetProperty("Ratings").GetInt32(), answer.GetProperty("NotHeld").GetInt32()));
        Assert.Null(Assert.Single(_fx.Store.GetModerationLog(0, 10), l => l.RatingId == onRonin.Id).ActorUserId);
        Assert.Equal("ItemIds must list 1 to 1000 item ids.", UserRatingsAdminApi.ItemIdsMessage);
    }

    [Fact]
    public async Task DetachNow_WhileTheDataIsReadOnly_Is503_AndChangesNothing()
    {
        var store = new Mock<IRatingsStore>(MockBehavior.Strict);
        store.SetupGet(s => s.IsReadOnly).Returns(true);
        store.Setup(s => s.ThrowIfReadOnly()).Throws(PluginException.Unavailable("The ratings database is read-only."));
        var journal = new MoveJournalReader(JournalFile, _logger);
        using var consumer = new RatingsLibraryConsumer(_host.Object, store.Object, _library.Object, _users.Object, journal, () => _verifier, () => _detached, _activity.Object, _logger, _fx.Clock);
        await using var host = await StartAdminHostAsync(new UserRatingsAdminApi(consumer, NoUsers, NoOwner));

        Assert.Equal(503, await StatusAsync(host, "admin", Guid.NewGuid(), JsonSerializer.Serialize(new { ItemIds = new[] { Guid.NewGuid() } })));
        await Assert.ThrowsAsync<PluginException>(() => consumer.DetachHeldAsync([Guid.NewGuid()], null, Ct));
        Assert.Empty(_entries);
    }

    // The Detach now routes never reach the users' reconciler.
    private static UserReconciler NoUsers() => throw new InvalidOperationException("The reconciler isn't part of this test.");

    private static RatingsUserDataOwner NoOwner() => throw new InvalidOperationException("The users' owner isn't part of this test.");

    private static async Task<WebHostHarness> StartAdminHostAsync(IUserRatingsAdminApi api, IKitDiagnostics? diagnostics = null)
    {
        var applicationHost = new Mock<IServerApplicationHost>();
        applicationHost.SetupGet(h => h.ApplicationVersion).Returns(JellyfinLine.Server);
        return await new WebHostHarness()
            .WithServices(services =>
            {
                KitAbiGuard.Register(services, applicationHost.Object, UserRatingsKit.Options);
                services.AddSingleton(UserRatingsKit.Options);
                services.AddSingleton(diagnostics ?? Mock.Of<IKitDiagnostics>());
                services.AddSingleton(api);
                ApiFixture.UseJellyfinJson(services);
            })
            .WithMvc(typeof(UserRatingsAdminController))
            .StartAsync();
    }

    private static Task<HttpResponseMessage> PostHeldDetachAsync(WebHostHarness host, string? caller, Guid? userId, string? json)
        => host.SendAsync(HttpMethod.Post, "/UserRatings/Admin/Held/Detach", request =>
        {
            if (caller is not null)
            {
                request.Headers.Add(JellyfinAuth.CallerHeader, caller);
                request.Headers.Add(JellyfinAuth.UserIdHeader, (userId ?? Guid.Empty).ToString("N"));
            }

            if (json is not null)
            {
                request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            }
        });

    private static async Task<int> StatusAsync(WebHostHarness host, string? caller, Guid? userId, string? json)
    {
        using var response = await PostHeldDetachAsync(host, caller, userId, json);
        return (int)response.StatusCode;
    }

    private static async Task<JsonElement> JsonAsync(WebHostHarness host, string caller, Guid userId, string json)
    {
        using var response = await PostHeldDetachAsync(host, caller, userId, json);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private (MusicArtist Artist, MusicAlbum Album) AddArtistAlbum(Folder root, string artistName, string albumName, string? musicBrainzAlbum)
    {
        var artist = _library.Add(new MusicArtist { Name = artistName, Path = Path.Combine(_music, artistName) }, root);
        var album = _library.Add(new MusicAlbum { Name = albumName, Path = Path.Combine(_music, artistName, albumName) }, artist);
        if (musicBrainzAlbum is not null)
        {
            album.ProviderIds["MusicBrainzAlbum"] = musicBrainzAlbum;
        }

        return (artist, album);
    }
}
