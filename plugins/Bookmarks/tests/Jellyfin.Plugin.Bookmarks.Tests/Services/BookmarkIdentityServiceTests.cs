using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Import;
using Jellyfin.Plugin.Bookmarks.Integration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Services;

/// <summary>
/// The identity service's tests, on the real store, the kit's monitor, verify sweep and detached records,
/// and an in-memory library (<see cref="FakeLibrary"/>, whose <c>GetItemById(Guid.Empty)</c> throws as 10.11.5's does):
/// the backfill (runtime included), a removed video or series detaching its bookmarks, the Exact relink rewriting the media
/// source, the Strong relink within Bookmarks' runtime check clearing it and marking the bookmark, one of two cuts deleted
/// leaving its bookmarks detached and ambiguous, a JE import without a runtime taking one Strong candidate while an
/// upgraded 1.0 bookmark without one never does (live and archived), two candidates never applied, the
/// move journal (catch-up, the hook, a new journal id), adopted records never looked up, read-only data left alone,
/// retention and purge never touching a bookmark with a clip file, and no user-facing field changed. Not run in parallel
/// with other classes: the hook's target is process-wide, and every running identity service (the API tests' hosts start
/// one) makes itself the target.
/// </summary>
[Collection(ItemMovedHookCollection.Name)]
public sealed class BookmarkIdentityServiceTests : IDisposable
{
    private const string JournalId = "3f2a9c1e7b5d4e6f8a0b1c2d3e4f5a6b";
    private static readonly DateTime Monday = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Created = Monday.AddDays(-30);

    private readonly FakeLibrary _library = new();
    private readonly TempDirectory _directory = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(Monday));
    private readonly PluginConfiguration _configuration = new();
    private readonly ListLogger _logger = new();
    private readonly ListLogger<BackgroundWriteGate> _gateLogger = new();
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly List<IDisposable> _disposables = [];
    private InterferingBookmarkStore _store = null!;
    private AttachedRecordVerifier _verifier = null!;
    private DetachedRecords _detached = null!;
    private BookmarkIdentityService _service = null!;

    public BookmarkIdentityServiceTests()
    {
        _alice.Id = Guid.NewGuid();
        _host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        _users.Setup(m => m.GetUserById(_alice.Id)).Returns(_alice);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StoreFile => _directory.Combine("bookmarks", "bookmarks.json");

    private string JournalFile => _directory.Combine("moves.jsonl");

    public void Dispose()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }

        _directory.Dispose();
    }

    [Fact]
    public async Task Backfill_CompletesMigrated10Bookmarks_RuntimeYearPathAndSeries_OneLookupPerVideo_AndNoUserField()
    {
        // A 1.0 bookmark after the 1→2 upgrade has Kind, the display name as a provisional Name and its provider ids.
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), null, ("Tmdb", "949"));
        var series = _library.AddSeries("Test Series", null, ("Tvdb", "900401"));
        var season = _library.AddSeason(series, 1);
        var episode = _library.AddEpisode(series, season, 2, TimeSpan.FromMinutes(42));
        episode.ProductionYear = 2020;
        var gone = Guid.NewGuid();
        Create();
        var onMovie1 = await AddAsync(Migrated(movie.Id, "Movie", "Heat (1995)", ("Tmdb", "949")));
        var onMovie2 = await AddAsync(Migrated(movie.Id, "Movie", "Heat (1995)", ("Tmdb", "949")) with { Name = "Bank job", Note = "the reflection" });
        var onEpisode = await AddAsync(Migrated(episode.Id, "Episode", "Test Series S01E02 - Episode 2"));
        var onGone = await AddAsync(Migrated(gone, "Movie", "Gone (2001)"));
        var adopted = await AddAsync(Adopted());

        var result = await _service.BackfillAsync(Ct);

        Assert.Equal((3, 3, 1), (result.Checked, result.Updated, result.Missing));
        Assert.Equal(1, _store.DocumentWrites);
        foreach (var id in new[] { onMovie1.Id, onMovie2.Id })
        {
            var identity = _store.Get(id)!.Identity;
            Assert.Equal(("Heat", 1995, TimeSpan.FromMinutes(170).Ticks, movie.Path), (identity.Name, identity.Year, identity.RunTimeTicks, identity.Path));
            Assert.Equal("949", identity.ProviderIds["Tmdb"]);
        }

        var onSeries = _store.Get(onEpisode.Id)!.Identity;
        Assert.Equal((series.Id, season.Id, "Test Series", 1, 2), (onSeries.SeriesId!.Value, onSeries.SeasonId!.Value, onSeries.SeriesName, onSeries.Season!.Value, onSeries.Episode!.Value));
        Assert.Equal("900401", onSeries.SeriesProviderIds!["Tvdb"]);
        Assert.Equal(TimeSpan.FromMinutes(42).Ticks, onSeries.RunTimeTicks);

        // Nothing a user sees or edits changed: the name, the note, the display name, UpdatedAt (the IfUpdatedAt token).
        foreach (var before in new[] { onMovie1, onMovie2, onEpisode })
        {
            var after = _store.Get(before.Id)!;
            Assert.Equal((before.Name, before.Note, before.ItemName, before.UpdatedAt, before.MediaSourceId), (after.Name, after.Note, after.ItemName, after.UpdatedAt, after.MediaSourceId));
        }

        Assert.Same(onGone, _store.Get(onGone.Id));
        Assert.Same(adopted, _store.Get(adopted.Id));
        Assert.All(new[] { movie.Id, episode.Id, gone }, id => Assert.Single(_library.IdLookups, l => l == id));
        Assert.DoesNotContain(Guid.Empty, _library.IdLookups);

        // A second pass has nothing to complete but the video that isn't there: no write.
        var bytes = await File.ReadAllBytesAsync(StoreFile, Ct);
        var second = await _service.BackfillAsync(Ct);
        Assert.Equal((1, 0, 1), (second.Checked, second.Updated, second.Missing));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(StoreFile, Ct));
    }

    [Fact]
    public async Task Backfill_WritesOncePer100Videos()
    {
        Create();
        var records = new List<Bookmark>();
        for (var i = 0; i < 250; i++)
        {
            var movie = _library.AddMovie("Movie " + i, 2000 + (i % 20), TimeSpan.FromMinutes(90 + (i % 7)));
            records.Add(Migrated(movie.Id, "Movie", movie.Name));
        }

        await _store.UpdateDocumentAsync(d => BookmarkStore.WithBookmarks(d, records), Ct);
        var writesBefore = _store.DocumentWrites;

        var result = await _service.BackfillAsync(Ct);

        Assert.Equal((250, 250), (result.Checked, result.Updated));
        Assert.Equal(3, _store.DocumentWrites - writesBefore);
        Assert.All(_store.GetAll(), b => Assert.NotNull(b.Identity.RunTimeTicks));
        Assert.Equal(250, _library.IdLookups.Distinct().Count());
    }

    [Fact]
    public async Task ARemovedVideo_DetachesItsBookmarks_CompletedFromTheSnapshotTheHandlerCaptured_AndTheSeriesTakesItsEpisodes()
    {
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), null, ("Tmdb", "949"));
        var series = _library.AddSeries("Test Series", null, ("Tvdb", "900401"));
        var season = _library.AddSeason(series, 1);
        var episode = _library.AddEpisode(series, season, 1);
        Create();

        // Removed before the backfill looked it up: the monitor's capture gives the bookmark what its reattach needs.
        var migrated = await AddAsync(Migrated(movie.Id, "Movie", "Heat (1995)"));
        var onEpisode = await AddAsync(On(episode));
        var tombstone = await AddAsync(On(episode) with { DeletedUtc = Monday.AddDays(-1), Clip = ClipWithFile() });
        var monitor = Monitor();

        _library.Remove(movie);
        _library.Remove(series);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var detached = _store.Get(migrated.Id)!;
        Assert.Equal(Monday, detached.DetachedUtc);
        Assert.Equal((TimeSpan.FromMinutes(170).Ticks, movie.Path, 1995, "949"), (detached.Identity.RunTimeTicks, detached.Identity.Path, detached.Identity.Year, detached.Identity.ProviderIds["Tmdb"]));
        Assert.Equal(migrated.UpdatedAt, detached.UpdatedAt);
        Assert.Equal(Monday, _store.Get(onEpisode.Id)!.DetachedUtc);
        Assert.Equal(Monday, _store.Get(tombstone.Id)!.DetachedUtc);
        Assert.True(BookmarkDtoMapper.ToDto(detached, null).Detached);
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains("1 bookmarks detached whose video was removed from the library, 2 whose series or season was", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABookmarkAnotherConsumersSweepDetachedFirst_StillTakesTheCapturedSnapshot()
    {
        // Two consumers of one plugin share the verify sweep: when another one's batch swept first (the developer library demo,
        // on in a sandbox), the bookmark on the removed video is detached already when this consumer handles the removal.
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), null, ("Tmdb", "949"));
        Create();
        var migrated = await AddAsync(Migrated(movie.Id, "Movie", "Heat (1995)"));
        var monitor = Monitor();
        Assert.Equal(1, await _service.DetachAsync([movie.Id], Monday.AddMinutes(-1), Ct));

        _library.Remove(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var detached = _store.Get(migrated.Id)!;
        Assert.Equal(Monday.AddMinutes(-1), detached.DetachedUtc);
        Assert.Equal((TimeSpan.FromMinutes(170).Ticks, movie.Path, "949"), (detached.Identity.RunTimeTicks, detached.Identity.Path, detached.Identity.ProviderIds["Tmdb"]));
        Assert.Equal(migrated.UpdatedAt, detached.UpdatedAt);
    }

    [Fact]
    public async Task AVideoPutBack_ReattachesExact_AndAMovedFile_RewritesTheMediaSourceThatWasTheOldId()
    {
        // Exact: the same id (the file put back), then the same path under a new id (FindByPath).
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"C:\media\Movies\Heat (1995)\Heat (1995).mkv", ("Tmdb", "949"));
        Create();
        var bookmark = await AddAsync(On(movie));
        var monitor = Monitor();

        _library.Remove(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        _library.Add(movie);
        _library.RaiseAdded(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var back = _store.Get(bookmark.Id)!;
        Assert.Null(back.DetachedUtc);
        Assert.Equal((movie.Id, bookmark.MediaSourceId, bookmark.AudioStreamIndex), (back.ItemId, back.MediaSourceId, back.AudioStreamIndex));
        Assert.Null(back.RelinkedUtc);
        Assert.Equal(Monday, back.AttachedUtc);

        // The same path under another id (a library rebuilt, a type change): Exact by path.
        _library.Remove(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        var again = _library.Add(new Movie { Name = "Heat", ProductionYear = 1995, RunTimeTicks = movie.RunTimeTicks, Path = movie.Path, ProviderIds = { ["Tmdb"] = "949" } });
        _library.RaiseAdded(again);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var moved = _store.Get(bookmark.Id)!;
        Assert.Equal(again.Id, moved.ItemId);
        Assert.Equal(again.Id.ToString("N", CultureInfo.InvariantCulture), moved.MediaSourceId);
        Assert.Equal(2, moved.AudioStreamIndex);
        Assert.Null(moved.RelinkedUtc);
        Assert.Equal(again.Id, moved.Identity.ItemId);
        Assert.Equal(bookmark.UpdatedAt, moved.UpdatedAt);
    }

    [Fact]
    public async Task AVideoPutBackScrapedAsAnotherTitle_StillReattachesExact_ForABookmarkIsOnTheFile()
    {
        // RefuseConflictingExact is User Ratings' (a rating is of the title); Bookmarks keeps
        // it off, so the same file back under its own id with another provider id takes its bookmarks as before.
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"C:\media\Movies\Heat (1995)\Heat (1995).mkv", ("Tmdb", "949"));
        Create();
        Assert.False(_service.RelinkPolicy.RefuseConflictingExact);
        Assert.False(_service.ImportRelinkPolicy.RefuseConflictingExact);
        Assert.False(JellyfinEnhancedBookmarkImporter.MatchPolicy.RefuseConflictingExact);
        var bookmark = await AddAsync(On(movie));
        var monitor = Monitor();

        _library.Remove(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        Assert.NotNull(_store.Get(bookmark.Id)!.DetachedUtc);
        movie.ProviderIds["Tmdb"] = "900949";
        _library.Add(movie);
        _library.RaiseAdded(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var back = _store.Get(bookmark.Id)!;
        Assert.Null(back.DetachedUtc);
        Assert.Equal((movie.Id, bookmark.MediaSourceId, bookmark.AudioStreamIndex, (DateTime?)null), (back.ItemId, back.MediaSourceId, back.AudioStreamIndex, back.RelinkedUtc));
    }

    [Fact]
    public async Task AStrongMatchWithin2Percent_Relinks_ClearingTheMediaSourceAndTrack_AndMarksIt()
    {
        var movie = _library.AddMovie("Tagged Movie", 2012, TimeSpan.FromMinutes(100), null, ("Tmdb", "900001"));
        Create();
        var bookmark = await AddAsync(On(movie));
        var monitor = Monitor();

        _library.Remove(movie);
        var elsewhere = _library.AddMovie("Tagged Movie", 2012, TimeSpan.FromSeconds(6030), @"C:\media\Movies 2\Tagged Movie (2012).mkv", ("Tmdb", "900001"));
        _library.RaiseAdded(elsewhere);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var relinked = _store.Get(bookmark.Id)!;
        Assert.Equal(elsewhere.Id, relinked.ItemId);
        Assert.Null(relinked.DetachedUtc);
        Assert.Null(relinked.MediaSourceId);
        Assert.Null(relinked.AudioStreamIndex);
        Assert.Equal(Monday, relinked.RelinkedUtc);
        Assert.Equal(MatchStrength.Strong, relinked.RelinkStrength);
        Assert.Equal(elsewhere.Path, relinked.Identity.Path);
        Assert.Equal((bookmark.StartTicks, bookmark.EndTicks, bookmark.UpdatedAt), (relinked.StartTicks, relinked.EndTicks, relinked.UpdatedAt));
        Assert.True(BookmarkDtoMapper.ToDto(relinked, null).Relinked);
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains("1 of 1 relinked bookmarks went to another file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeletingOneOfTwoCuts_LeavesItsBookmarksDetached_AndAmbiguous_NeverOnTheOtherCut()
    {
        // The theatrical and extended cut share a TMDb id; their runtimes differ by more than 60 s.
        var theatrical = _library.AddMovie("Edition Movie", 2015, TimeSpan.FromMinutes(100), @"C:\media\Movies\Edition Movie (2015)\Theatrical.mkv", ("Tmdb", "900002"));
        var extended = _library.AddMovie("Edition Movie", 2015, TimeSpan.FromMinutes(112), @"C:\media\Movies\Edition Movie (2015)\Extended.mkv", ("Tmdb", "900002"));
        Create();
        var bookmark = await AddAsync(On(theatrical));
        var monitor = Monitor();

        _library.Remove(theatrical);
        _library.RaiseUpdated(extended);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var stays = _store.Get(bookmark.Id)!;
        Assert.Equal(theatrical.Id, stays.ItemId);
        Assert.Equal(Monday, stays.DetachedUtc);
        Assert.Equal(bookmark.MediaSourceId, stays.MediaSourceId);
        var owner = Owner(_detached.Describe());
        Assert.Equal((1, 1), (Convert.ToInt32(owner["Count"], CultureInfo.InvariantCulture), Convert.ToInt32(owner["Ambiguous"], CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task AJellyfinEnhancedImportWithoutARuntime_TakesTheOneStrongCandidate_ButNeverOneOfTwo()
    {
        // A JE import that never attached: no runtime stored, so any single Strong candidate passes.
        Create();
        var lone = await AddAsync(Detached(Guid.NewGuid(), "Lone Movie", ("Tmdb", "900003")));
        var twice = await AddAsync(Detached(Guid.NewGuid(), "Twin Movie", ("Tmdb", "900004")));
        var monitor = Monitor();

        var candidate = _library.AddMovie("Lone Movie", 2011, TimeSpan.FromMinutes(95), null, ("Tmdb", "900003"));
        var twin1 = _library.AddMovie("Twin Movie", 1999, TimeSpan.FromMinutes(95), @"C:\media\Movies\Twin Movie (1999).mkv", ("Tmdb", "900004"));
        var twin2 = _library.AddMovie("Twin Movie", 2005, TimeSpan.FromMinutes(95), @"C:\media\Movies 2\Twin Movie (2005).mkv", ("Tmdb", "900004"));
        foreach (var item in new BaseItem[] { candidate, twin1, twin2 })
        {
            _library.RaiseAdded(item);
        }

        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var relinked = _store.Get(lone.Id)!;
        Assert.Equal(candidate.Id, relinked.ItemId);
        Assert.Equal(MatchStrength.Strong, relinked.RelinkStrength);
        Assert.Equal(TimeSpan.FromMinutes(95).Ticks, relinked.Identity.RunTimeTicks);
        var ambiguous = _store.Get(twice.Id)!;
        Assert.NotNull(ambiguous.DetachedUtc);
        Assert.Equal(twice.ItemId, ambiguous.ItemId);
        Assert.Equal(1, Convert.ToInt32(Owner(_detached.Describe())["Ambiguous"], CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ABookmarkTheJellyfinEnhancedImportKeptDetached_AttachesWhenItsFilmComesIn_WithTheImportPolicy()
    {
        // What the importer writes for a JE bookmark whose film isn't in the library (Origin JellyfinEnhanced,
        // detached, JE's TMDb id, no runtime) gets the identity service's import relink policy, so the one Strong candidate
        // that comes in later takes it; two copies never do.
        Create();
        var configurations = _directory.Combine("config");
        var jeUser = Path.Combine(configurations, "Jellyfin.Plugin.JellyfinEnhanced", _alice.Id.ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(jeUser);
        await File.WriteAllTextAsync(
            Path.Combine(jeUser, "bookmark.json"),
            JsonSerializer.Serialize(new
            {
                Bookmarks = new Dictionary<string, object>
                {
                    ["Bm_1"] = new { ItemId = Guid.NewGuid().ToString("N"), TmdbId = "900005", MediaType = "movie", Name = "Later Film (2010)", Timestamp = 600.5, Label = "Arrives later" },
                    ["Bm_2"] = new { ItemId = Guid.NewGuid().ToString("N"), TmdbId = "900006", MediaType = "movie", Name = "Twin Film (2010)", Timestamp = 60, Label = "Twice" }
                }
            }),
            Ct);
        var paths = new Mock<MediaBrowser.Common.Configuration.IApplicationPaths>();
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(configurations);
        var reader = new Jellyfin.Plugin.Kit.Interop.JellyfinEnhancedReader(paths.Object, null, Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceCollection()), _logger, _clock);
        var journal = new MoveJournalReader(JournalFile, _logger);
        using var importer = new Jellyfin.Plugin.Bookmarks.Import.JellyfinEnhancedBookmarkImporter(
            _store, reader, _library.Object, _users.Object, new IdentityResolver(_library.Object, journal, _clock), journal, _directory.Combine("bookmarks", "snapshots"), _logger, _clock);
        var report = await importer.ImportAsync(false, null, Guid.NewGuid(), Ct);
        Assert.Equal(2, report.Users[0].Detached);
        var later = _store.GetAll().Single(b => b.Name == "Arrives later");
        var twice = _store.GetAll().Single(b => b.Name == "Twice");
        Assert.Equal((BookmarkOrigin.JellyfinEnhanced, (long?)null), (later.Origin, later.Identity.RunTimeTicks));
        Assert.Same(_service.ImportRelinkPolicy, _service.GetRelinkPolicy(Of(later)));
        var monitor = Monitor();

        var film = _library.AddMovie("Later Film", 2010, TimeSpan.FromMinutes(100), null, ("Tmdb", "900005"));
        var twin1 = _library.AddMovie("Twin Film", 2010, TimeSpan.FromMinutes(100), @"C:\media\Movies\Twin Film (2010).mkv", ("Tmdb", "900006"));
        var twin2 = _library.AddMovie("Twin Film", 2010, TimeSpan.FromMinutes(130), @"C:\media\Movies 2\Twin Film (2010).mkv", ("Tmdb", "900006"));
        foreach (var item in new BaseItem[] { film, twin1, twin2 })
        {
            _library.RaiseAdded(item);
        }

        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        var relinked = _store.Get(later.Id)!;
        Assert.Equal((film.Id, (DateTime?)null, MatchStrength.Strong), (relinked.ItemId, relinked.DetachedUtc, relinked.RelinkStrength!.Value));
        Assert.Equal(("Arrives later", TimeSpan.FromSeconds(600.5).Ticks), (relinked.Name, relinked.StartTicks));
        Assert.NotNull(_store.Get(twice.Id)!.DetachedUtc);
    }

    [Fact]
    public async Task AnUpgraded10BookmarkWhoseVideoWasGoneBeforeTheBackfill_NeverTakesTheOneCandidate_LiveOrArchived()
    {
        // Under 1.0 the theatrical cut was replaced by the extended one (a new file, so a new id), before the
        // 1→2 upgrade. The upgraded bookmark has no runtime (the backfill can't look its video up), so the one TMDb candidate
        // can't be checked: it stays "Not in library", ambiguous, on its old id with its media source and audio track. A JE
        // import of the same film, without a runtime too, takes it.
        var theatrical = Guid.NewGuid();
        Create();
        var migrated = await AddAsync(Migrated(theatrical, "Movie", "Edition Movie (2015)", ("Tmdb", "900002")));
        var imported = await AddAsync(Detached(Guid.NewGuid(), "Edition Movie", ("Tmdb", "900002")));
        var monitor = Monitor();

        Assert.Equal(1, (await _service.BackfillAsync(Ct)).Missing);
        Assert.Null(_store.Get(migrated.Id)!.Identity.RunTimeTicks);
        await _verifier.SweepAsync(VerifySweepReason.Startup, Ct);
        Assert.Equal(Monday, _store.Get(migrated.Id)!.DetachedUtc);

        var extended = _library.AddMovie("Edition Movie", 2015, TimeSpan.FromMinutes(112), @"C:\media\Movies\Edition Movie (2015)\Extended.mkv", ("Tmdb", "900002"));
        _library.RaiseAdded(extended);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        var daily = await _detached.ResolveDetachedAsync(_service, Ct);

        var stays = _store.Get(migrated.Id)!;
        Assert.Equal((theatrical, (DateTime?)Monday), (stays.ItemId, stays.DetachedUtc));
        Assert.Equal((migrated.MediaSourceId, migrated.AudioStreamIndex, migrated.UpdatedAt), (stays.MediaSourceId, stays.AudioStreamIndex, stays.UpdatedAt));
        Assert.Null(stays.RelinkedUtc);
        Assert.Null(stays.RelinkStrength);
        Assert.True(BookmarkDtoMapper.ToDto(stays, null).Detached);
        Assert.Equal((1, 0, 1), (daily.Checked, daily.Relinked, daily.Ambiguous));
        Assert.Equal(1, Convert.ToInt32(Owner(_detached.Describe())["Ambiguous"], CultureInfo.InvariantCulture));
        var je = _store.Get(imported.Id)!;
        Assert.Equal((extended.Id, (DateTime?)null, MatchStrength.Strong), (je.ItemId, je.DetachedUtc, je.RelinkStrength!.Value));

        // Archived, it is resolved by its archived copy's origin: still never onto the other cut.
        _configuration.DetachedRetentionDays = 1;
        _clock.Set(new DateTimeOffset(Monday.AddDays(2)));
        Assert.Equal(1, (await _detached.ArchiveDueAsync(_service, Ct)).Archived);
        var archived = await _detached.ReattachFromArchiveAsync(_service, Ct);
        Assert.Equal((1, 0, 1), (archived.Checked, archived.Relinked, archived.Ambiguous));
        Assert.Null(_store.Get(migrated.Id));

        // Its own file back at its path (the same id): Exact, the media source and audio track kept, nothing marked.
        _library.Add(new Movie { Id = theatrical, Name = "Edition Movie", ProductionYear = 2015, RunTimeTicks = TimeSpan.FromMinutes(100).Ticks, Path = @"C:\media\Movies\Edition Movie (2015)\Theatrical.mkv", ProviderIds = { ["Tmdb"] = "900002" } });
        Assert.Equal(1, (await _detached.ReattachFromArchiveAsync(_service, Ct)).Relinked);
        var back = _store.Get(migrated.Id)!;
        Assert.Equal((theatrical, (DateTime?)null, migrated.MediaSourceId, migrated.AudioStreamIndex), (back.ItemId, back.DetachedUtc, back.MediaSourceId, back.AudioStreamIndex));
        Assert.Null(back.RelinkedUtc);
        Assert.Equal(TimeSpan.FromMinutes(100).Ticks, back.Identity.RunTimeTicks);
    }

    [Fact]
    public async Task EveryRecordButAJellyfinEnhancedImport_NeedsAStoredRuntime_ForAStrongRelink()
    {
        // The policy per record, by origin; a record the store no longer holds gets the strict one.
        Create();
        var native = await AddAsync(Record(Guid.NewGuid(), "Movie"));
        var imported = await AddAsync(Record(Guid.NewGuid(), "Movie") with { Origin = BookmarkOrigin.JellyfinEnhanced });
        var archivedImport = new ArchivedRecord
        {
            Id = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
            Identity = imported.Identity,
            DetachedUtc = Monday,
            Record = JsonSerializer.SerializeToNode(imported, JsonStoreFormat.Options)!.AsObject()
        };
        var candidate = new Movie { Id = Guid.NewGuid(), Name = "Item", RunTimeTicks = TimeSpan.FromMinutes(95).Ticks };

        Assert.Same(_service.RelinkPolicy, _service.GetRelinkPolicy(Of(native)));
        Assert.Same(_service.ImportRelinkPolicy, _service.GetRelinkPolicy(Of(imported)));
        Assert.Same(_service.ImportRelinkPolicy, _service.GetRelinkPolicy(archivedImport));
        Assert.Same(_service.RelinkPolicy, _service.GetRelinkPolicy(Of(native with { Id = Guid.NewGuid() })));
        Assert.False(_service.RelinkPolicy.Accepts(native.Identity, candidate));
        Assert.True(_service.ImportRelinkPolicy.Accepts(imported.Identity, candidate));
    }

    [Fact]
    public async Task AUserEditBetweenTheResolutionAndItsWrite_Wins()
    {
        // ApplyResolutions changes only a record still detached as the resolver saw it (IDetachedRecordOwner).
        var movie = _library.AddMovie("Tagged Movie", 2012, TimeSpan.FromMinutes(100), null, ("Tmdb", "900001"));
        Create();
        var bookmark = await AddAsync(On(movie) with { DetachedUtc = Monday.AddDays(-1) });
        _library.Remove(movie, raiseEvent: false);
        var elsewhere = _library.AddMovie("Tagged Movie", 2012, TimeSpan.FromMinutes(100), @"C:\media\Movies 2\Tagged.mkv", ("Tmdb", "900001"));
        var resolution = new DetachedResolution(_service.GetDetached().Single(), new ResolveResult { Outcome = ResolveOutcome.Relink, Strength = MatchStrength.Strong, ItemId = elsewhere.Id, Item = elsewhere });

        // Meanwhile it was detached again (a later sweep): a different DetachedUtc.
        await _store.UpdateAsync(bookmark.Id, b => b with { DetachedUtc = Monday }, Ct);

        Assert.Equal(0, await _service.ApplyResolutionsAsync([resolution], Ct));
        Assert.Equal(movie.Id, _store.Get(bookmark.Id)!.ItemId);
    }

    [Fact]
    public async Task AdoptedRecords_AreNeverLookedUp_ByAnyPass()
    {
        // GetItemById(Guid.Empty) throws on 10.11.5 (FakeLibrary too); HasLibraryItem guards every pass.
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), null, ("Tmdb", "949"));
        Create();
        var adopted = await AddAsync(Adopted());
        var adoptedDetached = await AddAsync(Adopted() with { DetachedUtc = Monday.AddDays(-2) });
        await AddAsync(On(movie));
        var monitor = Monitor();

        await _service.BackfillAsync(Ct);
        _library.Remove(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        _library.Add(movie);
        _library.RaiseAdded(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        await _verifier.SweepAsync(VerifySweepReason.Daily, Ct);
        await _detached.ResolveDetachedAsync(_service, Ct);
        await _service.CatchUpAsync(Ct);

        Assert.Same(adopted, _store.Get(adopted.Id));
        Assert.Same(adoptedDetached, _store.Get(adoptedDetached.Id));
        Assert.DoesNotContain(Guid.Empty, _library.IdLookups);
        Assert.DoesNotContain(Guid.Empty, _library.UserLookups);
        Assert.DoesNotContain(_service.GetDetached(), r => r.Id == adoptedDetached.Id.ToString("D"));
        Assert.DoesNotContain(Guid.Empty, _service.GetAttachedItemIds());
    }

    [Fact]
    public async Task TheMoveJournal_MovesBookmarksAttachedBeforeItsLines_KeepsThePosition_AndANewJournalReplaysIt()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var series = Guid.NewGuid();
        var newSeries = Guid.NewGuid();
        Create();
        var moved = await AddAsync(Record(a, "Movie") with { AttachedUtc = Monday.AddDays(-10) });
        var twice = await AddAsync(Record(b, "Movie") with { AttachedUtc = Monday.AddDays(-10), MediaSourceId = "some-other-source" });
        var reused = await AddAsync(Record(a, "Movie") with { AttachedUtc = Monday.AddDays(-1) });
        var episode = await AddAsync(Record(Guid.NewGuid(), "Episode") with { AttachedUtc = Monday.AddDays(-10), Identity = new IdentitySnapshot { Kind = "Episode", Name = "Episode 1", SeriesId = series } });
        var detached = await AddAsync(Record(c, "Movie") with { AttachedUtc = null, DetachedUtc = Monday.AddDays(-3) });

        // a moved to x, then b to y and y on to z at the same time (chained within the catch-up), a series moved, c detached
        // when the journal moved it (the sweep saw its old id gone): relinked and attached.
        var x = Guid.NewGuid();
        var y = Guid.NewGuid();
        var z = Guid.NewGuid();
        var d = Guid.NewGuid();
        WriteJournal(
            JournalId,
            Line(JournalId, 1, a, x, Monday.AddDays(-5), newPath: @"Q:\Moved\A.mkv"),
            Line(JournalId, 2, b, y, Monday.AddDays(-4)),
            Line(JournalId, 3, y, z, Monday.AddDays(-4)),
            Line(JournalId, 4, series, newSeries, Monday.AddDays(-4), kind: "Series"),
            Line(JournalId, 5, c, d, Monday.AddDays(-2)));

        var result = await _service.CatchUpAsync(Ct);

        Assert.Equal((5, 3), (result.Checked, result.Relinked));
        var first = _store.Get(moved.Id)!;
        Assert.Equal((x, Monday.AddDays(-5), x.ToString("N", CultureInfo.InvariantCulture), @"Q:\Moved\A.mkv"), (first.ItemId, first.AttachedUtc!.Value, first.MediaSourceId, first.Identity.Path));
        Assert.Null(first.RelinkedUtc);
        var chained = _store.Get(twice.Id)!;
        Assert.Equal((z, "some-other-source"), (chained.ItemId, chained.MediaSourceId));
        Assert.Same(reused, _store.Get(reused.Id));
        Assert.Equal(newSeries, _store.Get(episode.Id)!.Identity.SeriesId);
        var back = _store.Get(detached.Id)!;
        Assert.Equal((d, (DateTime?)null), (back.ItemId, back.DetachedUtc));
        var document = _store.Documents.Current;
        Assert.Equal((Guid.Parse(JournalId), 5L), (document.MoveJournalId!.Value, document.LastMoveSeq));
        Assert.All(_store.GetAll(), r => Assert.Equal(Created, r.UpdatedAt));

        // Nothing new: nothing written.
        var bytes = await File.ReadAllBytesAsync(StoreFile, Ct);
        Assert.Equal(0, (await _service.CatchUpAsync(Ct)).Checked);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(StoreFile, Ct));

        // A new journal (Library Maintenance reinstalled) replays from the start: idempotent, the new position stored.
        const string NewJournal = "0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e";
        WriteJournal(NewJournal, Line(NewJournal, 1, a, x, Monday.AddDays(-5)), Line(NewJournal, 2, x, c, Monday.AddDays(-1).AddHours(1)));
        await _service.CatchUpAsync(Ct);
        Assert.Equal(c, _store.Get(moved.Id)!.ItemId);
        Assert.Same(reused, _store.Get(reused.Id));
        Assert.Equal((Guid.Parse(NewJournal), 2L), (_store.Documents.Current.MoveJournalId!.Value, _store.Documents.Current.LastMoveSeq));
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains("replayed from the start (NewJournalId)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheHook_CatchesUpWhileTheServiceRuns_AndDoesNothingOtherwise()
    {
        ItemMovedHookV1.OnItemsMoved("[]");
        var a = Guid.NewGuid();
        var x = Guid.NewGuid();
        Create();
        var bookmark = await AddAsync(Record(a, "Movie") with { AttachedUtc = Monday.AddDays(-10) });

        await _service.StartAsync(Ct);
        await WaitUntilAsync(() => (bool)_service.Describe()["StartupDone"]!);
        WriteJournal(JournalId, Line(JournalId, 1, a, x, Monday.AddDays(-1)));
        var calls = ItemMovedHookV1.Calls;

        ItemMovedHookV1.OnItemsMoved("""[{"OldId":"not read"}]""");

        await WaitUntilAsync(() => _store.Get(bookmark.Id)!.ItemId == x);
        Assert.Equal(calls + 1, ItemMovedHookV1.Calls);
        await _service.StopAsync(Ct);

        // Stopped: the hook has no target any more, and never throws.
        WriteJournal(JournalId, Line(JournalId, 1, a, x, Monday.AddDays(-1)), Line(JournalId, 2, x, a, Monday.AddHours(-1)));
        ItemMovedHookV1.OnItemsMoved(null!);
        await Task.Delay(100, Ct);
        Assert.Equal(x, _store.Get(bookmark.Id)!.ItemId);
    }

    [Fact]
    public async Task AtStartup_TheJournalComesBeforeTheBackfill()
    {
        // A bookmark whose video Library Maintenance moved while the server was off: relinked by the journal, then completed
        // from its new item.
        var moved = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170), @"Q:\Moved\Heat (1995).mkv", ("Tmdb", "949"));
        var old = Guid.NewGuid();
        Create();
        var bookmark = await AddAsync(Migrated(old, "Movie", "Heat (1995)") with { AttachedUtc = Monday.AddDays(-10) });
        WriteJournal(JournalId, Line(JournalId, 1, old, moved.Id, Monday.AddDays(-1)));

        await _service.StartAsync(Ct);
        await WaitUntilAsync(() => (bool)_service.Describe()["StartupDone"]!);
        await _service.StopAsync(Ct);

        var after = _store.Get(bookmark.Id)!;
        Assert.Equal(moved.Id, after.ItemId);
        Assert.Equal(("Heat", TimeSpan.FromMinutes(170).Ticks), (after.Identity.Name, after.Identity.RunTimeTicks));
        var described = _service.Describe();
        Assert.Equal(1, ((IDictionary<string, object?>)described["Backfill"]!)["Updated"]);
        Assert.Equal(1, ((IDictionary<string, object?>)described["CatchUp"]!)["Relinked"]);
    }

    [Fact]
    public async Task AnUpdatedVideoOrSeries_RefreshesTheSnapshotsOfTheBookmarksOnIt()
    {
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        var series = _library.AddSeries("Test Series");
        var season = _library.AddSeason(series, 1);
        var episode = _library.AddEpisode(series, season, 1);
        Create();
        var onMovie = await AddAsync(On(movie));
        var onEpisode = await AddAsync(On(episode));
        var monitor = Monitor();

        // The provider ids arrive with the first metadata refresh.
        movie.ProviderIds["Tmdb"] = "949";
        series.ProviderIds["Tvdb"] = "900401";
        _library.RaiseUpdated(movie);
        _library.RaiseUpdated(series);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);

        Assert.Equal("949", _store.Get(onMovie.Id)!.Identity.ProviderIds["Tmdb"]);
        Assert.Equal("900401", _store.Get(onEpisode.Id)!.Identity.SeriesProviderIds!["Tvdb"]);
        Assert.Equal(onMovie.UpdatedAt, _store.Get(onMovie.Id)!.UpdatedAt);
    }

    [Fact]
    public async Task RetentionAndPurge_NeverTouchABookmarkWithAClipFile()
    {
        // Bookmarks keep detached records for good by default; with a retention set, the archive takes those
        // without a clip file only, and purge deletes those only (a clip file needs its record).
        _configuration.DetachedRetentionDays = 1;
        Create();
        var plain = await AddAsync(Detached(Guid.NewGuid(), "Plain", ("Tmdb", "1")) with { DetachedUtc = Monday.AddDays(-3) });
        var withClip = await AddAsync(Detached(Guid.NewGuid(), "Clipped", ("Tmdb", "2")) with { DetachedUtc = Monday.AddDays(-3), Clip = ClipWithFile() });
        var tombstone = await AddAsync(Detached(Guid.NewGuid(), "Kept", ("Tmdb", "3")) with { DetachedUtc = Monday.AddDays(-3), DeletedUtc = Monday.AddDays(-3), Clip = ClipWithFile() });

        var archived = await _detached.ArchiveDueAsync(_service, Ct);

        Assert.Equal(1, archived.Archived);
        Assert.Null(_store.Get(plain.Id));
        Assert.NotNull(_store.Get(withClip.Id));
        var archivedRecord = Assert.Single(_detached.ArchiveOf(_service).Read().Records);
        Assert.Equal(plain.Id.ToString("D"), archivedRecord.Id);
        Assert.Equal("Plain", archivedRecord.Record.Deserialize<Bookmark>(JsonStoreFormat.Options)!.Identity.Name);

        var purge = await _detached.PurgeAsync(_service, null, includeArchive: true, Ct);

        Assert.Equal(1, purge.Purged);
        Assert.NotNull(_store.Get(withClip.Id));
        Assert.NotNull(_store.Get(tombstone.Id));
        Assert.Empty(_detached.ArchiveOf(_service).Read().Records);
        Assert.Contains(purge.Snapshots, s => s.Contains("purge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnArchivedBookmark_ComesBackAttached_WhenItsVideoIsFound()
    {
        _configuration.DetachedRetentionDays = 1;
        var gone = Guid.NewGuid();
        Create();
        var bookmark = await AddAsync(Detached(gone, "Tagged Movie", ("Tmdb", "900001")) with { DetachedUtc = Monday.AddDays(-3), Identity = new IdentitySnapshot { ItemId = gone, Kind = "Movie", Name = "Tagged Movie", RunTimeTicks = TimeSpan.FromMinutes(100).Ticks, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "900001" } } });
        await _detached.ArchiveDueAsync(_service, Ct);
        Assert.Null(_store.Get(bookmark.Id));

        var found = _library.AddMovie("Tagged Movie", 2012, TimeSpan.FromMinutes(100), null, ("Tmdb", "900001"));
        var result = await _detached.ReattachFromArchiveAsync(_service, Ct);

        Assert.Equal(1, result.Relinked);
        var back = _store.Get(bookmark.Id)!;
        Assert.Equal((found.Id, (DateTime?)null, MatchStrength.Strong), (back.ItemId, back.DetachedUtc, back.RelinkStrength!.Value));
        Assert.Equal((bookmark.Name, bookmark.StartTicks), (back.Name, back.StartTicks));
    }

    [Fact]
    public async Task ReadOnlyData_IsLeftAlone_ByEveryPass_AndTheFileNeverChanges()
    {
        // A newer schema's file. The kit leaves a read-only owner alone; the service's own passes ask the gate.
        Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
        var original = StoreFixtures.Read(StoreFixtures.Schema3);
        await File.WriteAllBytesAsync(StoreFile, original, Ct);
        WriteJournal(JournalId, Line(JournalId, 1, Guid.NewGuid(), Guid.NewGuid(), Monday));
        Create();
        var monitor = Monitor();
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));

        Assert.True(_service.IsReadOnly);
        Assert.Equal("ReadOnly", (await _service.BackfillAsync(Ct)).Skipped);
        Assert.Equal("ReadOnly", (await _service.CatchUpAsync(Ct)).Skipped);
        _library.RaiseAdded(movie);
        _library.Remove(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, Ct);
        Assert.Equal("ReadOnly", (await _verifier.SweepAsync(VerifySweepReason.Daily, Ct)).Owners.Single().Skipped);
        Assert.Equal(0, await _service.DetachAsync([Guid.NewGuid()], Monday, Ct));
        Assert.Empty(_service.GetDetached());
        Assert.Empty(_service.GetAttachedItemIds());

        Assert.Equal(original, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.Single(_gateLogger.Entries, e => e.Message.Contains("so IdentityService is idle", StringComparison.Ordinal));
    }

    [Fact]
    public void NeedsBackfill_AndComplete_FollowTheirRules()
    {
        var full = new Bookmark
        {
            ItemId = Guid.NewGuid(),
            Identity = new IdentitySnapshot { Kind = "Movie", Name = "Heat", Year = 1995, RunTimeTicks = 1, Path = @"C:\x.mkv" }
        };
        full = full with { Identity = full.Identity with { ItemId = full.ItemId } };
        Assert.False(BookmarkIdentityService.NeedsBackfill(full));
        Assert.True(BookmarkIdentityService.NeedsBackfill(full with { Identity = full.Identity with { Path = null } }));
        Assert.True(BookmarkIdentityService.NeedsBackfill(full with { Identity = full.Identity with { RunTimeTicks = null } }));
        Assert.True(BookmarkIdentityService.NeedsBackfill(full with { Identity = full.Identity with { ItemId = Guid.NewGuid() } }));
        Assert.True(BookmarkIdentityService.NeedsBackfill(full with { Identity = full.Identity with { Kind = "Episode" } }));

        var stored = full.Identity with { ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1" } };
        Assert.Same(stored, BookmarkIdentityService.Complete(stored, null));
        Assert.Same(stored, BookmarkIdentityService.Complete(stored, stored with { ItemId = Guid.NewGuid(), Year = 2000 }));
        Assert.Same(stored, BookmarkIdentityService.Complete(stored, new IdentitySnapshot { ItemId = stored.ItemId }));
        var completed = BookmarkIdentityService.Complete(stored, new IdentitySnapshot { ItemId = stored.ItemId, Year = 1996, SeriesName = "S" });
        Assert.Equal((1996, "S", "Heat", "1"), (completed.Year, completed.SeriesName, completed.Name, completed.ProviderIds["Tmdb"]));
    }

    private static IDictionary<string, object?> Owner(IDictionary<string, object?> described)
        => ((IEnumerable<IDictionary<string, object?>>)described["Owners"]!).Single(o => (string?)o["Owner"] == BookmarkIdentityService.StoreName);

    private static DetachedRecord Of(Bookmark bookmark) => new()
    {
        Id = bookmark.Id.ToString("D", CultureInfo.InvariantCulture),
        Identity = bookmark.Identity,
        DetachedUtc = Monday
    };

    private static ClipInfo ClipWithFile() => new()
    {
        Status = BookmarkClipStatus.Completed,
        Mode = BookmarkClipMode.Fast,
        StartTicks = 0,
        EndTicks = 100_000_000,
        FilePath = @"C:\clips\alice\kept.mkv",
        FileSize = 1000,
        RequestedAt = Created,
        CompletedAt = Created
    };

    private static string Line(string journal, long seq, Guid oldId, Guid newId, DateTime utc, string kind = "Movie", string? newPath = null)
    {
        var line = new Dictionary<string, object>
        {
            ["V"] = 1,
            ["JournalId"] = journal,
            ["Seq"] = seq,
            ["Status"] = "Committed",
            ["Utc"] = utc.ToString("O", CultureInfo.InvariantCulture),
            ["OldId"] = oldId.ToString("N", CultureInfo.InvariantCulture),
            ["NewId"] = newId.ToString("N", CultureInfo.InvariantCulture),
            ["Kind"] = kind
        };
        if (newPath is not null)
        {
            line["NewPath"] = newPath;
        }

        return JsonSerializer.Serialize(line);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(20, Ct);
        }
    }

    private void WriteJournal(string journal, params string[] lines)
        => File.WriteAllLines(JournalFile, [$$"""{"V":1,"Header":true,"JournalId":"{{journal}}","CreatedUtc":"2026-09-01T00:00:00Z"}""", .. lines]);

    private Bookmark Record(Guid itemId, string kind) => new()
    {
        Id = Guid.NewGuid(),
        UserId = _alice.Id,
        ItemId = itemId,
        MediaSourceId = itemId.ToString("N", CultureInfo.InvariantCulture),
        AudioStreamIndex = 2,
        StartTicks = 600_000_000,
        EndTicks = 900_000_000,
        ItemName = "Item",
        Identity = new IdentitySnapshot { ItemId = itemId, Kind = kind, Name = "Item" },
        Origin = BookmarkOrigin.Native,
        AttachedUtc = Created,
        CreatedAt = Created,
        UpdatedAt = Created
    };

    private Bookmark On(BaseItem item) => Record(item.Id, ItemIdentity.KindOf(item)) with
    {
        ItemName = item.Name,
        Identity = ItemIdentity.Capture(item, _library.Object)
    };

    private Bookmark Migrated(Guid itemId, string kind, string displayName, params (string Provider, string Value)[] ids)
        => Record(itemId, kind) with
        {
            ItemName = displayName,
            Identity = new IdentitySnapshot { ItemId = itemId, Kind = kind, Name = displayName, ProviderIds = ids.ToDictionary(i => i.Provider, i => i.Value) }
        };

    private Bookmark Detached(Guid itemId, string name, params (string Provider, string Value)[] ids)
        => Record(itemId, "Movie") with
        {
            Origin = BookmarkOrigin.JellyfinEnhanced,
            Name = name,
            ItemName = name,
            MediaSourceId = null,
            AudioStreamIndex = null,
            AttachedUtc = null,
            DetachedUtc = Monday.AddDays(-1),
            Identity = new IdentitySnapshot { ItemId = itemId, Kind = "Movie", Name = name, ProviderIds = ids.ToDictionary(i => i.Provider, i => i.Value) }
        };

    private Bookmark Adopted() => Record(Guid.Empty, "Video") with
    {
        Origin = BookmarkOrigin.Adopted,
        MediaSourceId = null,
        Identity = new IdentitySnapshot { ItemId = Guid.Empty, Kind = "Video", Name = "clip" },
        Clip = ClipWithFile()
    };

    private async Task<Bookmark> AddAsync(Bookmark bookmark)
    {
        await _store.AddAsync(bookmark, Ct);
        return _store.Get(bookmark.Id)!;
    }

    private LibraryChangeMonitor Monitor()
    {
        var monitor = new LibraryChangeMonitor(_host.Object, _library.Object, null, () => [_service], BookmarksKit.Options, NullLoggerFactory.Instance, _clock);
        _disposables.Add(monitor);
        monitor.Subscribe();
        return monitor;
    }

    private void Create()
    {
        var inner = BookmarkStore.Create(StoreFile, _logger, _clock);
        _disposables.Add(inner);
        _store = new InterferingBookmarkStore(inner);
        var journal = new MoveJournalReader(JournalFile, _logger);
        var resolver = new IdentityResolver(_library.Object, journal, _clock);
        _verifier = new AttachedRecordVerifier(_host.Object, _library.Object, journal, () => [_service], _logger, _clock);
        _detached = new DetachedRecords(
            _directory.Combine("bookmarks", "archive"),
            _directory.Combine("bookmarks", "snapshots"),
            _library.Object,
            id => _users.Object.GetUserById(id),
            resolver,
            journal,
            () => [_service],
            _logger,
            _clock);
        _service = new BookmarkIdentityService(
            _host.Object,
            _store,
            new BackgroundWriteGate(_store, _gateLogger),
            _library.Object,
            journal,
            () => _verifier,
            () => _detached,
            TestData.ConfigurationProvider(_configuration),
            _directory.Combine("bookmarks", "snapshots"),
            _logger,
            _clock);
        _disposables.Add(_verifier);
        _disposables.Add(_service);
    }
}

/// <summary>
/// The tests that start an identity service, whose <see cref="ItemMovedHookV1"/> target is process-wide: never in parallel
/// with other test classes.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ItemMovedHookCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "ItemMovedHookV1 target";
}
