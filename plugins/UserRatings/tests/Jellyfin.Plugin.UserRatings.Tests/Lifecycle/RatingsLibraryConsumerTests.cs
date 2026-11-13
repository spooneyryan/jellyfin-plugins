using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Integration;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Services;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Jellyfin.Plugin.Kit.TestSupport;

namespace Jellyfin.Plugin.UserRatings.Tests.Lifecycle;

/// <summary>
/// The lifecycle tests, on the real ratings store, the kit's library-change monitor, verify sweep, detached
/// records and move journal, an in-memory library (<see cref="FakeLibrary"/>) and library folders that are real folders: a
/// mixed rename batch relinked in that batch whatever order its lists arrive in (in the full pass and in the batch after
/// it); a removed music artist folder whose album rating the batch's verify sweep detaches, and restored; a removed series
/// taking its episodes' ratings and given them back; removed then re-added; a library folder unmounted, empty or with the
/// file still on disk keeping every rating attached; the move journal at startup (downtime) and through the hook; the
/// Ambiguous banner with exactly one Activity Log entry per batch; the conflict rule; merged versions; a refresh that
/// rewrites nothing; and the prefilter that resolves only the ratings a batch could match. Not run in parallel with other
/// classes: the store collection's rule, and the hook's target is process-wide.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed partial class RatingsLibraryConsumerTests : IDisposable
{
    private const string JournalId = "3f2a9c1e7b5d4e6f8a0b1c2d3e4f5a6b";
    private static readonly DateTime Created = StoreFixture.T0.AddDays(-30);

    private readonly StoreFixture _fx = new();
    private readonly FakeLibrary _library = new();
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IActivityManager> _activity = new();
    private readonly List<ActivityLog> _entries = [];
    private readonly ListLogger _logger = new();
    private readonly List<string> _folders = [];
    private readonly List<IDisposable> _disposables = [];
    private readonly User _alice = ServicesFixture.NewUser("alice");
    private readonly User _bob = ServicesFixture.NewUser("bob");
    private readonly string _movies;
    private readonly string _movies2;
    private readonly string _shows;
    private readonly string _music;
    private AttachedRecordVerifier _verifier = null!;
    private DetachedRecords _detached = null!;
    private RatingsLibraryConsumer _consumer = null!;
    private int _retentionDays;

    public RatingsLibraryConsumerTests()
    {
        _host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        _users.Setup(u => u.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => new[] { _alice, _bob }.FirstOrDefault(u => u.Id == id));
        _activity.Setup(a => a.CreateAsync(It.IsAny<ActivityLog>())).Callback<ActivityLog>(_entries.Add).Returns(Task.CompletedTask);
        _movies = Folder("Movies");
        _movies2 = Folder("Movies 2");
        _shows = Folder("Shows");
        _music = Folder("Music");
        _library.Mock.Setup(l => l.GetVirtualFolders()).Returns(() => _folders.Select(f => new VirtualFolderInfo { Name = Path.GetFileName(f), Locations = [f] }).ToList());
        Create();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Now => _fx.Clock.GetUtcNow().UtcDateTime;

    private string JournalFile => _fx.Directory.Combine("moves.jsonl");

    public void Dispose()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }

        _fx.Dispose();
    }

    // -------------------------------------------------------------------------------------------- the batch order

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMovieFolderRenamedOnDisk_IsDetachedAndRelinkedThroughTmdb_InTheSameFullPass_WhateverOrderTheEventsCameIn(bool addedFirst)
    {
        var old = AddMovie("Heat (1995)", _movies, "949");
        var rating = await RateAsync(old, _alice, 9, "A great heist.");
        var monitor = Monitor();
        _fx.Clock.Advance(TimeSpan.FromHours(1));

        // The scan deletes the old item and creates the new one (Folder.cs:452-478); the provider ids come with its refresh.
        Movie renamed;
        if (addedFirst)
        {
            renamed = AddMovie("Heat 1995", _movies, null);
            _library.RaiseAdded(renamed);
            _library.Remove(old);
        }
        else
        {
            _library.Remove(old);
            renamed = AddMovie("Heat 1995", _movies, null);
            _library.RaiseAdded(renamed);
        }

        renamed.ProviderIds["Tmdb"] = "949";
        _library.RaiseUpdated(renamed);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var row = _fx.Store.Get(rating.Id)!;
        Assert.Equal((renamed.Id, (DateTime?)null, (string?)null), (row.ItemId, row.DetachedUtc, row.DetachReason));
        Assert.Equal((renamed.Path, Now), (row.Identity.Path, row.AttachedUtc));
        AssertMemberFieldsKept(rating, row);
        Assert.Empty(_entries);

        // Every automatic change is logged with ids; names and paths never reach Information.
        var information = _logger.At(LogLevel.Information).Select(e => e.Message).ToList();
        Assert.Contains(information, m => m.Contains("detached", StringComparison.Ordinal) && m.Contains(old.Id.ToString(), StringComparison.Ordinal) && m.Contains(rating.Id.ToString(), StringComparison.Ordinal));
        Assert.Contains(information, m => m.Contains($"Rating {rating.Id} moved from item {old.Id} to item {renamed.Id}", StringComparison.Ordinal));
        Assert.DoesNotContain(information, m => m.Contains("Heat", StringComparison.Ordinal) || m.Contains(_movies, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheBatchAfterTheFullPass_TakesTheSameOrder_RemovedBeforeAddedAndUpdated()
    {
        // What Jellyfin's own post-scan tasks change comes in the batch right after the scan (AfterFullPass).
        var old = AddMovie("Ronin (1998)", _movies, "8195");
        var rating = await RateAsync(old, _alice);
        var captured = ItemIdentity.Capture(old, _library.Object);
        _library.Remove(old, raiseEvent: false);
        var copy = AddMovie("Ronin", _movies2, "8195");

        await _consumer.HandleAsync(
            Batch(added: [Change(LibraryChangeKind.Added, copy)], updated: [Change(LibraryChangeKind.Updated, copy)], removed: [Change(LibraryChangeKind.Removed, old) with { Identity = captured }], afterFullPass: true),
            Ct);

        Assert.Equal((copy.Id, true), (_fx.Store.Get(rating.Id)!.ItemId, _fx.Store.Get(rating.Id)!.IsAttached));
        var last = Lifecycle("LastBatch");
        Assert.Equal((true, 1, 1), ((bool)last["AfterFullPass"]!, (int)last["Detached"]!, (int)last["Added"]!));
    }

    [Fact]
    public async Task ARemovedMusicArtistFolder_DetachesItsAlbumsRating_ThroughTheBatchsVerifySweep_AndRestoringItReattaches()
    {
        // An album stores no artist id, so only the verify sweep after a removed folder sees it go; and 10.11.5 keeps
        // the album in its item cache, under the artist it no longer has (seen in the sandbox).
        var root = _library.Add(new Folder { Name = "Music", Path = _music });
        var artist = _library.Add(new MusicArtist { Name = "Artist A", Path = Path.Combine(_music, "Artist A") }, root);
        var album = _library.Add(new MusicAlbum { Name = "Album A", Path = Path.Combine(_music, "Artist A", "Album A") }, artist);
        album.ProviderIds["MusicBrainzAlbum"] = "f1e2d3c4-0000-4000-8000-00000000000a";
        _library.Add(new MusicArtist { Name = "Artist B", Path = Path.Combine(_music, "Artist B") }, root);
        var rating = await RateAsync(album, _alice, 8);
        var monitor = Monitor();

        _library.Remove(artist, cacheKeepsDescendants: true);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var row = _fx.Store.Get(rating.Id)!;
        Assert.Equal((album.Id, RatingValues.DetachReason.Missing, Now), (row.ItemId, row.DetachReason, row.DetachedUtc));
        var last = Lifecycle("LastBatch");
        Assert.Equal((0, 1, 1), ((int)last["Removed"]!, (int)last["RemovedFolders"]!, (int)last["Swept"]!));

        // Restored on disk: the same paths give the same ids; the album comes back in the next scan.
        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        _library.Add(artist, root);
        _library.Add(album, artist);
        _library.RaiseAdded(artist);
        _library.RaiseAdded(album);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        row = _fx.Store.Get(rating.Id)!;
        Assert.Equal((album.Id, true, Now), (row.ItemId, row.IsAttached, row.AttachedUtc));
        AssertMemberFieldsKept(rating, row);
    }

    [Fact]
    public async Task AnArtistByName_NeverReachesTheConsumer_WhichDoesNotListMusicArtist()
    {
        // Jellyfin's artists post-scan task deletes an artist by name (no parent): only a consumer whose kinds list MusicArtist
        // gets it in Removed, and User Ratings' doesn't.
        Assert.DoesNotContain(BaseItemKind.MusicArtist, _consumer.Filter.Kinds!);
        Assert.Contains(BaseItemKind.MusicAlbum, _consumer.Filter.Kinds!);
        var byName = _library.Add(new MusicArtist { Name = "Artist C" });
        var monitor = Monitor();

        _library.Remove(byName);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var last = Lifecycle("LastBatch");
        Assert.Equal((0, 0), ((int)last["Removed"]!, (int)last["RemovedFolders"]!));
    }

    [Fact]
    public async Task ARemovedSeries_DetachesItsEpisodesAndItsOwnRatings_AndRestoringTheSeriesReattachesThem()
    {
        var series = _library.AddSeries("Test Series", Path.Combine(_shows, "Test Series"), ("Tvdb", "900401"));
        var season = _library.AddSeason(series, 1);
        var episode = _library.AddEpisode(series, season, 1);
        var onEpisode = await RateAsync(episode, _alice, 7);
        var onSeries = await RateAsync(series, _bob, 6);

        // Another rated series stays, so this isn't every rated item of the folder at once (the mass-detach guard's case).
        await RateAsync(_library.AddSeries("Other Series", Path.Combine(_shows, "Other Series"), ("Tvdb", "900402")), _bob, 5);
        var monitor = Monitor();

        _library.Remove(series);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.Equal(RatingValues.DetachReason.Removed, _fx.Store.Get(onEpisode.Id)!.DetachReason);
        Assert.Equal(RatingValues.DetachReason.Removed, _fx.Store.Get(onSeries.Id)!.DetachReason);

        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        _library.Add(series);
        _library.Add(season, series);
        _library.Add(episode, season);
        _library.RaiseAdded(series);
        _library.RaiseAdded(season);
        _library.RaiseAdded(episode);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.True(_fx.Store.Get(onEpisode.Id)!.IsAttached);
        Assert.True(_fx.Store.Get(onSeries.Id)!.IsAttached);
        Assert.Equal(series.Id, _fx.Store.Get(onEpisode.Id)!.Identity.SeriesId);
    }

    [Fact]
    public async Task RemovedThenReAdded_InTwoBatches_Reattaches_AndInOneBatch_NeverDetaches()
    {
        var movie = AddMovie("Heat (1995)", _movies, "949");
        var rating = await RateAsync(movie, _alice, 9, "A great heist.");
        var monitor = Monitor();

        _library.Remove(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        Assert.Equal(RatingValues.DetachReason.Removed, _fx.Store.Get(rating.Id)!.DetachReason);

        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        _library.Add(movie);
        _library.RaiseAdded(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        var back = _fx.Store.Get(rating.Id)!;
        Assert.Equal((movie.Id, true, Now), (back.ItemId, back.IsAttached, back.AttachedUtc));
        AssertMemberFieldsKept(rating, back);

        // Removed and created again within one scan: the item is there when the batch is handled, so nothing is written.
        var generation = _fx.Store.Generation;
        _library.Remove(movie);
        _library.Add(movie);
        _library.RaiseAdded(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        Assert.True(_fx.Store.Get(rating.Id)!.IsAttached);
        Assert.Equal(generation, _fx.Store.Generation);
    }

    // ------------------------------------------------------------------ a library briefly unavailable (the data safety)

    [Fact]
    public async Task AnUnmountedLibraryFolder_KeepsTheRatingsAttached_ThroughTheRemovalAndTheSweep_AndTheyShowAgainWhenItIsBack()
    {
        var movie = AddMovie("Heat (1995)", _movies, "949");
        var rating = await RateAsync(movie, _alice, 9, "A great heist.");
        var monitor = Monitor();
        var generation = _fx.Store.Generation;

        // The drive is unmounted, and the item goes anyway (a listing that failed part way, a clean-up task).
        Directory.Delete(_movies, recursive: true);
        _library.Remove(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        await _verifier.SweepAsync(VerifySweepReason.Daily, Ct);

        Assert.True(_fx.Store.Get(rating.Id)!.IsAttached);
        Assert.Equal(generation, _fx.Store.Generation);
        var held = Assert.Single(Held());
        Assert.Equal((movie.Id, LibraryAvailability.FolderUnavailable), ((Guid)held["ItemId"]!, (string)held["Reason"]!));
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains($"ratings on item {movie.Id} stay attached although the item is gone: {LibraryAvailability.FolderUnavailable} ({rating.Id})", StringComparison.Ordinal));

        // Mounted again: the scan creates the item at its path, which gives it its id again.
        Folder("Movies");
        _library.Add(movie);
        _library.RaiseAdded(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.Equal((movie.Id, true), (_fx.Store.Get(rating.Id)!.ItemId, _fx.Store.Get(rating.Id)!.IsAttached));
        Assert.Empty(Held());
        Assert.Equal(generation, _fx.Store.Generation);
    }

    [Fact]
    public async Task AFolderBackWithoutTheItem_LetsTheSweepDetachIt_AsMissing()
    {
        var movie = AddMovie("Heat (1995)", _movies, "949");
        var rating = await RateAsync(movie, _alice);
        Directory.Delete(_movies, recursive: true);
        _library.Remove(movie, raiseEvent: false);
        await _verifier.SweepAsync(VerifySweepReason.Startup, Ct);
        Assert.True(_fx.Store.Get(rating.Id)!.IsAttached);
        Assert.Single(Held());

        Folder("Movies");
        _fx.Clock.Advance(TimeSpan.FromDays(1));
        await _verifier.SweepAsync(VerifySweepReason.Daily, Ct);

        Assert.Equal((RatingValues.DetachReason.Missing, Now), (_fx.Store.Get(rating.Id)!.DetachReason, _fx.Store.Get(rating.Id)!.DetachedUtc));
        Assert.Empty(Held());
    }

    [Fact]
    public async Task AnEmptyMountPoint_KeepsTheRatingsAttached()
    {
        var movie = AddMovie("Heat (1995)", _movies, "949");
        var rating = await RateAsync(movie, _alice);
        var monitor = Monitor();

        // The drive's mount point is there with nothing in it (core skips such a library folder; this doesn't rely on it).
        File.Delete(Path.Combine(_movies, "placeholder.txt"));
        _library.Remove(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        await _verifier.SweepAsync(VerifySweepReason.Daily, Ct);

        Assert.True(_fx.Store.Get(rating.Id)!.IsAttached);
        Assert.Equal(LibraryAvailability.FolderUnavailable, (string)Assert.Single(Held())["Reason"]!);
    }

    [Fact]
    public async Task AFileStillOnDisk_KeepsItsRatingAttached_AndAnItemBackAtThatPathUnderAnotherId_TakesIt()
    {
        var movie = AddMovie("Heat (1995)", _movies, null);
        Directory.CreateDirectory(Path.GetDirectoryName(movie.Path)!);
        await File.WriteAllTextAsync(movie.Path, "synthetic", Ct);
        var rating = await RateAsync(movie, _alice, 9, "A great heist.");
        var monitor = Monitor();

        // Jellyfin dropped an item whose file is still there (a folder it didn't recognise): kept, held as StillOnDisk.
        _library.Remove(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        await _verifier.SweepAsync(VerifySweepReason.Daily, Ct);
        Assert.True(_fx.Store.Get(rating.Id)!.IsAttached);
        Assert.Equal(LibraryAvailability.StillOnDisk, (string)Assert.Single(Held())["Reason"]!);

        // The file comes back under another id (no provider ids, so only the path can match it): an Exact match by path.
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        var again = _library.Add(new Movie { Name = "Heat", ProductionYear = 1995, Path = movie.Path, RunTimeTicks = movie.RunTimeTicks });
        _library.RaiseAdded(again);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var row = _fx.Store.Get(rating.Id)!;
        Assert.Equal((again.Id, true, Now), (row.ItemId, row.IsAttached, row.AttachedUtc));
        AssertMemberFieldsKept(rating, row);
        Assert.Empty(Held());
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains($"Rating {rating.Id} moved from item {movie.Id} to item {again.Id} (Path: Exact, Path)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoLibraryFolderKnown_KeepsEveryRatingAttached()
    {
        var movie = AddMovie("Heat (1995)", _movies, "949");
        var rating = await RateAsync(movie, _alice);
        _folders.Clear();
        _library.Remove(movie, raiseEvent: false);

        await _verifier.SweepAsync(VerifySweepReason.Daily, Ct);

        Assert.True(_fx.Store.Get(rating.Id)!.IsAttached);
        Assert.Equal(LibraryAvailability.NoLibraryFolders, (string)Assert.Single(Held())["Reason"]!);
    }

    [Fact]
    public async Task APathInNoLibraryFolder_DetachesOnlyWhileEveryLibraryFolderIsAvailable()
    {
        var movie = AddMovie("Heat (1995)", _fx.Directory.Combine("taken out of the library"), "949");
        var rating = await RateAsync(movie, _alice);
        _library.Remove(movie, raiseEvent: false);
        Directory.Delete(_music, recursive: true);

        await _verifier.SweepAsync(VerifySweepReason.Daily, Ct);
        Assert.True(_fx.Store.Get(rating.Id)!.IsAttached);

        Folder("Music");
        await _verifier.SweepAsync(VerifySweepReason.Daily, Ct);
        Assert.Equal(RatingValues.DetachReason.Missing, _fx.Store.Get(rating.Id)!.DetachReason);
    }

    // ------------------------------------------------------------------------------------------------ the move journal

    [Fact]
    public async Task AtStartup_TheJournalMovesAttachedAndDetachedRatings_AndContainerIds_ButNotARatingMadeAfterTheLine()
    {
        // Moves made by Library Maintenance while the server was off (catch-up after downtime).
        var oldMovie = Guid.NewGuid();
        var newMovie = AddMovie("Heat (1995)", _movies2, "949");
        var oldRonin = Guid.NewGuid();
        var newRonin = AddMovie("Ronin (1998)", _movies2, "8195");
        var oldSeries = Guid.NewGuid();
        var newSeries = Guid.NewGuid();
        var reused = Guid.NewGuid();
        var attached = await UpsertAsync(Rating(_alice, oldMovie, "Movie"));
        var detached = await UpsertAsync(Rating(_bob, oldRonin, "Movie") with { DetachedUtc = StoreFixture.T0.AddDays(-3), DetachReason = RatingValues.DetachReason.Removed });
        var episode = await UpsertAsync(Rating(_alice, Guid.NewGuid(), "Episode") with { Identity = new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Episode", Name = "Episode 1", SeriesId = oldSeries } });
        var late = await UpsertAsync(Rating(_bob, reused, "Movie") with { CreatedUtc = StoreFixture.T0, UpdatedUtc = StoreFixture.T0 });
        WriteJournal(
            Line(1, oldMovie, newMovie.Id, StoreFixture.T0.AddDays(-2)),
            Line(2, oldRonin, newRonin.Id, StoreFixture.T0.AddDays(-2)),
            Line(3, oldSeries, newSeries, StoreFixture.T0.AddDays(-2), "Series"),
            Line(4, reused, Guid.NewGuid(), StoreFixture.T0.AddDays(-1)));

        await _consumer.StartAsync(Ct);
        await WaitUntilAsync(() => (bool)_consumer.Describe()["StartupDone"]!);

        Assert.Equal((newMovie.Id, true, StoreFixture.T0.AddDays(-2)), (_fx.Store.Get(attached.Id)!.ItemId, _fx.Store.Get(attached.Id)!.IsAttached, _fx.Store.Get(attached.Id)!.AttachedUtc));
        Assert.Equal((newRonin.Id, true), (_fx.Store.Get(detached.Id)!.ItemId, _fx.Store.Get(detached.Id)!.IsAttached));
        Assert.Equal((episode.ItemId, newSeries), (_fx.Store.Get(episode.Id)!.ItemId, _fx.Store.Get(episode.Id)!.Identity.SeriesId));
        Assert.Equal(reused, _fx.Store.Get(late.Id)!.ItemId);
        AssertMemberFieldsKept(attached, _fx.Store.Get(attached.Id)!);
        Assert.Equal((JournalId, "4"), (_fx.Store.GetMeta(RatingsLibraryConsumer.MoveJournalIdKey), _fx.Store.GetMeta(RatingsLibraryConsumer.LastMoveSeqKey)));

        // A second catch-up has nothing to do: no write.
        var generation = _fx.Store.Generation;
        await _consumer.CatchUpAsync(Ct);
        Assert.Equal(generation, _fx.Store.Generation);
        await _consumer.StopAsync(Ct);
    }

    [Fact]
    public async Task TheHook_CatchesUpWhileTheServiceRuns_AndDoesNothingOnceItStopped()
    {
        ItemMovedHookV1.OnItemsMoved("[]");
        var a = Guid.NewGuid();
        var x = Guid.NewGuid();
        var rating = await UpsertAsync(Rating(_alice, a, "Movie"));
        await _consumer.StartAsync(Ct);
        await WaitUntilAsync(() => (bool)_consumer.Describe()["StartupDone"]!);
        WriteJournal(Line(1, a, x, StoreFixture.T0.AddDays(-1)));
        var calls = ItemMovedHookV1.Calls;

        ItemMovedHookV1.OnItemsMoved("""[{"OldId":"not read"}]""");

        await WaitUntilAsync(() => _fx.Store.Get(rating.Id)!.ItemId == x);
        Assert.Equal(calls + 1, ItemMovedHookV1.Calls);
        await _consumer.StopAsync(Ct);

        WriteJournal(Line(1, a, x, StoreFixture.T0.AddDays(-1)), Line(2, x, a, StoreFixture.T0.AddHours(-1)));
        ItemMovedHookV1.OnItemsMoved(null!);
        await Task.Delay(100, Ct);
        Assert.Equal(x, _fx.Store.Get(rating.Id)!.ItemId);
    }

    [Fact]
    public async Task AJournalMoveOntoAnItemTheMemberRatedSince_KeepsTheNewerRating()
    {
        var a = Guid.NewGuid();
        var x = AddMovie("Heat (1995)", _movies, "949");
        var older = await UpsertAsync(Rating(_alice, a, "Movie"));
        var newer = await RateAsync(x, _alice, 4, updated: Created.AddDays(1));
        WriteJournal(Line(1, a, x.Id, StoreFixture.T0.AddDays(-1)));

        await _consumer.CatchUpAsync(Ct);

        Assert.Equal((a, RatingValues.DetachReason.Ambiguous), (_fx.Store.Get(older.Id)!.ItemId, _fx.Store.Get(older.Id)!.DetachReason));
        Assert.True(_fx.Store.Get(newer.Id)!.IsAttached);
        Assert.Equal(RatingsLibraryConsumer.AmbiguousActivityName(1), Assert.Single(_entries).Name);
    }

    // ---------------------------------------------------------------------------------------------- Ambiguous

    [Fact]
    public async Task TwoCopies_LeaveTheRatingsAmbiguous_WithTheBanner_AndOneActivityEntryForTheBatch_UntilTheAdminLooks()
    {
        var old = AddMovie("Heat (1995)", _movies, "949");
        var onAlice = await RateAsync(old, _alice, 9);
        var onBob = await RateAsync(old, _bob, 6);
        var monitor = Monitor();

        _library.Remove(old);
        var copy1 = AddMovie("Heat", _movies, "949");
        var copy2 = AddMovie("Heat", _movies2, "949");
        _library.RaiseAdded(copy1);
        _library.RaiseAdded(copy2);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.All(new[] { onAlice, onBob }, r => Assert.Equal((old.Id, RatingValues.DetachReason.Ambiguous), (_fx.Store.Get(r.Id)!.ItemId, _fx.Store.Get(r.Id)!.DetachReason)));
        var entry = Assert.Single(_entries);
        Assert.Equal(("User Ratings: 2 ratings need a manual relink", RatingsLibraryConsumer.ActivityType, Guid.Empty, LogLevel.Warning), (entry.Name, entry.Type, entry.UserId, entry.LogSeverity));
        var ambiguous = Lifecycle("Ambiguous");
        Assert.Equal((2, true, RatingsLibraryConsumer.AmbiguousBanner(2)), ((int)ambiguous["Count"]!, (bool)ambiguous["Banner"]!, (string?)ambiguous["Message"]));
        Assert.Equal("2 ratings could not be relinked automatically because several copies match or another title is at their place. Review them in Data → Detached & ambiguous.", ambiguous["Message"]);
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains($"Rating {onAlice.Id} (item {old.Id}) needs a manual relink: SeveralStrongCandidates", StringComparison.Ordinal));

        // The admin opens Data → Detached & ambiguous: the banner ends. Seen again in a later batch, nothing is new.
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        await _consumer.MarkAmbiguousSeenAsync(Ct);
        Assert.False((bool)Lifecycle("Ambiguous")["Banner"]!);
        _library.RaiseUpdated(copy1);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        await _detached.ResolveDetachedAsync(_consumer, Ct);
        Assert.Single(_entries);
        Assert.False((bool)Lifecycle("Ambiguous")["Banner"]!);
        Assert.Equal(2, (int)Lifecycle("Detached")[RatingValues.DetachReason.Ambiguous]!);
    }

    [Fact]
    public async Task AmbiguousRowsFromTwoStepsOfOneBatch_GetOneActivityEntry_WithTheirTotal()
    {
        // Step 3 (a merged version onto a copy bob rated too) and step 4 (two copies of alice's movie) in the same scan.
        var primary = AddMovie("Ronin (1998)", _movies, "8195");
        var other = AddMovie("Ronin (1998) - 4K", _movies, "8195");
        await RateAsync(primary, _bob, 4, updated: Created.AddHours(1));
        await RateAsync(other, _bob, 8, updated: Created.AddHours(2));
        var old = AddMovie("Heat (1995)", _movies, "949");
        var onAlice = await RateAsync(old, _alice, 9);
        var monitor = Monitor();

        other.PrimaryVersionId = JellyfinLine.VersionId(primary.Id);
        _library.RaiseUpdated(other, ItemUpdateType.MetadataEdit);
        _library.Remove(old);
        _library.RaiseAdded(AddMovie("Heat", _movies, "949"));
        _library.RaiseAdded(AddMovie("Heat", _movies2, "949"));
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.Equal(RatingValues.DetachReason.Ambiguous, _fx.Store.Get(onAlice.Id)!.DetachReason);
        Assert.Equal(RatingsLibraryConsumer.AmbiguousActivityName(2), Assert.Single(_entries).Name);
        Assert.Equal(2, (int)Lifecycle("LastBatch")["Ambiguous"]!);
    }

    [Fact]
    public async Task TheDailyResolve_OutsideABatch_WritesItsOwnActivityEntry()
    {
        var old = Guid.NewGuid();
        AddMovie("Heat", _movies, "949");
        AddMovie("Heat", _movies2, "949");
        await UpsertAsync(Rating(_alice, old, "Movie") with
        {
            Identity = new IdentitySnapshot { ItemId = old, Kind = "Movie", Name = "Heat", ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "949" } },
            DetachedUtc = StoreFixture.T0.AddDays(-1),
            DetachReason = RatingValues.DetachReason.Missing
        });

        await _detached.ResolveDetachedAsync(_consumer, Ct);

        Assert.Equal(RatingsLibraryConsumer.AmbiguousActivityName(1), Assert.Single(_entries).Name);
        Assert.Equal("User Ratings: 1 rating needs a manual relink", RatingsLibraryConsumer.AmbiguousActivityName(1));
        Assert.True((bool)Lifecycle("Ambiguous")["Banner"]!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARelinkOntoAnItemTheMemberAlreadyRated_KeepsTheNewerRating_AndDetachesTheOlderAsAmbiguous(bool movingIsNewer)
    {
        var old = AddMovie("Heat (1995)", _movies, "949");
        var copy = AddMovie("Heat", _movies2, "949");
        var moving = await RateAsync(old, _alice, 9, updated: Created.AddHours(movingIsNewer ? 2 : 1));
        var there = await RateAsync(copy, _alice, 3, updated: Created.AddHours(movingIsNewer ? 1 : 2));
        var monitor = Monitor();

        _library.Remove(old);
        _library.RaiseUpdated(copy);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var (winner, loser) = movingIsNewer ? (moving, there) : (there, moving);
        Assert.Equal((copy.Id, true), (_fx.Store.Get(winner.Id)!.ItemId, _fx.Store.Get(winner.Id)!.IsAttached));
        Assert.Equal(RatingValues.DetachReason.Ambiguous, _fx.Store.Get(loser.Id)!.DetachReason);
        AssertMemberFieldsKept(winner, _fx.Store.Get(winner.Id)!);
        Assert.Equal(RatingsLibraryConsumer.AmbiguousActivityName(1), Assert.Single(_entries).Name);
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains($"Rating {loser.Id} was detached as Ambiguous: its member's newer rating {winner.Id} is on item {copy.Id}", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------- updated items

    [Fact]
    public async Task AnItemMergedIntoAnotherVersion_TakesItsRatingsToThePrimary_LoggedAsRelink_UnderTheConflictRule()
    {
        var primary = AddMovie("Heat (1995)", _movies, "949");
        var other = AddMovie("Heat (1995) - 4K", _movies, "949");
        var alice = await RateAsync(other, _alice, 9);
        var bobOnPrimary = await RateAsync(primary, _bob, 4, updated: Created.AddHours(1));
        var bobOnOther = await RateAsync(other, _bob, 8, updated: Created.AddHours(2));
        var monitor = Monitor();

        other.PrimaryVersionId = JellyfinLine.VersionId(primary.Id);
        _library.RaiseUpdated(other, ItemUpdateType.MetadataEdit);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.Equal((primary.Id, true), (_fx.Store.Get(alice.Id)!.ItemId, _fx.Store.Get(alice.Id)!.IsAttached));
        Assert.Equal((primary.Id, true), (_fx.Store.Get(bobOnOther.Id)!.ItemId, _fx.Store.Get(bobOnOther.Id)!.IsAttached));
        Assert.Equal(RatingValues.DetachReason.Ambiguous, _fx.Store.Get(bobOnPrimary.Id)!.DetachReason);
        var log = _fx.Store.GetModerationLog(0, 10).Where(l => l.Action == RatingValues.ModerationAction.Relink).ToList();
        Assert.Equal(2, log.Count);
        Assert.All(log, l => Assert.Equal((RatingsLibraryConsumer.MergedVersionReason, primary.Id, (Guid?)null), (l.Reason, l.ItemId!.Value, l.ActorUserId)));
        Assert.Contains(log, l => l.RatingId == alice.Id && l.TargetUserId == _alice.Id && l.Details == "{\"FromItemId\":\"" + other.Id.ToString("N") + "\"}");

        // Stable: the next batch moves nothing back to the hidden version and creates no new Ambiguous row.
        _library.RaiseUpdated(other);
        _library.RaiseUpdated(primary);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        Assert.Equal(primary.Id, _fx.Store.Get(alice.Id)!.ItemId);
        Assert.Equal(RatingValues.DetachReason.Ambiguous, _fx.Store.Get(bobOnPrimary.Id)!.DetachReason);
        Assert.Single(_entries);
    }

    [Fact]
    public async Task AMetadataRefresh_WithNothingNew_WritesNothing_AndNewProviderIdsAreTaken_KeepingTheMembersFields()
    {
        var movie = AddMovie("Heat (1995)", _movies, "949");
        var rating = await RateAsync(movie, _alice, 9, "A great heist.");
        var series = _library.AddSeries("Test Series", Path.Combine(_shows, "Test Series"), ("Tvdb", "900401"));
        var season = _library.AddSeason(series, 1);
        var episode = _library.AddEpisode(series, season, 1);
        var onEpisode = await RateAsync(episode, _bob);
        var monitor = Monitor();
        var generation = _fx.Store.Generation;

        // The runtime has no column: compared as stored, a refresh with the same metadata changes nothing.
        _library.RaiseUpdated(movie);
        _library.RaiseUpdated(episode);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        Assert.Equal(generation, _fx.Store.Generation);

        movie.ProviderIds["Imdb"] = "tt0113277";
        series.ProviderIds["Tmdb"] = "1396";
        _library.RaiseUpdated(movie);
        _library.RaiseUpdated(series);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var row = _fx.Store.Get(rating.Id)!;
        Assert.Equal("tt0113277", row.Identity.ProviderIds["Imdb"]);
        Assert.Contains("movie|imdb|tt0113277", _fx.Store.GetKeys(rating.Id), StringComparer.OrdinalIgnoreCase);
        AssertMemberFieldsKept(rating, row);
        Assert.Equal(Created, row.AttachedSinceUtc);
        Assert.Equal("1396", _fx.Store.Get(onEpisode.Id)!.Identity.SeriesProviderIds!["Tmdb"]);
        Assert.Equal(2, (int)Lifecycle("LastBatch")["Refreshed"]!);
    }

    [Fact]
    public async Task ABatch_ResolvesOnlyTheDetachedRatingsItsItemsCouldMatch()
    {
        // The batch's keys, paths, ids and the journal pick the candidates first; the others aren't resolved.
        for (var i = 0; i < 30; i++)
        {
            await UpsertAsync(Rating(_bob, Guid.NewGuid(), "Movie") with
            {
                Identity = new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie", Name = "Other", ProviderIds = new Dictionary<string, string> { ["Tmdb"] = (10000 + i).ToString(CultureInfo.InvariantCulture) } },
                DetachedUtc = StoreFixture.T0.AddDays(-1),
                DetachReason = RatingValues.DetachReason.Removed
            });
        }

        var old = Guid.NewGuid();
        var matching = await UpsertAsync(Rating(_alice, old, "Movie") with
        {
            Identity = new IdentitySnapshot { ItemId = old, Kind = "Movie", Name = "Heat", ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "949" } },
            DetachedUtc = StoreFixture.T0.AddDays(-1),
            DetachReason = RatingValues.DetachReason.Removed
        });
        var monitor = Monitor();
        var movie = AddMovie("Heat (1995)", _movies, "949");
        _library.RaiseAdded(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.Equal(movie.Id, _fx.Store.Get(matching.Id)!.ItemId);
        var resolve = Assert.IsAssignableFrom<IDictionary<string, object?>>(Lifecycle("LastBatch")["Resolve"]);
        Assert.Equal((1, 1), ((int)resolve["Checked"]!, (int)resolve["Relinked"]!));
    }

    [Fact]
    public async Task ReadOnlyData_IsLeftAlone()
    {
        var store = new Mock<IRatingsStore>(MockBehavior.Strict);
        store.SetupGet(s => s.IsReadOnly).Returns(true);
        var journal = new MoveJournalReader(JournalFile, _logger);
        using var consumer = new RatingsLibraryConsumer(_host.Object, store.Object, _library.Object, _users.Object, journal, () => _verifier, () => _detached, _activity.Object, _logger, _fx.Clock);

        await consumer.HandleAsync(Batch(removed: [new ItemChange { Kind = LibraryChangeKind.Removed, ItemId = Guid.NewGuid() }]), Ct);
        Assert.Equal(0, await consumer.DetachAsync([Guid.NewGuid()], Now, Ct));
        Assert.Empty(consumer.GetDetached());
        Assert.Empty(consumer.GetAttachedItemIds());
        Assert.Equal("ReadOnly", (await consumer.CatchUpAsync(Ct))["Skipped"]);
        Assert.Equal(0, await consumer.ApplyResolutionsAsync([], Ct));
    }

    private static void AssertMemberFieldsKept(RatingRecord before, RatingRecord after)
        => Assert.Equal(
            (before.Id, before.UserId, before.Stars2, before.ReviewText, before.IsSpoiler, before.CreatedUtc, before.UpdatedUtc, before.ReviewUpdatedUtc, before.EditCount, before.Origin),
            (after.Id, after.UserId, after.Stars2, after.ReviewText, after.IsSpoiler, after.CreatedUtc, after.UpdatedUtc, after.ReviewUpdatedUtc, after.EditCount, after.Origin));

    private static ItemChange Change(LibraryChangeKind kind, BaseItem item)
        => new() { Kind = kind, ItemId = item.Id, ItemKind = item.GetBaseItemKind(), IsFolder = item.IsFolder, Utc = DateTimeOffset.UtcNow };

    private static LibraryChangeBatch Batch(
        IReadOnlyList<ItemChange>? added = null,
        IReadOnlyList<ItemChange>? updated = null,
        IReadOnlyList<ItemChange>? removed = null,
        IReadOnlyList<ItemChange>? removedFolders = null,
        bool afterFullPass = false)
        => new(added ?? [], updated ?? [], removed ?? [], removedFolders ?? [], [], fullPass: false, duringPostScan: false, afterFullPass);

    private static string Line(long seq, Guid oldId, Guid newId, DateTime utc, string kind = "Movie")
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["V"] = 1,
            ["JournalId"] = JournalId,
            ["Seq"] = seq,
            ["Status"] = "Committed",
            ["Utc"] = utc.ToString("O", CultureInfo.InvariantCulture),
            ["OldId"] = oldId.ToString("N", CultureInfo.InvariantCulture),
            ["NewId"] = newId.ToString("N", CultureInfo.InvariantCulture),
            ["Kind"] = kind
        });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(20, Ct);
        }
    }

    private RatingRecord Rating(User user, Guid itemId, string kind) => new()
    {
        Id = Guid.NewGuid(),
        UserId = user.Id,
        Identity = new IdentitySnapshot { ItemId = itemId, Kind = kind, Name = "Item" },
        Stars2 = 7,
        ReviewText = "Synthetic review.",
        ReviewUpdatedUtc = Created,
        CreatedUtc = Created,
        UpdatedUtc = Created,
        EditCount = 1
    };

    private async Task<RatingRecord> RateAsync(BaseItem item, User user, int stars2 = 8, string? text = null, DateTime? updated = null)
        => await UpsertAsync(new RatingRecord
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Identity = RatingsLibraryConsumer.Normalize(ItemIdentity.Capture(item, _library.Object)),
            Stars2 = stars2,
            ReviewText = text,
            ReviewUpdatedUtc = text is null ? null : Created,
            CreatedUtc = Created,
            UpdatedUtc = updated ?? Created,
            EditCount = 2
        });

    private async Task<RatingRecord> UpsertAsync(RatingRecord record)
    {
        await _fx.Store.UpsertAsync(record, Ct);
        return _fx.Store.Get(record.Id)!;
    }

    private Movie AddMovie(string folderName, string libraryFolder, string? tmdb)
    {
        var movie = _library.AddMovie(folderName, 1995, TimeSpan.FromMinutes(170), Path.Combine(libraryFolder, folderName, folderName + ".mkv"));
        if (tmdb is not null)
        {
            movie.ProviderIds["Tmdb"] = tmdb;
        }

        return movie;
    }

    private string Folder(string name)
    {
        var path = _fx.Directory.Combine("media", name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "placeholder.txt"), "synthetic");
        if (!_folders.Contains(path))
        {
            _folders.Add(path);
        }

        return path;
    }

    private IDictionary<string, object?> Lifecycle(string key) => Assert.IsAssignableFrom<IDictionary<string, object?>>(_consumer.Describe()[key]);

    private List<IDictionary<string, object?>> Held() => Assert.IsAssignableFrom<IEnumerable<IDictionary<string, object?>>>(_consumer.Describe()["Held"]).ToList();

    private void WriteJournal(params string[] lines)
        => File.WriteAllLines(JournalFile, [$$"""{"V":1,"Header":true,"JournalId":"{{JournalId}}","CreatedUtc":"2026-09-01T00:00:00Z"}""", .. lines]);

    private LibraryChangeMonitor Monitor()
    {
        var monitor = new LibraryChangeMonitor(_host.Object, _library.Object, null, () => [_consumer], UserRatingsKit.Options, NullLoggerFactory.Instance, _fx.Clock);
        _disposables.Add(monitor);
        monitor.Subscribe();
        return monitor;
    }

    private void Create()
    {
        var journal = new MoveJournalReader(JournalFile, _logger);
        var resolver = new IdentityResolver(_library.Object, journal, _fx.Clock);
        _verifier = new AttachedRecordVerifier(_host.Object, _library.Object, journal, () => [_consumer], _logger, _fx.Clock);
        _detached = new DetachedRecords(
            _fx.Directory.Combine("userratings", "archive"),
            _fx.Snapshots,
            _library.Object,
            id => _users.Object.GetUserById(id),
            resolver,
            journal,
            () => [_consumer],
            _logger,
            _fx.Clock);
        _consumer = new RatingsLibraryConsumer(_host.Object, _fx.Store, _library.Object, _users.Object, journal, () => _verifier, () => _detached, _activity.Object, _logger, _fx.Clock, () => _retentionDays);
        _disposables.Add(_consumer);
        _disposables.Add(_verifier);
    }
}
