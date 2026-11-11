using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Api;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Tasks;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Services;

/// <summary>
/// The developer library demo: off by default (403 "turned off", and the kit leaves its records alone), admin-only;
/// when on, its records go through the kit's lifecycle as bookmarks use it: a removed item or container
/// detaches them, a removed folder's batch runs the verify sweep, added items relink them (Exact keeps the media source,
/// Strong clears it and marks the record), Bookmarks' runtime check leaves the edition pair ambiguous, retention archives
/// them, the export carries the archive, the item's return reattaches them, and purge deletes them after a snapshot. Its
/// reads show FreshUserData, LibraryAccess and VersionSets as a user.
/// </summary>
public sealed class LibraryDemoTests : IDisposable
{
    private static readonly DateTime Monday = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly PluginConfiguration _configuration = new() { EnableLibraryDemo = true };
    private readonly FakeLibrary _library = new();
    private readonly TempDirectory _directory = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(Monday));
    private readonly User _admin = User("admin");
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly List<IDisposable> _disposables = [];
    private LibraryDemoRecords _records = null!;
    private AttachedRecordVerifier _verifier = null!;
    private DetachedRecords _detached = null!;

    public LibraryDemoTests()
    {
        _host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        _host.SetupGet(h => h.SystemId).Returns("sandbox");
        _users.Setup(m => m.GetUserById(_admin.Id)).Returns(_admin);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _directory.Dispose();
    }

    [Fact]
    public async Task IsOffByDefault_TheKitLeavesItAlone_AndEveryCallSaysSo()
    {
        Assert.False(new PluginConfiguration().EnableLibraryDemo);
        _configuration.EnableLibraryDemo = false;
        var demo = CreateDemo();
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));

        foreach (var call in new Func<Task>[]
        {
            () => Task.FromResult(demo.DescribeRecords()),
            () => demo.AddRecordAsync(movie.Id, null, null, false, _admin, Ct),
            () => demo.ClearRecordsAsync(Ct),
            () => demo.SweepAsync(Ct),
            () => demo.ResolveAsync(Ct),
            () => demo.PurgeAsync(null, true, Ct),
            () => Task.FromResult(demo.Export(new DefaultHttpContext())),
            () => Task.FromResult(demo.ReadUserData(movie.Id, null, null, null, _admin)),
            () => Task.FromResult(demo.DescribeLibraries(null, null, _admin)),
            () => Task.FromResult(demo.DescribeVersionSets([movie.Id], null, _admin))
        })
        {
            var error = await Assert.ThrowsAnyAsync<PluginException>(call);
            Assert.Equal(PluginErrorKind.FeatureDisabled, error.Kind);
        }

        Assert.True(_records.IsReadOnly);
        Assert.Empty(_records.GetAttachedItemIds());
        Assert.Empty(_records.GetDetached());
        Assert.True(_records.Filter.Ignore!(LibraryChangeKind.Added, movie));
        await _records.HandleAsync(new LibraryChangeBatch([], [], [], [], [], fullPass: true, duringPostScan: true), Ct);
        Assert.Equal("ReadOnly", (await _verifier.SweepAsync(VerifySweepReason.Daily, Ct)).Owners.Single().Skipped);
        Assert.False(File.Exists(_directory.Combine("library-demo.json")));
    }

    [Fact]
    public void TheController_IsForAdminsOnly()
        => Assert.Equal(Policies.RequiresElevation, typeof(BookmarksLibraryDemoController).GetCustomAttribute<AuthorizeAttribute>()!.Policy);

    [Fact]
    public async Task ARemovedSeries_DetachesItsEpisodes_ThroughContainersAndTheSweep_AndItsRenamedFolderRelinksThemStrong()
    {
        // The series folder renamed and scanned. Removal is one event, for the series (LibraryManager.cs:409-466).
        var demo = CreateDemo();
        var series = _library.AddSeries("Test Series", @"C:\media\Shows\Test Series", ("Tvdb", "900401"));
        var seasons = new[] { _library.AddSeason(series, 1), _library.AddSeason(series, 2) };
        var episodes = seasons.SelectMany(s => new[] { _library.AddEpisode(series, s, 1), _library.AddEpisode(series, s, 2) }).ToList();
        foreach (var episode in episodes)
        {
            await demo.AddRecordAsync(episode.Id, null, "on " + episode.Name, false, _admin, Ct);
        }

        var legacy = (Guid)(await demo.AddRecordAsync(episodes[3].Id, null, "legacy", withoutContainerIds: true, _admin, Ct))["Id"]!;
        var monitor = Monitor();

        _library.Remove(series);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var records = _records.Store.Current.Records;
        Assert.All(records, r => Assert.NotNull(r.DetachedUtc));
        Assert.Equal(4, records.Count(r => r.DetachedBy == "Container"));
        Assert.Equal("Sweep", records.Single(r => r.Id == legacy).DetachedBy);
        Assert.Equal(new[] { VerifySweepReason.RemovedFolders }, _verifier.LastSweep!.Reasons);

        // The renamed folder: new ids, the same TVDb id and numbers.
        var renamed = _library.AddSeries("Test Series", @"C:\media\Shows\Test Series Renamed", ("Tvdb", "900401"));
        var newSeasons = new[] { _library.AddSeason(renamed, 1), _library.AddSeason(renamed, 2) };
        var newEpisodes = newSeasons.SelectMany(s => new[] { _library.AddEpisode(renamed, s, 1), _library.AddEpisode(renamed, s, 2) }).ToList();
        foreach (var item in new BaseItem[] { renamed }.Concat(newSeasons).Concat(newEpisodes))
        {
            _library.RaiseAdded(item);
        }

        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        records = _records.Store.Current.Records;
        Assert.All(records, r =>
        {
            Assert.Null(r.DetachedUtc);
            Assert.Equal(MatchStrength.Strong, r.RelinkStrength);
            Assert.Null(r.MediaSourceId);
            Assert.Equal(Monday, r.RelinkedUtc);
            Assert.Equal(renamed.Id, r.Identity.SeriesId);
        });
        Assert.Equal(newEpisodes[3].Id, records.Single(r => r.Id == legacy).ItemId);
        Assert.Equal(newEpisodes.Select(e => e.Id), records.Where(r => r.Id != legacy).OrderBy(r => r.Identity.Season).ThenBy(r => r.Identity.Episode).Select(r => r.ItemId));
    }

    [Fact]
    public async Task ARemovedMovie_Detaches_AndItsReturnReattachesIt_ExactWithItsMediaSource()
    {
        // Remove a movie file and rescan, put it back and rescan (the same path gives the same id).
        var demo = CreateDemo();
        var movie = _library.AddMovie("Tagged Movie", 2012, TimeSpan.FromMinutes(2), null, ("Tmdb", "900001"));
        await demo.AddRecordAsync(movie.Id, null, "the note", false, _admin, Ct);
        var monitor = Monitor();

        _library.Remove(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        var detached = Assert.Single(_records.Store.Current.Records);
        Assert.Equal(("Removed", Monday), (detached.DetachedBy, detached.DetachedUtc!.Value));

        _library.Add(movie);
        _library.RaiseAdded(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var back = Assert.Single(_records.Store.Current.Records);
        Assert.Null(back.DetachedUtc);
        Assert.Equal(movie.Id, back.ItemId);
        Assert.Equal(movie.Id.ToString("N"), back.MediaSourceId);
        Assert.Null(back.RelinkStrength);
        Assert.Null(back.RelinkedUtc);
    }

    [Fact]
    public async Task TheEditionPair_LeavesTheRecordDetached_AsAmbiguous_InDiagnostics()
    {
        // Deleting the theatrical copy never moves its bookmarks onto the extended cut (runtime within 2 %, 60 s).
        var demo = CreateDemo();
        var sixty = _library.AddMovie("Edition Movie", 2001, TimeSpan.FromSeconds(60), null, ("Tmdb", "900301"));
        _library.AddMovie("Edition Movie Extended", 2001, TimeSpan.FromSeconds(90), null, ("Tmdb", "900301"));
        await demo.AddRecordAsync(sixty.Id, null, null, false, _admin, Ct);
        var monitor = Monitor();

        _library.Remove(sixty);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        await demo.ResolveAsync(Ct);

        var record = Assert.Single(_records.Store.Current.Records);
        Assert.NotNull(record.DetachedUtc);
        Assert.Equal("Ambiguous: FailedValidation", record.LastOutcome);
        var detached = (IDictionary<string, object?>)demo.DescribeRecords()["Detached"]!;
        var owner = Assert.Single((IEnumerable<IDictionary<string, object?>>)detached["Owners"]!);
        Assert.Equal(1, owner["Ambiguous"]);
    }

    [Fact]
    public async Task AfterRetention_ARecordIsArchived_Exported_AndReattachedWhenItsItemReturns()
    {
        // DetachedRetentionDays = 1, the clock moved on, maintenance run.
        var demo = CreateDemo();
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), null, ("Tmdb", "949"));
        await demo.AddRecordAsync(movie.Id, null, "the heist", false, _admin, Ct);
        var monitor = Monitor();
        _library.Remove(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        _configuration.DetachedRetentionDays = 1;
        _clock.Advance(TimeSpan.FromDays(2));

        var archived = await _detached.ArchiveDueAsync(_records, Ct);

        Assert.Equal(1, archived.Archived);
        Assert.Empty(_records.Store.Current.Records);
        Assert.True(File.Exists(_directory.Combine("archive", "library-demo-archived.jsonl")));

        // An archive that can't be read makes the export a 503, never a backup without its archived records.
        using (new FileStream(_directory.Combine("archive", "library-demo-archived.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(PluginErrorKind.Unavailable, Assert.Throws<PluginException>(() => demo.Export(new DefaultHttpContext())).Kind);
        }

        var listed = Assert.Single((IEnumerable<IDictionary<string, object?>>)demo.DescribeRecords()["Archived"]!);
        Assert.Equal("Heat", listed["Name"]);
        var file = (FileContentResult)demo.Export(new DefaultHttpContext());
        using (var export = JsonDocument.Parse(file.FileContents))
        {
            // Its time is the server's clock's, as the other exports' (Bookmarks 1.2.1), in the file and its name.
            Assert.Equal(Monday.AddDays(2), export.RootElement.GetProperty("ExportedUtc").GetDateTime());
            Assert.Contains("-export-20261007", file.FileDownloadName, StringComparison.Ordinal);
            var entry = Assert.Single(export.RootElement.GetProperty("Archive").EnumerateArray());
            Assert.Equal("the heist", entry.GetProperty("Note").GetString());
            Assert.Equal(0, export.RootElement.GetProperty("Data").GetProperty("Records").GetArrayLength());
        }

        _library.Add(movie);
        var reattached = await _detached.ReattachFromArchiveAsync(_records, Ct);

        Assert.Equal(1, reattached.Relinked);
        var back = Assert.Single(_records.Store.Current.Records);
        Assert.Null(back.DetachedUtc);
        Assert.Equal("the heist", back.Note);
        Assert.Empty((IEnumerable<IDictionary<string, object?>>)demo.DescribeRecords()["Archived"]!);
    }

    [Fact]
    public async Task Purge_DeletesTheDetachedAndArchivedRecords_AfterASnapshot()
    {
        var demo = CreateDemo();
        var kept = _library.AddMovie("Kept", 2000, TimeSpan.FromMinutes(90));
        var gone = _library.AddMovie("Gone", 2000, TimeSpan.FromMinutes(90));
        await demo.AddRecordAsync(kept.Id, null, null, false, _admin, Ct);
        await demo.AddRecordAsync(gone.Id, null, null, false, _admin, Ct);
        _library.Remove(gone, raiseEvent: false);
        await demo.SweepAsync(Ct);

        var result = await demo.PurgeAsync(null, includeArchive: true, Ct);

        Assert.Equal(1, result["Purged"]);
        var snapshot = Assert.Single((IEnumerable<string>)result["Snapshots"]!);
        Assert.True(File.Exists(Path.Combine(_directory.Combine("snapshots", "pre-op"), snapshot)));
        Assert.Equal(kept.Id, Assert.Single(_records.Store.Current.Records).ItemId);
    }

    [Fact]
    public void ReadUserData_Cached_IsCoresBlankSnapshot_AndFresh_IsTheStoredRow()
    {
        // Found with User Ratings: core set the cached Series' snapshot to null after a child changed.
        var series = new Series { Id = Guid.NewGuid(), Name = "Test Series" };
        var row = new UserData
        {
            ItemId = series.Id,
            Item = null,
            UserId = _admin.Id,
            User = null,
            CustomDataKey = series.Id.ToString("N"),
            IsFavorite = true,
            Played = true,
            PlayCount = 3,
            PlaybackPositionTicks = 6_000_000_000
        };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(series.Id)).Returns(series);
        library.Setup(l => l.GetItemById<BaseItem>(series.Id, _admin)).Returns(series);
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([new Series { Id = series.Id, UserData = [row] }]);
        var userData = new Mock<IUserDataManager>();
        userData.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>())).Returns((User user, BaseItem item)
            => item.UserData?.Where(e => e.UserId == user.Id).Select(e => new UserItemData { Key = e.CustomDataKey, IsFavorite = e.IsFavorite, Played = e.Played, PlayCount = e.PlayCount, PlaybackPositionTicks = e.PlaybackPositionTicks }).FirstOrDefault()
                ?? new UserItemData { Key = "blank" });
        series.UserData = null;
        var demo = CreateDemo(library.Object, userData.Object);

        var cached = demo.ReadUserData(series.Id, null, "Cached", null, _admin);
        Assert.Equal((false, false, false, 0), ((bool)cached["HasSnapshot"]!, (bool)cached["IsFavorite"]!, (bool)cached["Played"]!, (int)cached["PlayCount"]!));

        var fresh = demo.ReadUserData(series.Id, null, null, null, _admin);
        Assert.Equal(("Query", false, true, true, 3, 6_000_000_000L), ((string)fresh["Source"]!, (bool)fresh["HadSnapshot"]!, (bool)fresh["IsFavorite"]!, (bool)fresh["Played"]!, (int)fresh["PlayCount"]!, (long)fresh["PlaybackPositionTicks"]!));

        // The fresh read repaired core's cached instance.
        Assert.Equal(true, demo.ReadUserData(series.Id, null, "Cached", null, _admin)["IsFavorite"]);
        Assert.Equal(PluginErrorKind.Invalid, Assert.Throws<PluginException>(() => demo.ReadUserData(series.Id, null, "Stale", null, _admin)).Kind);
        Assert.Equal(PluginErrorKind.NotFound, Assert.Throws<PluginException>(() => demo.ReadUserData(Guid.NewGuid(), null, null, null, _admin)).Kind);
    }

    [Fact]
    public void Libraries_AndVersionSets_TakeTheUsersView()
    {
        var kid = User("kid");
        _users.Setup(m => m.GetUserById(kid.Id)).Returns(kid);
        var movies = Library("Movies");
        var movies2 = Library("Movies 2");
        var primary = MovieIn(movies2, "Heat");
        var secondary = MovieIn(movies, "Heat 4K");
        secondary.SetPrimaryVersionId(primary.Id.ToString("N"));
        primary.LinkedAlternateVersions = [new LinkedChild { ItemId = secondary.Id }];
        _library.Hide(kid, primary);
        var demo = CreateDemo(access: new LibraryAccess(_library.Object, user => user == kid ? [movies] : [movies, movies2]));

        var forKid = demo.DescribeLibraries(kid.Id, null, _admin);
        Assert.Equal(new[] { movies.Id }, ((IEnumerable<Guid>)forKid["Resolved"]!));
        var query = (IDictionary<string, object?>)forKid["Query"]!;
        Assert.Equal((false, 1, 0), ((bool)query["GroupByPresentationUniqueKey"]!, (int)query["Movies"]!, (int)query["OwnedMovies"]!));
        Assert.Null(query["HasOwnerId"]);
        Assert.Null(demo.DescribeLibraries(kid.Id, [movies2.Id], _admin)["Query"]);

        var setsForKid = demo.DescribeVersionSets([secondary.Id], kid.Id, _admin);
        var setsForAdmin = demo.DescribeVersionSets([secondary.Id], null, _admin);
        var set = Assert.Single((IEnumerable<IDictionary<string, object?>>)setsForKid["Sets"]!);
        Assert.Equal(primary.Id, set["RootId"]);
        Assert.Equal(secondary.Id, Assert.Single((IEnumerable<IDictionary<string, object?>>)setsForKid["Representatives"]!)["Id"]);
        Assert.Equal(primary.Id, Assert.Single((IEnumerable<IDictionary<string, object?>>)setsForAdmin["Representatives"]!)["Id"]);
        Assert.Equal(PluginErrorKind.Invalid, Assert.Throws<PluginException>(() => demo.DescribeVersionSets([], null, _admin)).Kind);
    }

    [Fact]
    public void TheDeveloperClock_MovesTheTimeOn_WithinLimits_InDebugBuildsOnly()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.InRange(new DeveloperClock(() => 2).GetUtcNow() - now, TimeSpan.FromDays(2), TimeSpan.FromDays(2) + TimeSpan.FromMinutes(1));
        Assert.InRange(new DeveloperClock(() => -5).GetUtcNow() - now, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        Assert.InRange(new DeveloperClock(() => int.MaxValue).GetUtcNow() - now, TimeSpan.FromDays(DeveloperClock.MaxOffsetDays), TimeSpan.FromDays(DeveloperClock.MaxOffsetDays) + TimeSpan.FromMinutes(1));

        var services = new ServiceCollection().AddSingleton(TestData.ConfigurationProvider(new PluginConfiguration { DeveloperClockOffsetDays = 3 })).BuildServiceProvider();
        var clock = BookmarksKit.Options.Clock(services);
#if DEBUG
        Assert.True(DeveloperClock.IsHonoured);
        Assert.IsType<DeveloperClock>(clock);
#else
        Assert.False(DeveloperClock.IsHonoured);
        Assert.Same(TimeProvider.System, clock);
#endif
    }

    [Fact]
    public async Task TheMaintenanceTask_IsBookmarksAt0310_AndRunsTheKitsSteps()
    {
        var task = new BookmarksMaintenanceTask(new ServiceCollection().BuildServiceProvider());
        Assert.Equal(("Bookmarks: Maintenance", "BookmarksMaintenance", "Bookmarks"), (task.Name, task.Key, task.Category));
        var trigger = Assert.Single(task.GetDefaultTriggers());
        Assert.Equal((TaskTriggerInfoType.DailyTrigger, new TimeSpan(3, 10, 0).Ticks), (trigger.Type, trigger.TimeOfDayTicks));

        // With the registrator's services: every step runs, and with the demos off none fails.
        _host.SetupGet(h => h.ApplicationVersion).Returns(new Version(10, 11, 5));
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_directory.Path);
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, _host.Object);
        services.AddSingleton(_host.Object)
            .AddSingleton(paths.Object)
            .AddSingleton(_library.Object)
            .AddSingleton(_users.Object)
            .AddSingleton(Mock.Of<IUserDataManager>())
            .AddSingleton(TestData.ConfigurationProvider(new PluginConfiguration()))
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        await using var provider = services.BuildServiceProvider();

        await new BookmarksMaintenanceTask(provider).ExecuteAsync(new Progress<double>(), Ct);

        var run = (IDictionary<string, object?>)provider.GetRequiredService<KitMaintenance>().Describe()["LastRun"]!;
        var steps = (IEnumerable<IDictionary<string, object?>>)run["Steps"]!;
        Assert.True(Equals(0, run["Failed"]), string.Join("; ", steps.Select(s => string.Join(", ", s.Select(e => $"{e.Key}={e.Value}")))));
        Assert.Equal(
            new[] { "Snapshots", "Archive", "Users", "VerifySweep", "ResolveDetached", "Reattach", "ClipFiles", "MissingClips" },
            ((IEnumerable<IDictionary<string, object?>>)run["Steps"]!).Select(s => (string)s["Name"]!));

        // The missing-clip sweep, the last step, ran on the registrator's services, and Diagnostics' ClipSweep entry has
        // its run; ClipRoots describes the facade (not answering here: its hosted service didn't start).
        var sources = provider.GetServices<Kit.Diagnostics.IKitDiagnosticsSource>().ToDictionary(s => s.Key, StringComparer.Ordinal);
        var sweep = Assert.IsAssignableFrom<IDictionary<string, object?>>(sources["ClipSweep"].Describe());
        Assert.Equal((30, 0), ((int)sweep["RetentionDays"]!, (int)sweep["MarkedMissing"]!));
        Assert.Null(((IDictionary<string, object?>)sweep["LastRun"]!)["Skipped"]);
        var facade = Assert.IsAssignableFrom<IDictionary<string, object?>>(sources["ClipRoots"].Describe());
        Assert.Equal("Jellyfin.Plugin.Bookmarks.Integration.ClipRootsV1.Get", facade["Facade"]);

        // Every owner, the bookmarks' among them, could be created.
        var created = KitComponents.CreateAll<Kit.Library.IAttachedRecordOwner>(provider, NullLogger.Instance);
        Assert.True(created.Count == 2, string.Join("; ", KitComponents.Problems<Kit.Library.IAttachedRecordOwner>(provider).Select(p => $"{p["Name"]}: {p["Problem"]}")));
        var owners = KitComponents.CreateAll<Kit.Users.IUserDataOwner>(provider, NullLogger.Instance);
        Assert.True(owners.Count == 2, string.Join("; ", KitComponents.Problems<Kit.Users.IUserDataOwner>(provider).Select(p => $"{p["Name"]}: {p["Problem"]}")));
        Assert.Empty(Kit.Hosting.KitComponents.Problems<Kit.Library.IAttachedRecordOwner>(provider));
        Assert.Empty(Kit.Hosting.KitComponents.Problems<Kit.Users.IUserDataOwner>(provider));
    }

    private static User User(string name) => new(name, "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = Guid.NewGuid() };

    private CollectionFolder Library(string name)
        => _library.Add(new CollectionFolder { Name = name, CollectionType = CollectionType.movies, PhysicalFolderIds = [Guid.NewGuid()] });

    private Movie MovieIn(CollectionFolder library, string name)
    {
        var movie = _library.AddMovie(name, 1995, TimeSpan.FromMinutes(100));
        _library.SetTopParent(movie, library.PhysicalFolderIds[0]);
        return movie;
    }

    private LibraryChangeMonitor Monitor()
    {
        var monitor = new LibraryChangeMonitor(_host.Object, _library.Object, null, () => [_records], BookmarksKit.Options, NullLoggerFactory.Instance);
        _disposables.Add(monitor);
        monitor.Subscribe();
        return monitor;
    }

    private BookmarkLibraryDemo CreateDemo(ILibraryManager? library = null, IUserDataManager? userData = null, LibraryAccess? access = null)
    {
        library ??= _library.Object;
        var logger = new ListLogger();
        var configuration = TestData.ConfigurationProvider(_configuration);
        var journal = new MoveJournalReader(_directory.Combine("moves.jsonl"), logger);
        var resolver = new IdentityResolver(library, journal, _clock);
        _verifier = new AttachedRecordVerifier(_host.Object, library, journal, () => [_records], logger, _clock);
        _detached = new DetachedRecords(
            _directory.Combine("archive"),
            _directory.Combine("snapshots"),
            library,
            id => _users.Object.GetUserById(id),
            resolver,
            journal,
            () => [_records],
            logger,
            _clock);
        _records = new LibraryDemoRecords(configuration, library, () => _verifier, () => _detached, _directory.Combine("library-demo.json"), _directory.Combine("snapshots"), logger, _clock);
        _disposables.Add(_verifier);
        _disposables.Add(_records);
        return new BookmarkLibraryDemo(
            configuration,
            library,
            _users.Object,
            userData ?? Mock.Of<IUserDataManager>(),
            _records,
            _verifier,
            _detached,
            access ?? new LibraryAccess(library, _ => []),
            new FreshUserData(library, userData ?? Mock.Of<IUserDataManager>()),
            _host.Object,
            _clock);
    }
}
