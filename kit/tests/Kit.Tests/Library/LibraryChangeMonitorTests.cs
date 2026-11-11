using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// <see cref="LibraryChangeMonitor"/> with a fake clock: the filter defaults, the removed item's
/// identity captured in the handler, <c>RemovedFolders</c>, the opt-in user data, the debounce and the maximum wait, the
/// deferral while <c>IsScanRunning</c>, <b>the post-scan flush with <c>IsScanRunning</c> still true</b>, which
/// dispatches synchronously with <c>FullPass</c> and <c>DuringPostScan</c> and is never deferred, and the changes Jellyfin's
/// own post-scan tasks make after it (they run after the plugins'), sent with <c>AfterFullPass</c> as soon as the scan ends.
/// </summary>
public sealed class LibraryChangeMonitorTests : IDisposable
{
    private static readonly Guid User1 = new("11111111-0000-0000-0000-000000000001");
    private static readonly Guid User2 = new("22222222-0000-0000-0000-000000000002");

    private readonly FakeLibrary _library = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly Mock<ILoggerFactory> _loggerFactory = new();
    private readonly ManualTimerProvider _clock = new();
    private readonly ListLogger _log = new();
    private readonly List<LibraryChangeMonitor> _monitors = [];
    private bool _started = true;

    public LibraryChangeMonitorTests()
    {
        _host.SetupGet(h => h.CoreStartupHasCompleted).Returns(() => _started);
        _loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(_log);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var monitor in _monitors)
        {
            monitor.Dispose();
        }
    }

    [Fact]
    public async Task TheHandlers_TakeLibraryItemsOfTheConsumersKinds_AndLeaveOutWhatItIgnores()
    {
        var ownBoxSet = _library.Add(new BoxSet { Name = "Auto: best of 1999" });
        var otherBoxSet = _library.Add(new BoxSet { Name = "Trilogy" });
        var consumer = new RecordingConsumer("Movies", new LibraryChangeFilter
        {
            Kinds = new HashSet<BaseItemKind> { BaseItemKind.Movie, BaseItemKind.BoxSet },
            Ignore = (_, item) => item.Id == ownBoxSet.Id,
        });
        var monitor = Subscribed(consumer);
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        var series = _library.AddSeries("Test Series");
        var channelItem = _library.Add(new Movie { Name = "A channel's film", ChannelId = Guid.NewGuid() });

        foreach (var item in new BaseItem[] { movie, series, channelItem, ownBoxSet, otherBoxSet })
        {
            _library.RaiseAdded(item);
        }

        await monitor.FlushAsync(fullPass: false, duringPostScan: false, null, Ct);

        var batch = Assert.Single(consumer.Batches);
        Assert.Equal([movie.Id, otherBoxSet.Id], batch.Added.Select(c => c.ItemId));
        Assert.Equal([BaseItemKind.Movie, BaseItemKind.BoxSet], batch.Added.Select(c => c.ItemKind!.Value));
        Assert.Equal([false, true], batch.Added.Select(c => c.IsFolder));
        Assert.Equal([BaseItemKind.BoxSet, BaseItemKind.Movie], batch.Kinds.Order());
        Assert.False(batch.FullPass);
    }

    [Fact]
    public async Task ARemovedFolder_ReachesRemovedFolders_WhateverTheKinds_AndItsDescendantsRaiseNothing()
    {
        var series = _library.AddSeries("Test Series", null, ("Tvdb", "900401"));
        var season = _library.AddSeason(series, 1);
        _library.AddEpisode(series, season, 1);
        var episodesOnly = new RecordingConsumer("Episodes", new LibraryChangeFilter { Kinds = new HashSet<BaseItemKind> { BaseItemKind.Episode } });
        var everything = new RecordingConsumer("Everything");
        var monitor = Subscribed(episodesOnly, everything);

        _library.Remove(series);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var episodes = Assert.Single(episodesOnly.Batches);
        Assert.Empty(episodes.Removed);
        Assert.Equal(series.Id, Assert.Single(episodes.RemovedFolders).ItemId);

        // One event, for the series only (LibraryManager.cs:409-466): its season and episode are gone without one.
        var all = Assert.Single(everything.Batches);
        Assert.Equal(series.Id, Assert.Single(all.Removed).ItemId);
        Assert.Equal(series.Id, Assert.Single(all.RemovedFolders).ItemId);
        Assert.True(all.RemovedFolders[0].IsFolder);
        Assert.Equal([BaseItemKind.Series], all.Kinds);
        Assert.Equal(1, all.Count);
    }

    [Fact]
    public async Task ARemovedMusicArtistFolder_ReachesRemovedFolders_ThoughDeleteItemCutItsParentLink_AndAnArtistByNameDoesnt()
    {
        // LibraryManager.DeleteItem cuts the parent link before ItemRemoved (LibraryManager.cs:451, 466), and a MusicArtist
        // is a folder only while it has a parent (MusicArtist.cs:30-33), so the item itself says IsFolder == false then.
        var episodes = new RecordingConsumer("Episodes", new LibraryChangeFilter { Kinds = new HashSet<BaseItemKind> { BaseItemKind.Episode } });
        var artists = new RecordingConsumer("Artists", new LibraryChangeFilter { Kinds = new HashSet<BaseItemKind> { BaseItemKind.MusicArtist } });
        var monitor = Subscribed(episodes, artists);
        var music = _library.Add(new CollectionFolder { Name = "Music" });
        var folderArtist = _library.Add(new MusicArtist { Name = "Folder Artist" }, music);
        var album = _library.Add(new MusicAlbum { Name = "Album" }, folderArtist);
        var byName = _library.Add(new MusicArtist { Name = "By Name" });
        Assert.True(folderArtist.IsFolder);
        Assert.False(byName.IsFolder);

        _library.Remove(folderArtist);                  // the scan's validation: the artist's folder is gone
        _library.Remove(byName);                        // ArtistsValidator: a dead artist by name
        Assert.False(folderArtist.IsFolder);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        // The albums store no artist, so the verify sweep needs the artist folder in RemovedFolders.
        var forEpisodes = episodes.Batches.Single();
        Assert.Empty(forEpisodes.Removed);
        var folder = Assert.Single(forEpisodes.RemovedFolders);
        Assert.Equal(folderArtist.Id, folder.ItemId);
        Assert.True(folder.IsFolder);
        var forArtists = artists.Batches.Single();
        Assert.Equal([folderArtist.Id, byName.Id], forArtists.Removed.Select(c => c.ItemId));
        Assert.Equal([true, false], forArtists.Removed.Select(c => c.IsFolder));
        Assert.Equal([folderArtist.Id], forArtists.RemovedFolders.Select(c => c.ItemId));
        Assert.DoesNotContain(album.Id, forArtists.Removed.Concat(forArtists.RemovedFolders).Select(c => c.ItemId));
    }

    [Fact]
    public async Task ARemovedItem_HasItsIdentity_CapturedInTheHandler()
    {
        var series = _library.AddSeries("Test Series", null, ("Tvdb", "900401"));
        var season = _library.AddSeason(series, 2);
        var episode = _library.AddEpisode(series, season, 3, TimeSpan.FromMinutes(42));
        var capturing = new RecordingConsumer("Capturing");
        var plain = new RecordingConsumer("Plain", new LibraryChangeFilter { CaptureIdentity = false });
        var monitor = Subscribed(capturing, plain);

        _library.Remove(episode);
        Assert.Null(_library.Object.GetItemById(episode.Id));
        await monitor.FlushAsync(fullPass: false, duringPostScan: false, null, Ct);

        var identity = Assert.Single(capturing.Batches.Single().Removed).Identity!;
        Assert.Equal(episode.Id, identity.ItemId);
        Assert.Equal("Episode", identity.Kind);
        Assert.Equal("Episode 3", identity.Name);
        Assert.Equal(series.Id, identity.SeriesId);
        Assert.Equal(season.Id, identity.SeasonId);
        Assert.Equal(2, identity.Season);
        Assert.Equal(3, identity.Episode);
        Assert.Equal("900401", identity.SeriesProviderIds!["Tvdb"]);
        Assert.Equal(TimeSpan.FromMinutes(42).Ticks, identity.RunTimeTicks);
        Assert.Equal(season.Id, capturing.Batches.Single().Removed[0].ParentId);

        // The same change object for both consumers: captured once, and carried to a consumer that didn't ask too.
        Assert.Same(capturing.Batches.Single().Removed[0], plain.Batches.Single().Removed[0]);
    }

    [Fact]
    public async Task AnIdentityThatCantBeCaptured_CostsTheChangeOnlyItsIdentity()
    {
        var series = _library.AddSeries("Test Series", null, ("Tvdb", "900401"));
        var season = _library.AddSeason(series, 1);
        var episode = _library.AddEpisode(series, season, 1);
        _library.Mock.Setup(l => l.GetItemById(series.Id)).Throws(new InvalidOperationException("database busy"));
        var consumer = new RecordingConsumer("Capturing", new LibraryChangeFilter { CaptureTopParent = true });
        var monitor = Subscribed(consumer);

        _library.Remove(episode);
        await monitor.FlushAsync(fullPass: false, duringPostScan: false, null, Ct);

        var removed = Assert.Single(consumer.Batches.Single().Removed);
        Assert.Equal(episode.Id, removed.ItemId);
        Assert.Null(removed.Identity);
        Assert.Null(removed.TopParentId);
        Assert.Single(_log.At(LogLevel.Warning));
        Assert.Equal(2, monitor.Describe()["HandlerErrors"]);
    }

    [Fact]
    public async Task WithoutAConsumerThatAsks_NoIdentityIsCaptured()
    {
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        var consumer = new RecordingConsumer("Plain", new LibraryChangeFilter { CaptureIdentity = false });
        var monitor = Subscribed(consumer);
        _library.IdLookups.Clear();

        _library.Remove(movie);
        await monitor.FlushAsync(fullPass: false, duringPostScan: false, null, Ct);

        Assert.Null(Assert.Single(consumer.Batches.Single().Removed).Identity);
        Assert.Empty(_library.IdLookups);
    }

    [Fact]
    public async Task UserData_IsOptIn_AndPlaybackStartAndProgressDontCount()
    {
        var withUserData = new RecordingConsumer("WithUserData", new LibraryChangeFilter { IncludeUserData = true });
        var without = new RecordingConsumer("Without");
        var monitor = Subscribed(withUserData, without);
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));

        SaveUserData(movie, User1, UserDataSaveReason.PlaybackStart);
        SaveUserData(movie, User1, UserDataSaveReason.PlaybackProgress);
        SaveUserData(movie, User1, UserDataSaveReason.UpdateUserRating);
        SaveUserData(movie, User2, UserDataSaveReason.PlaybackFinished);
        await monitor.FlushAsync(fullPass: false, duringPostScan: false, null, Ct);

        var batch = Assert.Single(withUserData.Batches);
        Assert.Equal(
            [(User1, UserDataSaveReason.UpdateUserRating), (User2, UserDataSaveReason.PlaybackFinished)],
            batch.UserData.Select(c => (c.UserId!.Value, c.SaveReason!.Value)));
        Assert.All(batch.UserData, c => Assert.Equal(LibraryChangeKind.UserData, c.Kind));
        Assert.Empty(without.Batches);
        _userData.VerifyAdd(u => u.UserDataSaved += It.IsAny<EventHandler<UserDataSaveEventArgs>>(), Times.Once);

        // A monitor whose consumers don't ask for user data doesn't subscribe to it.
        Subscribed(new RecordingConsumer("Other"));
        _userData.VerifyAdd(u => u.UserDataSaved += It.IsAny<EventHandler<UserDataSaveEventArgs>>(), Times.Once);
    }

    [Fact]
    public async Task AConsumerThatCouldntBeCreated_IsTriedAgainByTheTimerPath_AndGetsTheChangesFromThenOn()
    {
        // One consumer that can't be created costs only itself, and the timer path looks for it again every
        // ConsumerRetryInterval; a consumer added then that asks for user data gets the subscription.
        var working = new RecordingConsumer("Working");
        var late = new RecordingConsumer("Late", new LibraryChangeFilter { IncludeUserData = true });
        var canCreate = false;
        var missing = false;

        // As KitComponents: the factory tries every consumer, and the one that failed is listed until an attempt succeeds.
        var monitor = new LibraryChangeMonitor(
            _host.Object,
            _library.Object,
            _userData.Object,
            () =>
            {
                missing = !canCreate;
                return canCreate ? [working, late] : [working];
            },
            TestKit.Options,
            _loggerFactory.Object,
            _clock,
            () => missing);
        _monitors.Add(monitor);
        monitor.Subscribe();
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));

        _library.RaiseAdded(movie);
        Assert.Equal(TimeSpan.FromSeconds(60), await monitor.DispatchDueAsync(Ct));
        _clock.Advance(TimeSpan.FromSeconds(30));

        // Nothing waits for the timer any more, but the retry is due in 30 s.
        Assert.Equal(TimeSpan.FromSeconds(30), await monitor.DispatchDueAsync(Ct));
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(LibraryChangeMonitor.ConsumerRetryInterval, await monitor.DispatchDueAsync(Ct));
        Assert.Single(working.Batches);
        Assert.Equal(["Working"], monitor.Queues.Select(q => q.Name));
        _userData.VerifyAdd(u => u.UserDataSaved += It.IsAny<EventHandler<UserDataSaveEventArgs>>(), Times.Never);

        // Not before the retry is due; then added, subscribed to user data, and given what comes after.
        canCreate = true;
        Assert.False(monitor.TryAddUnavailableConsumers());
        _clock.Advance(LibraryChangeMonitor.ConsumerRetryInterval);
        await monitor.DispatchDueAsync(Ct);
        Assert.Equal(["Working", "Late"], monitor.Queues.Select(q => q.Name));
        _userData.VerifyAdd(u => u.UserDataSaved += It.IsAny<EventHandler<UserDataSaveEventArgs>>(), Times.Once);
        Assert.Single(_log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("now go to Late too", StringComparison.Ordinal));

        SaveUserData(movie, User1, UserDataSaveReason.TogglePlayed);
        _library.RaiseUpdated(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        var batch = Assert.Single(late.Batches);
        Assert.Equal([movie.Id], batch.Updated.Select(c => c.ItemId));
        Assert.Equal([User1], batch.UserData.Select(c => c.UserId!.Value));
        Assert.Empty(batch.Added);
    }

    [Fact]
    public async Task TheTimerPath_DispatchesWhenNoChangeCameForTheQuietPeriod()
    {
        var consumer = new RecordingConsumer("Timer", library: _library.Object);
        var monitor = Subscribed(consumer);
        var a = _library.AddMovie("A", 2001, TimeSpan.FromMinutes(90));
        var b = _library.AddMovie("B", 2002, TimeSpan.FromMinutes(90));

        _library.RaiseAdded(a);
        _clock.Advance(TimeSpan.FromSeconds(30));
        _library.RaiseUpdated(b, ItemUpdateType.MetadataDownload);
        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), await monitor.DispatchDueAsync(Ct));
        Assert.Empty(consumer.Batches);

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(await monitor.DispatchDueAsync(Ct));

        var received = Assert.Single(consumer.Received);
        Assert.Equal([a.Id], received.Batch.Added.Select(c => c.ItemId));
        Assert.Equal(ItemUpdateType.MetadataDownload, Assert.Single(received.Batch.Updated).UpdateReason);
        Assert.False(received.Batch.FullPass);
        Assert.False(received.Batch.DuringPostScan);
        Assert.False(received.ScanRunning);
        Assert.Null(await monitor.DispatchDueAsync(Ct));
    }

    [Fact]
    public async Task ABusyLibrary_StillGetsABatch_TenMinutesAfterItsFirstChange()
    {
        var consumer = new RecordingConsumer("Busy");
        var monitor = Subscribed(consumer);
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));

        for (var second = 0; second < 600; second += 50)
        {
            _library.RaiseUpdated(movie);
            _clock.Advance(TimeSpan.FromSeconds(50));
            await monitor.DispatchDueAsync(Ct);
            Assert.Equal(second + 50 >= 600 ? 1 : 0, consumer.Batches.Count);
        }

        Assert.Single(consumer.Batches.Single().Updated);
    }

    [Fact]
    public async Task WhileAScanRuns_TheTimerPathKeepsCollecting_AndDispatchesOnceTheScanEnded()
    {
        var consumer = new RecordingConsumer("Deferred", library: _library.Object);
        var monitor = Subscribed(consumer);
        var a = _library.AddMovie("A", 2001, TimeSpan.FromMinutes(90));
        var b = _library.AddMovie("B", 2002, TimeSpan.FromMinutes(90));

        _library.RaiseAdded(a);
        _library.IsScanRunning = true;
        _clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(LibraryChangeMonitor.ScanRecheckInterval, await monitor.DispatchDueAsync(Ct));

        _library.RaiseAdded(b);
        _clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(LibraryChangeMonitor.ScanRecheckInterval, await monitor.DispatchDueAsync(Ct));
        Assert.Empty(consumer.Batches);
        Assert.Equal(1, monitor.Queues[0].Deferrals);

        // A scan that ends without a post-scan run (cancelled): what was collected goes out as an ordinary batch.
        _library.IsScanRunning = false;
        Assert.Null(await monitor.DispatchDueAsync(Ct));

        var received = Assert.Single(consumer.Received);
        Assert.Equal([a.Id, b.Id], received.Batch.Added.Select(c => c.ItemId));
        Assert.False(received.Batch.FullPass);
        Assert.False(received.ScanRunning);
    }

    [Fact]
    public async Task ThePostScanFlush_WhileIsScanRunningIsStillTrue_DispatchesAFullPassSynchronously()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new RecordingConsumer("PostScan", library: _library.Object) { OnHandle = (_, _) => release.Task };
        var monitor = Subscribed(consumer);
        var added = _library.AddMovie("Added", 2001, TimeSpan.FromMinutes(90));
        var removed = _library.AddMovie("Removed", 2002, TimeSpan.FromMinutes(90));

        // The scan runs: its changes are collected, and the due batch waits for the scan (the guarded timer path).
        _library.IsScanRunning = true;
        _library.RaiseAdded(added);
        _library.Remove(removed);
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(LibraryChangeMonitor.ScanRecheckInterval, await monitor.DispatchDueAsync(Ct));
        Assert.Empty(consumer.Batches);

        // The post-scan task runs inside the scan (LibraryManager.cs:1133-1143, 1208): IsScanRunning is still true, and the
        // flush neither waits for it nor goes through the timer path.
        var progress = new ListProgress();
        var flush = monitor.FlushAsync(fullPass: true, duringPostScan: true, progress, Ct);

        var received = Assert.Single(consumer.Received);
        Assert.True(received.Batch.FullPass);
        Assert.True(received.Batch.DuringPostScan);
        Assert.True(received.ScanRunning);
        Assert.Equal([added.Id], received.Batch.Added.Select(c => c.ItemId));
        Assert.Equal([removed.Id], received.Batch.Removed.Select(c => c.ItemId));

        // Synchronous: the flush returns only when the consumer is done.
        await Task.Delay(50, Ct);
        Assert.False(flush.IsCompleted);
        release.SetResult();
        await flush.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(100, progress.Values.Last());

        // Nothing is left to go out later as an ordinary batch, during the scan or after it; the timer path only looks every
        // second whether the scan has ended (Jellyfin's own post-scan tasks run after this one).
        Assert.True(monitor.Queues[0].IsEmpty);
        Assert.Equal(LibraryChangeMonitor.ScanEndRecheckInterval, await monitor.DispatchDueAsync(Ct));
        _library.IsScanRunning = false;
        Assert.Null(await monitor.DispatchDueAsync(Ct));
        Assert.Single(consumer.Batches);
        Assert.Equal(1, monitor.Queues[0].FullPasses);
        Assert.Contains(_log.At(LogLevel.Information), e => e.Message.StartsWith("Library scan finished: full pass to PostScan (1 added, 0 updated, 1 removed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangesRaisedAfterTheFullPass_WhileTheScanStillRuns_GoOutAsSoonAsTheScanEnds_WithoutTheQuietPeriod()
    {
        // Jellyfin 10.11.5 runs the plugins' post-scan tasks before its own (ApplicationHost.cs:822-826,
        // LibraryManager.cs:263, 1220-1265): the artists, genres, studios, collections and clean-up tasks change the library
        // after the full pass, while IsScanRunning is still true.
        var consumer = new RecordingConsumer("Tail", new LibraryChangeFilter { IncludeUserData = true }, _library.Object);
        var monitor = Subscribed(consumer);
        var movie = _library.AddMovie("Scanned", 2001, TimeSpan.FromMinutes(90));
        var orphan = _library.AddMovie("Orphan", 2002, TimeSpan.FromMinutes(90));
        var artist = _library.Add(new MusicArtist { Name = "By Name" });
        var genre = _library.Add(new Genre { Name = "New Genre" });

        _library.IsScanRunning = true;
        _library.RaiseAdded(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        Assert.True(monitor.IsAfterFullPass);

        _library.RaiseAdded(genre);                     // GenresValidator
        _library.Remove(artist);                        // ArtistsValidator: "Deleting dead MusicArtist", an artist by name
        _library.Remove(orphan);                        // CleanDatabaseScheduledTask: an item whose parent is gone
        SaveUserData(movie, User1, UserDataSaveReason.UpdateUserRating);
        Assert.True(monitor.Queues[0].HasAfterFullPass);

        // Not in the full pass, and nothing while the scan runs; the timer path looks every second whether it has ended.
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(LibraryChangeMonitor.ScanEndRecheckInterval, await monitor.DispatchDueAsync(Ct));
        var full = Assert.Single(consumer.Batches);
        Assert.Equal([movie.Id], full.Added.Select(c => c.ItemId));
        Assert.Equal(0, monitor.Queues[0].Deferrals);

        // The scan ends 10 s after the last change: the batch goes out at once, not after the 60 s quiet period.
        _library.IsScanRunning = false;
        Assert.Null(await monitor.DispatchDueAsync(Ct));

        var received = consumer.Received[1];
        Assert.True(received.Batch.AfterFullPass);
        Assert.False(received.Batch.FullPass);
        Assert.False(received.Batch.DuringPostScan);
        Assert.False(received.ScanRunning);
        Assert.Equal([genre.Id], received.Batch.Added.Select(c => c.ItemId));
        Assert.Equal([artist.Id, orphan.Id], received.Batch.Removed.Select(c => c.ItemId));
        Assert.Empty(received.Batch.RemovedFolders);    // an artist by name is no folder (MusicArtist.cs:30-33)
        Assert.Equal([movie.Id], received.Batch.UserData.Select(c => c.ItemId));
        Assert.False(monitor.IsAfterFullPass);
        Assert.False(monitor.Queues[0].HasAfterFullPass);
        Assert.Contains(_log.At(LogLevel.Debug), e => e.Message.StartsWith("Library scan ended: changes after the full pass to Tail (1 added, 0 updated, 2 removed, 0 removed folders, 1 user data)", StringComparison.Ordinal));
        var recent = Assert.IsType<List<IDictionary<string, object?>>>(monitor.Queues[0].Describe()["Recent"]);
        Assert.Equal([true, false], recent.Select(r => r["AfterFullPass"]));

        // After the scan a change is ordinary again: it waits for its quiet period.
        _library.RaiseUpdated(movie);
        Assert.False(monitor.Queues[0].HasAfterFullPass);
        Assert.Equal(TimeSpan.FromSeconds(60), await monitor.DispatchDueAsync(Ct));
        _clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Null(await monitor.DispatchDueAsync(Ct));
        Assert.False(consumer.Batches[2].AfterFullPass);
    }

    [Fact]
    public async Task ChangesAfterTheFullPass_WhoseQuietPeriodEndsDuringTheScan_StillWaitForIt_AndCountADeferral()
    {
        var consumer = new RecordingConsumer("LongTail", library: _library.Object);
        var monitor = Subscribed(consumer);
        var genre = _library.Add(new Genre { Name = "Slow Genre" });

        _library.IsScanRunning = true;
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        _library.RaiseAdded(genre);

        // A long ArtistsValidator: the quiet period and even the maximum wait run out while the scan still runs.
        _clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(LibraryChangeMonitor.ScanEndRecheckInterval, await monitor.DispatchDueAsync(Ct));
        Assert.Equal(LibraryChangeMonitor.ScanEndRecheckInterval, await monitor.DispatchDueAsync(Ct));
        Assert.Single(consumer.Batches);
        Assert.Equal(1, monitor.Queues[0].Deferrals);

        _library.IsScanRunning = false;
        Assert.Null(await monitor.DispatchDueAsync(Ct));
        Assert.True(consumer.Batches[1].AfterFullPass);
        Assert.Equal([genre.Id], consumer.Batches[1].Added.Select(c => c.ItemId));
    }

    [Fact]
    public async Task TheMarkEndsWithTheScan_AndOnlyAFlushInsideTheScanSetsIt()
    {
        var consumer = new RecordingConsumer("Marks", library: _library.Object);
        var monitor = Subscribed(consumer);
        var a = _library.AddMovie("A", 2001, TimeSpan.FromMinutes(90));
        var b = _library.AddMovie("B", 2002, TimeSpan.FromMinutes(90));

        // A flush from outside the post-scan task (tests, a later admin action) marks nothing.
        _library.IsScanRunning = true;
        await monitor.FlushAsync(fullPass: true, duringPostScan: false, null, Ct);
        Assert.False(monitor.IsAfterFullPass);

        // A scan whose post-scan phase changes nothing: the timer path looks every second until it sees the scan end.
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        Assert.True(monitor.IsAfterFullPass);
        Assert.Equal(LibraryChangeMonitor.ScanEndRecheckInterval, await monitor.DispatchDueAsync(Ct));
        _library.IsScanRunning = false;
        Assert.Null(await monitor.DispatchDueAsync(Ct));
        Assert.False(monitor.IsAfterFullPass);

        // The next scan: its changes before its own full pass aren't marked, and a cancelled scan (no full pass) leaves
        // them to the quiet period.
        _library.IsScanRunning = true;
        _library.RaiseAdded(a);
        Assert.False(monitor.Queues[0].HasAfterFullPass);
        _library.IsScanRunning = false;
        Assert.Equal(TimeSpan.FromSeconds(60), await monitor.DispatchDueAsync(Ct));

        // A change raised after the full pass but between the scan's end and the monitor seeing it is not marked.
        _clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Null(await monitor.DispatchDueAsync(Ct));
        _library.IsScanRunning = true;
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        _library.IsScanRunning = false;
        _library.RaiseAdded(b);
        Assert.False(monitor.Queues[0].HasAfterFullPass);
        Assert.Equal(TimeSpan.FromSeconds(60), await monitor.DispatchDueAsync(Ct));
        Assert.False(monitor.IsAfterFullPass);
    }

    [Fact]
    public async Task ChangesAfterAFullPass_ThatANewScanOvertakes_GoIntoItsFullPass()
    {
        var consumer = new RecordingConsumer("Overtaken", library: _library.Object);
        var monitor = Subscribed(consumer);
        var genre = _library.Add(new Genre { Name = "Late Genre" });

        _library.IsScanRunning = true;
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        _library.RaiseAdded(genre);

        // The scan ends and the next one starts before the monitor looks: the changes wait, and its full pass takes them.
        _library.IsScanRunning = false;
        _library.IsScanRunning = true;
        Assert.Equal(LibraryChangeMonitor.ScanEndRecheckInterval, await monitor.DispatchDueAsync(Ct));
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.Equal(2, consumer.Batches.Count);
        Assert.True(consumer.Batches[1].FullPass);
        Assert.False(consumer.Batches[1].AfterFullPass);
        Assert.Equal([genre.Id], consumer.Batches[1].Added.Select(c => c.ItemId));
        Assert.True(monitor.Queues[0].IsEmpty);
    }

    [Fact]
    public async Task TheLoop_SendsTheChangesAfterTheFullPass_ASecondAfterTheScanEnded()
    {
        var consumer = new RecordingConsumer("LoopTail", library: _library.Object);
        var monitor = Monitor(consumer);
        var genre = _library.Add(new Genre { Name = "Loop Genre" });
        await monitor.StartAsync(Ct);
        await WaitUntilAsync(() => monitor.IsSubscribed);

        _library.IsScanRunning = true;
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        _library.RaiseAdded(genre);
        await _clock.WaitForArmedTimersAsync();
        _clock.Advance(LibraryChangeMonitor.ScanEndRecheckInterval);
        await _clock.WaitForArmedTimersAsync();
        Assert.Single(consumer.Batches);

        _library.IsScanRunning = false;
        _clock.Advance(LibraryChangeMonitor.ScanEndRecheckInterval);

        var batch = await consumer.WaitForBatchAsync(2);
        Assert.True(batch.AfterFullPass);
        Assert.Equal([genre.Id], batch.Added.Select(c => c.ItemId));
        await monitor.StopAsync(Ct);
    }

    [Fact]
    public async Task AFullPass_IsDispatchedEvenWhenNothingChanged_WhereAnOrdinaryFlushSkipsIt()
    {
        var consumer = new RecordingConsumer("Empty");
        var monitor = Subscribed(consumer);

        await monitor.FlushAsync(fullPass: false, duringPostScan: false, null, Ct);
        Assert.Empty(consumer.Batches);

        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        var batch = Assert.Single(consumer.Batches);
        Assert.True(batch.IsEmpty);
        Assert.True(batch.FullPass);
        Assert.True(batch.DuringPostScan);
    }

    [Fact]
    public async Task EachConsumer_IsWrappedInATryCatch()
    {
        var throwing = new RecordingConsumer("Throwing") { OnHandle = (_, _) => throw new InvalidOperationException("boom") };
        var failing = new RecordingConsumer("Failing") { OnHandle = async (_, _) => { await Task.Yield(); throw new InvalidOperationException("later"); } };
        var fine = new RecordingConsumer("Fine");
        var monitor = Subscribed(throwing, failing, fine);
        _library.RaiseAdded(_library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170)));

        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.Single(throwing.Batches);
        Assert.Single(failing.Batches);
        Assert.Single(fine.Batches.Single().Added);
        Assert.Equal([1L, 1L, 0L], monitor.Queues.Select(q => q.Failures));
        Assert.Equal(2, _log.At(LogLevel.Error).Count());
        Assert.Contains(_log.At(LogLevel.Error), e => e.Message.Contains("consumer Throwing failed on a batch of 1 changes (full pass: True)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFlush_WaitsForATimerBatchInProgress_AndChangesMeanwhileGoIntoTheFullPass()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new RecordingConsumer("Serial") { OnHandle = (batch, _) => batch.FullPass ? Task.CompletedTask : hold.Task };
        var monitor = Subscribed(consumer);
        var a = _library.AddMovie("A", 2001, TimeSpan.FromMinutes(90));
        var b = _library.AddMovie("B", 2002, TimeSpan.FromMinutes(90));

        _library.RaiseAdded(a);
        _clock.Advance(TimeSpan.FromSeconds(60));
        var timer = monitor.DispatchDueAsync(Ct);
        await consumer.WaitForBatchAsync(1);

        _library.RaiseAdded(b);
        _library.IsScanRunning = true;
        var flush = monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        await Task.Delay(50, Ct);
        Assert.False(flush.IsCompleted);
        Assert.Single(consumer.Batches);

        hold.SetResult();
        await timer;
        await flush;

        Assert.Equal([a.Id], consumer.Batches[0].Added.Select(c => c.ItemId));
        Assert.True(consumer.Batches[1].FullPass);
        Assert.Equal([b.Id], consumer.Batches[1].Added.Select(c => c.ItemId));
    }

    [Fact]
    public async Task ACancelledScan_CancelsTheFlush_AndTheNextConsumersKeepTheirChanges()
    {
        using var scan = new CancellationTokenSource();
        var first = new RecordingConsumer("First") { OnHandle = (_, token) => Task.Delay(Timeout.Infinite, token) };
        var second = new RecordingConsumer("Second");
        var monitor = Subscribed(first, second);
        _library.RaiseAdded(_library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170)));

        var flush = monitor.FlushAsync(fullPass: true, duringPostScan: true, null, scan.Token);
        await first.WaitForBatchAsync(1);
        await scan.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush);
        Assert.Empty(second.Batches);
        Assert.False(monitor.Queues[1].IsEmpty);
        Assert.Empty(_log.At(LogLevel.Error));
        Assert.Equal(0, monitor.Queues[0].Failures);
    }

    [Fact]
    public async Task TheService_SubscribesOnlyOnceTheGateIsOpen_ThenItsLoopDispatches_AndStopUnsubscribes()
    {
        _started = false;
        var consumer = new RecordingConsumer("Loop", new LibraryChangeFilter { QuietPeriod = TimeSpan.FromSeconds(5), MaxWait = TimeSpan.FromSeconds(30) });
        var monitor = Monitor(consumer);
        var early = _library.AddMovie("Early", 2001, TimeSpan.FromMinutes(90));
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));

        await monitor.StartAsync(Ct);
        await _clock.WaitForArmedTimersAsync();
        _library.RaiseAdded(early);
        Assert.False(monitor.IsSubscribed);

        _started = true;
        _clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => monitor.IsSubscribed);
        _library.RaiseAdded(movie);
        await _clock.WaitForArmedTimersAsync();
        _clock.Advance(TimeSpan.FromSeconds(5));

        var batch = await consumer.WaitForBatchAsync(1);
        Assert.Equal([movie.Id], batch.Added.Select(c => c.ItemId));

        await monitor.StopAsync(Ct);
        Assert.False(monitor.IsSubscribed);
        _library.Mock.VerifyAdd(l => l.ItemAdded += It.IsAny<EventHandler<ItemChangeEventArgs>>(), Times.Once);
        _library.Mock.VerifyRemove(l => l.ItemAdded -= It.IsAny<EventHandler<ItemChangeEventArgs>>(), Times.Once);
        _library.Mock.VerifyRemove(l => l.ItemRemoved -= It.IsAny<EventHandler<ItemChangeEventArgs>>(), Times.Once);
        Assert.Empty(_log.At(LogLevel.Error));
    }

    [Fact]
    public async Task WithoutConsumers_NothingIsSubscribed_AndAFlushIsANoOp()
    {
        var monitor = Monitor();
        var progress = new ListProgress();

        await monitor.StartAsync(Ct);
        await monitor.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        monitor.Subscribe();
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, progress, Ct);

        Assert.False(monitor.IsSubscribed);
        _library.Mock.VerifyAdd(l => l.ItemAdded += It.IsAny<EventHandler<ItemChangeEventArgs>>(), Times.Never);
        Assert.Equal([100.0], progress.Values);
    }

    [Fact]
    public async Task ConsumersThatCantBeCreated_LeaveTheMonitorIdle_WithOneError()
    {
        var monitor = new LibraryChangeMonitor(_host.Object, _library.Object, _userData.Object, () => throw new InvalidOperationException("DI"), TestKit.Options, _loggerFactory.Object, _clock);
        _monitors.Add(monitor);

        monitor.Subscribe();
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.Empty(monitor.Queues);
        Assert.False(monitor.IsSubscribed);
        Assert.Contains("couldn't be created", Assert.Single(_log.At(LogLevel.Error)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConsumerWithoutAUsableFilter_IsLeftOut_AndTheOthersStillWork()
    {
        var fine = new RecordingConsumer("Fine");
        var monitor = Subscribed(new BrokenFilterConsumer(), fine);
        _library.RaiseAdded(_library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170)));

        await monitor.FlushAsync(fullPass: false, duringPostScan: false, null, Ct);

        Assert.Equal(["Fine"], monitor.Queues.Select(q => q.Name));
        Assert.Single(fine.Batches);
        Assert.Single(_log.At(LogLevel.Error));
    }

    [Fact]
    public async Task TheHandlers_NeverThrow_AndABrokenFilterCostsOnlyItsConsumerTheEvent()
    {
        var broken = new RecordingConsumer("Broken", new LibraryChangeFilter { Ignore = (_, _) => throw new InvalidOperationException("filter") });
        var fine = new RecordingConsumer("Fine", new LibraryChangeFilter { IncludeUserData = true });
        var monitor = Subscribed(broken, fine);
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));

        _library.RaiseAdded(movie);
        _library.RaiseUpdated(movie);
        SaveUserData(movie, User1, UserDataSaveReason.TogglePlayed);
        monitor.OnItemChanged(LibraryChangeKind.Added, null);
        monitor.OnItemChanged(LibraryChangeKind.Added, new ItemChangeEventArgs());
        monitor.OnUserDataChanged(null);
        await monitor.FlushAsync(fullPass: false, duringPostScan: false, null, Ct);

        Assert.Empty(broken.Batches);
        Assert.Single(fine.Batches.Single().Added);
        Assert.Single(fine.Batches.Single().UserData);
        Assert.Single(_log.At(LogLevel.Warning));
        Assert.Equal(1, _log.At(LogLevel.Debug).Count(e => e.Message.StartsWith("A library event", StringComparison.Ordinal)));
        Assert.Equal(2, monitor.Describe()["HandlerErrors"]);
    }

    [Fact]
    public async Task TheTopParent_IsTheLibraryFolder_AlsoForARemovedItem()
    {
        var root = _library.Add(new AggregateFolder { Name = "root" });
        var movies = _library.Add(new CollectionFolder { Name = "Shows" }, root);
        var series = _library.AddSeries("Test Series");
        series.ParentId = movies.Id;
        var season = _library.AddSeason(series, 1);
        var episode = _library.AddEpisode(series, season, 1);
        var withTop = new RecordingConsumer("WithTop", new LibraryChangeFilter { CaptureTopParent = true });
        var withoutTop = new RecordingConsumer("WithoutTop", new LibraryChangeFilter { Kinds = new HashSet<BaseItemKind> { BaseItemKind.Season } });
        var monitor = Subscribed(withTop, withoutTop);

        _library.RaiseAdded(episode);
        _library.RaiseAdded(movies);

        // LibraryManager.DeleteItem cuts a removed item's parent link before it raises ItemRemoved.
        season.ParentId = Guid.Empty;
        _library.Mock.Raise(l => l.ItemRemoved += null, _library.Object, new ItemChangeEventArgs { Item = season, Parent = series });
        await monitor.FlushAsync(fullPass: false, duringPostScan: false, null, Ct);

        var batch = withTop.Batches.Single();
        Assert.Equal([movies.Id, movies.Id], batch.Added.Select(c => c.TopParentId));
        Assert.Equal(movies.Id, batch.Removed.Single().TopParentId);
        Assert.Equal(series.Id, batch.Removed.Single().ParentId);

        // The change is shared, so the consumer that didn't ask sees the id as well; one without any capture has none.
        var plain = Subscribed(new RecordingConsumer("Plain"));
        _library.RaiseAdded(episode);
        await plain.FlushAsync(fullPass: false, duringPostScan: false, null, Ct);
        Assert.Null(((RecordingConsumer)plain.Queues[0].Consumer).Batches.Single().Added.Single().TopParentId);
    }

    [Fact]
    public async Task Describe_ShowsWhatTheMonitorWatches_AndEachConsumersBatches()
    {
        var consumer = new RecordingConsumer("Described", new LibraryChangeFilter { Kinds = new HashSet<BaseItemKind> { BaseItemKind.Movie }, IncludeUserData = true });
        var monitor = Subscribed(consumer);
        _library.IsScanRunning = true;
        _library.RaiseAdded(_library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170)));
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var described = monitor.Describe();

        Assert.Equal(true, described["Watching"]);
        Assert.Equal(true, described["UserData"]);
        Assert.Equal(true, described["ScanRunning"]);
        var entry = Assert.Single(Assert.IsType<List<IDictionary<string, object?>>>(described["Consumers"]));
        Assert.Equal("Described", entry["Name"]);
        Assert.Equal(["Movie"], Assert.IsType<List<string>>(entry["Kinds"]));
        Assert.Equal(60.0, entry["QuietSeconds"]);
        Assert.Equal(600.0, entry["MaxWaitSeconds"]);
        Assert.Equal(1L, entry["FullPasses"]);
        var recent = Assert.Single(Assert.IsType<List<IDictionary<string, object?>>>(entry["Recent"]));
        Assert.Equal(true, recent["FullPass"]);
        Assert.Equal(true, recent["DuringPostScan"]);
        Assert.Equal(true, recent["ScanRunning"]);
        Assert.Equal(1, recent["Added"]);
        _loggerFactory.Verify(f => f.CreateLogger("Jellyfin.Plugin.KitTest.Kit.LibraryChanges"), Times.AtLeastOnce);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(5, Ct);
        }
    }

    private LibraryChangeMonitor Monitor(params ILibraryChangeConsumer[] consumers)
    {
        var monitor = new LibraryChangeMonitor(_host.Object, _library.Object, _userData.Object, () => consumers, TestKit.Options, _loggerFactory.Object, _clock);
        _monitors.Add(monitor);
        return monitor;
    }

    private LibraryChangeMonitor Subscribed(params ILibraryChangeConsumer[] consumers)
    {
        var monitor = Monitor(consumers);
        monitor.Subscribe();
        Assert.True(monitor.IsSubscribed);
        return monitor;
    }

    private void SaveUserData(BaseItem item, Guid userId, UserDataSaveReason reason)
        => _userData.Raise(u => u.UserDataSaved += null, _userData.Object, new UserDataSaveEventArgs { Item = item, UserId = userId, SaveReason = reason });

    private sealed class BrokenFilterConsumer : ILibraryChangeConsumer
    {
        public string Name => "Broken";

        public LibraryChangeFilter Filter => throw new InvalidOperationException("no filter");

        public Task HandleAsync(LibraryChangeBatch batch, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

/// <summary>A progress sink that keeps what it is told, synchronously.</summary>
internal sealed class ListProgress : IProgress<double>
{
    private readonly List<double> _values = [];

    public IReadOnlyList<double> Values
    {
        get
        {
            lock (_values)
            {
                return _values.ToList();
            }
        }
    }

    public void Report(double value)
    {
        lock (_values)
        {
            _values.Add(value);
        }
    }
}
