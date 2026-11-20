using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The clip artwork (since 1.3.0 / 2.1.0): on real files in temporary clip folders, a clip's video's images are copied
/// beside the clip file as <c>&lt;clip&gt;-poster</c>, <c>-fanart</c> and (an episode's) <c>-thumb</c>, only local image files
/// that are there, nothing with the setting off; the artwork follows a rename and goes with a delete; the daily clean-up
/// deletes only orphaned artwork named after a Bookmarks clip, never in a clip folder that is offline or not recognised; and
/// a failure never fails the clip.
/// </summary>
public sealed class ClipArtworkTests : IDisposable
{
    private const string ClipName = "Bank job - Heat (1995) [01.52.10-01.55.00].mp4";
    private const string Stem = "Bank job - Heat (1995) [01.52.10-01.55.00]";

    private readonly TempDirectory _directory = new();
    private readonly string _root;
    private readonly string _alice;
    private readonly Guid _movieId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly string _poster;
    private readonly string _backdrop;
    private readonly FakeClipArtworkSource _source = new();
    private readonly FakeClipFileSystem _fileSystem = new();
    private readonly ListLogger _logger = new();
    private readonly PluginConfiguration _configuration = new();
    private readonly BookmarkStore _store;
    private readonly ClipFileOps _files;
    private readonly ClipArtwork _artwork;

    public ClipArtworkTests()
    {
        _root = _directory.Combine("clips");
        _alice = Path.Combine(_root, "alice");
        Directory.CreateDirectory(_alice);
        ClipFileSupport.MarkClipFolder(_root);
        _store = ClipFileSupport.StoreWithClipRoots(_directory.Combine("data", "bookmarks.json"), _root);
        _poster = _directory.CreateFile(Path.Combine("metadata", "movie", "poster.jpg"), "poster image");
        _backdrop = _directory.CreateFile(Path.Combine("metadata", "movie", "backdrop0.png"), "backdrop image");
        _source.Items[_movieId] = new ClipArtworkImages(_poster, _backdrop, null);
        _files = ClipFileSupport.Ops(_store, fileSystem: _fileSystem);
        _artwork = new ClipArtwork(_store, _files, _source, TestData.ConfigurationProvider(_configuration), _logger);
        _files.Artwork = () => _artwork;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _artwork.Dispose();
        _store.Dispose();
        _directory.Dispose();
    }

    [Theory]
    [InlineData("Bank job - Heat (1995) [01.52.10-01.55.00]-poster.jpg", "Bank job - Heat (1995) [01.52.10-01.55.00]", "poster", ".jpg", false)]
    [InlineData("Heat (1995) [01.52.10-01.55.00] (2)-fanart.webp", "Heat (1995) [01.52.10-01.55.00] (2)", "fanart", ".webp", false)]
    [InlineData("Clip [100.00.00-100.00.10] (0123456789abcdef0123456789abcdef)-thumb.jpeg", "Clip [100.00.00-100.00.10] (0123456789abcdef0123456789abcdef)", "thumb", ".jpeg", false)]
    [InlineData("Heat [00.00.01-00.00.02]-poster.png.0123456789abcdef0123456789abcdef.tmp", "Heat [00.00.01-00.00.02]", "poster", ".png", true)]
    public void ArtworkNames_OfBookmarksClips_AreRead(string name, string clip, string kind, string extension, bool temporary)
        => Assert.Equal(new ClipArtworkName(clip, kind, extension, temporary), ClipArtworkNames.Parse(name));

    [Theory]
    [InlineData("Holiday-poster.jpg")]                                        // a user's own: no range
    [InlineData("Heat [01.52.10-01.55.00]-poster.gif")]                       // a format Bookmarks doesn't write
    [InlineData("Heat [01.52.10-01.55.00]-poster.JPG")]                       // Bookmarks writes lower case
    [InlineData("Heat [01.52.10-01.55.00]-banner.jpg")]
    [InlineData("Heat [01.52.10-01.55.00].jpg")]
    [InlineData("Heat [01.52.10-01.55.00]-poster.jpg.tmp")]
    [InlineData("Heat [01.52.10-01.55.00] (x)-poster.jpg")]
    [InlineData("Heat [1.52.10-01.55.00]-poster.jpg")]
    [InlineData("Heat [01.52.10-01.55.00]-poster.jpg\n")]
    [InlineData("alice/Heat [01.52.10-01.55.00]-poster.jpg")]
    public void OtherNames_AreNotArtwork(string name) => Assert.Null(ClipArtworkNames.Parse(name));

    [Fact]
    public async Task Sync_WritesThePosterAndTheFanart_InTheImagesOwnFormats_AndRefreshesALibraryClip()
    {
        var clip = await AddClipAsync(ClipName);
        _source.LibraryClips.Add(clip.Clip!.FilePath!);

        var run = await _artwork.SyncAsync(clip.Id, Ct);

        Assert.Equal("poster image", Text(Art("poster", ".jpg")));
        Assert.Equal("backdrop image", Text(Art("fanart", ".png")));
        Assert.Equal(File.GetLastWriteTimeUtc(_poster), File.GetLastWriteTimeUtc(Art("poster", ".jpg")));
        Assert.False(File.Exists(Art("thumb", ".jpg")));
        Assert.Equal([Stem + "-fanart.png", Stem + "-poster.jpg", ClipName], Directory.GetFiles(_alice).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal((2, 1, 1), (run.Written, run.ClipsWritten, run.Refreshed));
        Assert.Equal([clip.Clip.FilePath!], _source.Refreshed);

        // Again: up to date, nothing written, no refresh.
        var again = await _artwork.SyncAsync(clip.Id, Ct);
        Assert.Equal((0, 2, 0), (again.Written, again.UpToDate, again.Refreshed));
        Assert.Single(_source.Refreshed);
    }

    [Fact]
    public async Task Sync_OfAnEpisode_WritesItsThumbToo_AndReplacesOnlyItsOwnArtwork_WhenTheImageChanges()
    {
        var episodeId = Guid.NewGuid();
        var series = _directory.CreateFile(Path.Combine("metadata", "series", "poster.webp"), "series poster");
        var still = _directory.CreateFile(Path.Combine("metadata", "episode", "still.jpg"), "episode still");
        _source.Items[episodeId] = new ClipArtworkImages(series, _backdrop, still);
        var clip = await AddClipAsync(ClipName, itemId: episodeId);
        Write(Art("poster", ".jpg"), "an older poster in another format");
        Write(Art("poster", ".webp"), "an older poster");

        await _artwork.SyncAsync(clip.Id, Ct);

        Assert.Equal("series poster", Text(Art("poster", ".webp")));
        Assert.Equal("episode still", Text(Art("thumb", ".jpg")));
        Assert.Equal("backdrop image", Text(Art("fanart", ".png")));
        Assert.False(File.Exists(Art("poster", ".jpg")));
        Assert.Empty(Directory.GetFiles(_alice, "*.tmp"));
    }

    [Fact]
    public async Task Sync_SkipsImagesThatArentThere_FormatsNotCopied_ClipsNotFinished_AndVideosNotInTheLibrary()
    {
        var gone = Guid.NewGuid();
        _source.Items[gone] = new ClipArtworkImages(_directory.Combine("metadata", "cleared.jpg"), _directory.CreateFile(Path.Combine("metadata", "anim.gif")), null);
        var missingImages = await AddClipAsync("Heat [00.00.01-00.00.02].mp4", itemId: gone);
        var notInLibrary = await AddClipAsync("Heat [00.00.03-00.00.04].mp4", itemId: Guid.NewGuid());
        var failed = await AddClipAsync("Heat [00.00.05-00.00.06].mp4", status: BookmarkClipStatus.Failed);
        var oldName = await AddClipAsync("Heat 1995 clip.mp4");
        var deeper = await AddClipAsync(ClipName, folder: Path.Combine(_alice, "deeper"));

        foreach (var clip in new[] { missingImages, notInLibrary, failed, oldName, deeper })
        {
            var run = await _artwork.SyncAsync(clip.Id, Ct);
            Assert.Equal(0, run.Written);
        }

        Assert.DoesNotContain(Directory.GetFiles(_root, "*", SearchOption.AllDirectories), f => ClipArtworkNames.Parse(Path.GetFileName(f)) is not null);
        Assert.Empty(_source.Refreshed);
    }

    [Fact]
    public async Task WithTheSettingOff_NothingIsWritten()
    {
        _configuration.GiveClipsVideoArtwork = false;
        var clip = await AddClipAsync(ClipName);

        await _artwork.SyncAsync(clip.Id, Ct);
        var run = await _artwork.SyncAllAsync(Ct);

        Assert.Equal([ClipName], Directory.GetFiles(_alice).Select(Path.GetFileName));
        Assert.Equal("GiveClipsVideoArtwork is off", run.Skipped);
    }

    [Fact]
    public async Task AnArtworkPathThatIsALinkOrAFolder_IsLeftAsItIs()
    {
        var clip = await AddClipAsync(ClipName);
        Directory.CreateDirectory(Art("poster", ".jpg"));
        _fileSystem.ReparsePoints.Add(Art("fanart", ".png"));

        await _artwork.SyncAsync(clip.Id, Ct);

        Assert.True(Directory.Exists(Art("poster", ".jpg")));
        Assert.False(File.Exists(Art("fanart", ".png")));
    }

    [Fact]
    public async Task ARename_MovesTheArtworkToTheNewName_AndLeavesNoneUnderTheOld()
    {
        var user = TestData.CreateUser();
        var clip = await AddClipAsync(ClipName, userId: user.Id);
        await _artwork.SyncAsync(clip.Id, Ct);

        var renamed = await new ClipFileRenamer(_store, _files, NullLogger.Instance).RenameAsync(user, clip.Id, changeName: true, "Vault", renameFile: true, ifUpdatedAt: null, Ct);

        const string NewStem = "Vault - Heat (1995) [01.52.10-01.55.00]";
        Assert.Equal(Path.Combine(_alice, NewStem + ".mp4"), renamed.Clip!.FilePath);
        Assert.Equal(
            [NewStem + "-fanart.png", NewStem + "-poster.jpg", NewStem + ".mp4"],
            Directory.GetFiles(_alice).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("poster image", Text(Path.Combine(_alice, NewStem + "-poster.jpg")));
    }

    [Fact]
    public async Task ADelete_DeletesTheClipsArtwork_ButNotAnotherClipsOrAUsersOwnImages()
    {
        var clip = await AddClipAsync(ClipName);
        var other = await AddClipAsync("Heat (1995) [00.10.00-00.11.00].mkv");
        await _artwork.SyncAsync(clip.Id, Ct);
        await _artwork.SyncAsync(other.Id, Ct);
        var own = _directory.CreateFile(Path.Combine("clips", "alice", "Holiday-poster.jpg"));

        await ClipFileSingleDelete.RunAsync(_store, _files, clip.Id, onlyIfMissing: false, requireRecognisedFolder: true, _ => true, null, _ => null, null, forAdmin: false, null, Ct);

        Assert.Equal(
            ["Heat (1995) [00.10.00-00.11.00]-fanart.png", "Heat (1995) [00.10.00-00.11.00]-poster.jpg", "Heat (1995) [00.10.00-00.11.00].mkv", "Holiday-poster.jpg"],
            Directory.GetFiles(_alice).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(File.Exists(own));
    }

    [Fact]
    public async Task ADelete_LeavesAnImageWhoseNameDiffersOnlyInCase()
    {
        var clip = await AddClipAsync(ClipName);
        var upper = _directory.CreateFile(Path.Combine("clips", "alice", Stem + "-poster.JPG"), "the user's");

        await _artwork.SyncAsync(clip.Id, Ct);
        Assert.Equal("the user's", Text(upper));
        await ClipFileSingleDelete.RunAsync(_store, _files, clip.Id, onlyIfMissing: false, requireRecognisedFolder: true, _ => true, null, _ => null, null, forAdmin: false, null, Ct);

        Assert.True(File.Exists(upper));
        Assert.False(File.Exists(Art("fanart", ".png")));
    }

    [Fact]
    public async Task ABulkDelete_DeletesTheArtworkOfEveryClipItLetsGo()
    {
        var first = await AddClipAsync(ClipName);
        var second = await AddClipAsync("Heat (1995) [00.10.00-00.11.00].mkv");
        await _artwork.SyncAllAsync(Ct);
        Assert.Equal(6, Directory.GetFiles(_alice).Length);

        await ClipFileBatchDelete.RunAsync(_store, _files, [first.Id, second.Id], _ => true, _ => null, _ => { }, null, requireRecognisedFolder: true, Ct);

        Assert.Empty(Directory.GetFiles(_alice));
    }

    [Fact]
    public async Task TheDailyCleanUp_DeletesOnlyOrphanedArtworkOfBookmarksClips()
    {
        await AddClipAsync(ClipName);
        await AddClipAsync("Heat (1995) [00.20.00-00.21.00].mp4", createFile: false);                                          // its file is gone
        string[] deleted =
        [
            _directory.CreateFile(Path.Combine("clips", "alice", "Gone [00.01.00-00.02.00]-poster.jpg")),
            _directory.CreateFile(Path.Combine("clips", "alice", "Gone [00.01.00-00.02.00] (2)-fanart.webp")),
            _directory.CreateFile(Path.Combine("clips", "Gone [00.01.00-00.02.00]-thumb.jpeg")),                                   // the clip folder itself
            _directory.CreateFile(Path.Combine("clips", "alice", Stem + "-poster.png.0123456789abcdef0123456789abcdef.tmp"))      // a copy a crash left
        ];
        string[] untouched =
        [
            _directory.CreateFile(Path.Combine("clips", "alice", "Holiday-poster.jpg")),                                         // a user's own
            _directory.CreateFile(Path.Combine("clips", "alice", "Gone [00.01.00-00.02.00]-poster.gif")),
            _directory.CreateFile(Path.Combine("clips", "alice", "Gone [00.05.00-00.06.00]-poster.JPG")),                       // Bookmarks writes lower case
            _directory.CreateFile(Path.Combine("clips", "alice", "Gone [00.01.00-00.02.00]-banner.jpg")),
            _directory.CreateFile(Path.Combine("clips", "alice", "Gone [00.01.00-00.02.00].jpg")),
            _directory.CreateFile(Path.Combine("clips", "alice", "Kept [00.03.00-00.04.00]-poster.jpg")),                         // its video is there
            _directory.CreateFile(Path.Combine("clips", "alice", "Kept [00.03.00-00.04.00].mkv")),
            _directory.CreateFile(Path.Combine("clips", "alice", "Heat (1995) [00.20.00-00.21.00]-poster.jpg")),                 // a record names it
            _directory.CreateFile(Path.Combine("clips", "alice", "deeper", "Gone [00.01.00-00.02.00]-poster.jpg")),              // two levels down
            _directory.CreateFile(Path.Combine("elsewhere", "Gone [00.01.00-00.02.00]-poster.jpg"))                              // no clip folder
        ];

        var run = await _artwork.SyncAllAsync(Ct);

        Assert.All(deleted, f => Assert.False(File.Exists(f), f));
        Assert.All(untouched, f => Assert.True(File.Exists(f), f));
        Assert.True(File.Exists(Art("poster", ".jpg")));
        Assert.True(File.Exists(Art("fanart", ".png")));
        Assert.Equal((4, 2, 1), (run.Deleted, run.Written, run.ClipsWritten));
        Assert.DoesNotContain(_logger.Entries.Where(e => e.Level >= LogLevel.Information), e => e.Message.Contains("Gone", StringComparison.Ordinal) || e.Message.Contains("Heat", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AClipFolderOfflineOrNotRecognised_IsLeftAsItIs()
    {
        var orphan = _directory.CreateFile(Path.Combine("clips", "alice", "Gone [00.01.00-00.02.00]-poster.jpg"));

        // Not recognised: its marker file is gone (a folder in its place, a disk not mounted).
        ClipFileSupport.UnmarkClipFolder(_root);
        var unrecognised = await _artwork.SyncAllAsync(Ct);
        Assert.True(File.Exists(orphan));
        Assert.Equal(1, unrecognised.FoldersLeftOut);

        // Not there: an unplugged drive.
        ClipFileSupport.Unplug(_root);
        var offline = await _artwork.SyncAllAsync(Ct);
        await _artwork.CleanUpAsync([Path.Combine(_alice, "Gone [00.01.00-00.02.00].mp4")], Ct);
        ClipFileSupport.PlugIn(_root);
        Assert.True(File.Exists(orphan));
        Assert.Equal((1, 0), (offline.FoldersLeftOut, offline.Deleted));
    }

    [Fact]
    public async Task AFileThatCantBeDeleted_IsAWarning_AndTheRestGoOn()
    {
        var held = _directory.CreateFile(Path.Combine("clips", "alice", "Gone [00.01.00-00.02.00]-poster.jpg"));
        var other = _directory.CreateFile(Path.Combine("clips", "alice", "Gone [00.01.00-00.02.00]-fanart.jpg"));
        _fileSystem.HoldOpen(held);

        var run = await _artwork.SyncAllAsync(Ct);

        Assert.True(File.Exists(held));
        Assert.False(File.Exists(other));
        Assert.Equal((1, 1), (run.Deleted, run.NotDeleted));
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.Contains("orphaned clip artwork", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALookUpThatThrows_IsAWarning_AndNeverFailsTheCaller()
    {
        var clip = await AddClipAsync(ClipName);
        _source.Failure = new InvalidOperationException("the library is busy");

        var run = await _artwork.SyncAsync(clip.Id, Ct);
        await _files.ArtworkForClipAsync(clip.Id);
        await _files.ArtworkAfterRenameAsync(clip.Id, clip.Clip!.FilePath!, clip.Clip.FilePath!);

        Assert.Equal(1, run.Failed);
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.Contains(clip.Id.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Entries.Where(e => e.Level >= LogLevel.Information), e => e.Message.Contains("Bank job", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheClipWorker_GivesANewClipItsArtwork_AndAnArtworkFailureNeverFailsTheClip()
    {
        using var data = new TempDirectory();
        using var store = BookmarkStore.Create(data.Combine("bookmarks.json"), NullLogger.Instance);
        var planner = new FakeClipPlanner(data) { NamedFiles = true };
        var runner = new FakeFfmpegRunner();
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var files = ClipFileSupport.Ops(store);
        var source = new FakeClipArtworkSource();
        using var artwork = new ClipArtwork(store, files, source, TestData.ConfigurationProvider(), NullLogger.Instance);
        files.Artwork = () => artwork;
        using var service = new ClipExtractionService(
            store,
            new BackgroundWriteGate(store, NullLogger<BackgroundWriteGate>.Instance),
            planner,
            runner,
            files,
            ClipFileSupport.Janitor(store, files),
            ClipFileSupport.Guard(store),
            TestData.ConfigurationProvider(),
            host.Object,
            NullLogger<ClipExtractionService>.Instance);
        var good = TestData.CreateBookmark(_userId, itemId: _movieId);
        var bad = TestData.CreateBookmark(_userId, startSeconds: 200, endSeconds: 230);
        source.Items[good.ItemId] = new ClipArtworkImages(_poster, null, null);
        await store.AddAsync(good, Ct);
        await store.AddAsync(bad, Ct);
        await service.StartAsync(Ct);
        try
        {
            await service.EnqueueAsync(good.Id, good.UserId, BookmarkClipMode.Accurate, null, Ct);
            var made = await WaitForCompletedAsync(store, good.Id);
            // Written once the clip is in place and recorded.
            var poster = Path.ChangeExtension(made.Clip!.FilePath!, null) + "-poster.jpg";
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (!File.Exists(poster) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, Ct);
            }

            Assert.Equal("poster image", Text(poster));

            source.Failure = new InvalidOperationException("the library is busy");
            var lookups = source.Lookups;
            await service.EnqueueAsync(bad.Id, bad.UserId, BookmarkClipMode.Accurate, null, Ct);
            await WaitForCompletedAsync(store, bad.Id);
            while (source.Lookups == lookups && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, Ct);
            }

            // The artwork's look-up threw after the clip was recorded: the clip stays made, with its file.
            await Task.Delay(100, Ct);
            Assert.True(source.Lookups > lookups);
            var done = store.Get(bad.Id)!;
            Assert.Equal(BookmarkClipStatus.Completed, done.Clip!.Status);
            Assert.True(File.Exists(done.Clip.FilePath));
            Assert.Null(done.Clip.Error);
        }
        finally
        {
            runner.Release();
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void TheLibrarysImages_AreTheVideosLocalOnes_AndAnEpisodesSeriesPoster()
    {
        var library = new Mock<ILibraryManager>();
        var providers = new Mock<IProviderManager>();
        var source = new LibraryClipArtworkSource(library.Object, providers.Object, Mock.Of<IFileSystem>(), NullLogger.Instance);
        var movie = new Movie { Id = Guid.NewGuid(), ImageInfos = [Image(ImageType.Primary, @"C:\meta\poster.jpg"), Image(ImageType.Backdrop, @"C:\meta\backdrop.jpg"), Image(ImageType.Backdrop, @"C:\meta\backdrop1.jpg")] };
        var remote = new Movie { Id = Guid.NewGuid(), ImageInfos = [Image(ImageType.Primary, "https://image.example/poster.jpg")] };
        var series = new Series { Id = Guid.NewGuid(), ImageInfos = [Image(ImageType.Primary, @"C:\meta\series.jpg"), Image(ImageType.Backdrop, @"C:\meta\series-backdrop.jpg")] };
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, ImageInfos = [Image(ImageType.Primary, @"C:\meta\still.jpg")] };
        var orphanEpisode = new Episode { Id = Guid.NewGuid(), ImageInfos = [Image(ImageType.Primary, @"C:\meta\still2.jpg")] };
        foreach (var item in new BaseItem[] { movie, remote, series, episode, orphanEpisode })
        {
            library.Setup(l => l.GetItemById(item.Id)).Returns(item);
        }

        Assert.Equal(new ClipArtworkImages(@"C:\meta\poster.jpg", @"C:\meta\backdrop.jpg", null), source.Find(movie.Id));
        Assert.Equal(new ClipArtworkImages(null, null, null), source.Find(remote.Id));
        Assert.Equal(new ClipArtworkImages(@"C:\meta\series.jpg", @"C:\meta\series-backdrop.jpg", @"C:\meta\still.jpg"), source.Find(episode.Id));
        Assert.Equal(new ClipArtworkImages(@"C:\meta\still2.jpg", null, @"C:\meta\still2.jpg"), source.Find(orphanEpisode.Id));
        Assert.Null(source.Find(Guid.NewGuid()));
        Assert.Null(source.Find(Guid.Empty));

        // A clip that is a library item gets an image refresh; one that isn't yet gets none.
        var clipItem = new Video { Id = Guid.NewGuid() };
        library.Setup(l => l.FindByPath(@"C:\clips\a [00.00.01-00.00.02].mp4", false)).Returns(clipItem);
        Assert.True(source.RefreshClip(@"C:\clips\a [00.00.01-00.00.02].mp4"));
        Assert.False(source.RefreshClip(@"C:\clips\b [00.00.01-00.00.02].mp4"));
        providers.Verify(p => p.QueueRefresh(clipItem.Id, It.IsAny<MetadataRefreshOptions>(), RefreshPriority.Normal), Times.Once);
        providers.VerifyNoOtherCalls();
    }

    private static string Text(string path) => File.ReadAllText(path);

    private static void Write(string path, string text) => File.WriteAllText(path, text);

    private static ItemImageInfo Image(ImageType type, string path) => new() { Type = type, Path = path };

    private static async Task<Bookmark> WaitForCompletedAsync(BookmarkStore store, Guid id)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (store.Get(id)?.Clip?.Status != BookmarkClipStatus.Completed)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the clip: " + store.Get(id)?.Clip?.Status);
            await Task.Delay(20, Ct);
        }

        return store.Get(id)!;
    }

    private string Art(string kind, string extension) => Path.Combine(_alice, Stem + "-" + kind + extension);

    private async Task<Bookmark> AddClipAsync(
        string fileName,
        string? folder = null,
        Guid? itemId = null,
        bool createFile = true,
        BookmarkClipStatus status = BookmarkClipStatus.Completed,
        Guid? userId = null)
    {
        folder ??= _alice;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        if (createFile)
        {
            await File.WriteAllTextAsync(path, "clip", Ct);
        }

        var now = DateTime.UtcNow;
        var bookmark = TestData.CreateBookmark(userId ?? _userId, itemId: itemId ?? _movieId) with
        {
            Name = "Bank job",
            Clip = new ClipInfo
            {
                Status = status,
                Mode = ClipFileNaming.GetMode(fileName),
                StartTicks = TestData.OneSecond * 6730,
                EndTicks = TestData.OneSecond * 6900,
                RequestedAt = now,
                CompletedAt = now,
                FilePath = path,
                FileSize = 4,
                FileStartTicks = TestData.OneSecond * 6730,
                FileEndTicks = TestData.OneSecond * 6900
            }
        };
        await _store.AddAsync(bookmark, Ct);
        return bookmark;
    }
}
