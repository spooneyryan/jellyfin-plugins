using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// Playlist exports (since 1.5.0 / 2.3.0) end to end, on real files in a temporary clip folder: the clips' permission (clips
/// on, the user allowed to download), only the caller's playlists and exports; the export made in the background into
/// <c>&lt;playlist&gt;.mp4</c> beside the user's clips through a <c>.partial</c>, never over a file, with the playlist's poster;
/// a cancel that leaves nothing behind; and the clip maintenance (artwork clean-up, the <c>.partial</c> janitor) leaving the
/// export and its poster alone. With the real ffmpeg (skipped without it): three synthetic videos of different sizes and
/// frame rates, one without sound, joined into one H.264/AAC file that ffprobe and a full decode find sound.
/// </summary>
public sealed class PlaylistExportServiceTests : IAsyncLifetime
{
    private readonly TempDirectory _directory = new();
    private readonly FakePlaylistExportLibrary _library = new();
    private readonly PluginConfiguration _configuration = new() { OrganizeClipsByUser = false };
    private readonly Mock<IMediaEncoder> _mediaEncoder = new();
    private readonly FakeFfmpegRunner _fakeRunner = new();
    private readonly ListLogger<PlaylistExportService> _logger = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly User _bob = TestData.CreateUser("bob");
    private readonly string _root;
    private BookmarkStore? _store;
    private ClipFileOps? _files;
    private ClipExtractionService? _clips;
    private PlaylistExportService? _service;

    public PlaylistExportServiceTests()
    {
        _root = _directory.Combine("clips");
        _configuration.ClipOutputPath = _root;
        _mediaEncoder.SetupGet(m => m.EncoderPath).Returns("ffmpeg");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PlaylistExportService Service => _service ??= CreateService(_fakeRunner);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _service?.Dispose();
        if (_clips is not null)
        {
            await _clips.StopAsync(CancellationToken.None);
            _clips.Dispose();
        }

        _store?.Dispose();
        _directory.Dispose();
    }

    [Fact]
    public async Task TheClipsPermission_IsNeeded_AndOnlyTheCallersPlaylistsAndExports_AreSeen()
    {
        var playlist = _library.AddPlaylist("Road trip", Movie("Heat", 60));

        // Clips off: no export for anyone.
        _configuration.EnableClips = false;
        Assert.Equal(PluginErrorKind.Forbidden, (await Assert.ThrowsAsync<BookmarkException>(() => Service.EstimateAsync(_alice, playlist, false, Ct))).Kind);
        Assert.Equal(PluginErrorKind.Forbidden, (await Assert.ThrowsAsync<BookmarkException>(() => Service.StartAsync(_alice, playlist, false, Ct))).Kind);
        _configuration.EnableClips = true;

        // A user who may not download media.
        var carol = TestData.CreateUser("carol", canDownload: false);
        Assert.Equal(PluginErrorKind.Forbidden, (await Assert.ThrowsAsync<BookmarkException>(() => Service.EstimateAsync(carol, playlist, false, Ct))).Kind);
        Assert.Equal(PluginErrorKind.Forbidden, (await Assert.ThrowsAsync<BookmarkException>(() => Service.StartAsync(carol, playlist, false, Ct))).Kind);

        // Another user's playlist is one that doesn't exist.
        _library.PlaylistUsers[playlist] = [_alice.Id];
        Assert.Equal(PluginErrorKind.NotFound, (await Assert.ThrowsAsync<BookmarkException>(() => Service.EstimateAsync(_bob, playlist, false, Ct))).Kind);
        Assert.Equal(PluginErrorKind.NotFound, (await Assert.ThrowsAsync<BookmarkException>(() => Service.StartAsync(_bob, Guid.NewGuid(), false, Ct))).Kind);

        // And so is another user's export.
        var export = await Service.StartAsync(_alice, playlist, false, Ct);
        Assert.Equal(PluginErrorKind.NotFound, Assert.Throws<BookmarkException>(() => Service.GetExport(_bob, export.Id)).Kind);
        Assert.Equal(PluginErrorKind.NotFound, Assert.Throws<BookmarkException>(() => Service.Cancel(_bob, export.Id)).Kind);
        Assert.Empty(Service.GetExports(_bob));
        Assert.Equal([export.Id], Service.GetExports(_alice).Select(e => e.Id));
    }

    [Fact]
    public async Task TheEstimate_SaysWhatTheExportWillBe()
    {
        var hidden = Movie("Secret", 60);
        _library.Hidden.Add(hidden.Id);
        var song = new MediaBrowser.Controller.Entities.Audio.Audio { Id = Guid.NewGuid(), Name = "Theme" };
        var playlist = _library.AddPlaylist("Road trip", Movie("Heat", 60, 1280, 720), song, hidden, Movie("Ronin", 90, 1920, 1080));
        _directory.CreateFile(Path.Combine("clips", "Road trip.mp4"));

        var estimate = await Service.EstimateAsync(_alice, playlist, false, Ct);

        Assert.Equal(2, estimate.VideoCount);
        Assert.Equal([("Theme", PlaylistExportSkipReasons.NotAVideo), (null, PlaylistExportSkipReasons.NotAvailable)], estimate.Skipped.Select(s => (s.Name, s.Reason)));
        Assert.Equal(TimeSpan.FromSeconds(150).Ticks, estimate.DurationTicks);
        Assert.Equal((1920, 1080, 25.0), (estimate.Width, estimate.Height, estimate.FrameRate));
        Assert.Equal("Road trip (2).mp4", estimate.FileName);
        Assert.True(estimate.EstimatedBytes > 0);
        Assert.True(estimate.EstimatedSeconds > 0);
        Assert.True(estimate.CanExport);
        Assert.False(estimate.IsLarge);
        Assert.Null(estimate.Problem);

        var nothing = await Service.EstimateAsync(_alice, _library.AddPlaylist("Songs", song), false, Ct);
        Assert.False(nothing.CanExport);
        Assert.Equal(PlaylistExportPlanner.NoVideosMessage, nothing.Problem);
        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<BookmarkException>(() => Service.StartAsync(_alice, nothing.PlaylistId, false, Ct))).Kind);
    }

    [Fact]
    public async Task AnExport_IsWrittenBesideTheClips_ThroughAPartial_WithThePlaylistsPoster_NeverOverAFile()
    {
        var poster = _directory.CreateFile(Path.Combine("metadata", "playlist", "poster.jpg"), "poster image");
        var playlist = _library.AddPlaylist("Road trip", Movie("Heat", 60), Movie("Ronin", 30));
        _library.Posters[playlist] = poster;
        _directory.CreateFile(Path.Combine("clips", "Road trip.mp4"), "someone's own file");

        var export = await Service.StartAsync(_alice, playlist, false, Ct);
        var done = await WaitAsync(export.Id);

        Assert.True(done.State == BookmarkPlaylistExportState.Completed, done.Error);
        Assert.Equal("Road trip (2).mp4", done.FileName);
        Assert.Equal(new FileInfo(Path.Combine(_root, "Road trip (2).mp4")).Length, done.FileSize);
        Assert.Equal("clip", await File.ReadAllTextAsync(Path.Combine(_root, "Road trip (2).mp4"), Ct));
        Assert.Equal("someone's own file", await File.ReadAllTextAsync(Path.Combine(_root, "Road trip.mp4"), Ct));
        Assert.Equal("poster image", await File.ReadAllTextAsync(Path.Combine(_root, "Road trip (2)-poster.jpg"), Ct));
        Assert.Equal(
            ["Road trip (2)-poster.jpg", "Road trip (2).mp4", "Road trip.mp4"],
            ClipFileSupport.FilesBesidesMarker(_root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(ClipFileSupport.IsMarked(_root));
        Assert.Null(done.Progress);
        Assert.NotNull(done.CompletedAt);

        // No bookmark, no clip record: the export isn't a clip.
        Assert.Empty(_store!.GetAll());

        // Without artwork for clips, no poster.
        _configuration.GiveClipsVideoArtwork = false;
        var again = await WaitAsync((await Service.StartAsync(_alice, playlist, false, Ct)).Id);
        Assert.Equal("Road trip (3).mp4", again.FileName);
        Assert.False(File.Exists(Path.Combine(_root, "Road trip (3)-poster.jpg")));
    }

    [Fact]
    public async Task ACancel_StopsTheExport_AndLeavesNothingBehind()
    {
        var playlist = _library.AddPlaylist("Road trip", Movie("Heat", 60));
        _fakeRunner.HoldUntilReleased();

        var export = await Service.StartAsync(_alice, playlist, false, Ct);
        await _fakeRunner.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // While it is made: Running, with its progress; a second export of the playlist is refused.
        var running = Service.GetExport(_alice, export.Id);
        Assert.Equal(BookmarkPlaylistExportState.Running, running.State);
        Assert.Equal(50, running.Progress);
        Assert.Single(ClipFileSupport.FilesBesidesMarker(_root), f => f.EndsWith(".mp4.partial", StringComparison.Ordinal));
        Assert.Equal(PluginErrorKind.Conflict, (await Assert.ThrowsAsync<BookmarkException>(() => Service.StartAsync(_alice, playlist, false, Ct))).Kind);

        Service.Cancel(_alice, export.Id);
        var done = await WaitAsync(export.Id);

        Assert.Equal(BookmarkPlaylistExportState.Cancelled, done.State);
        Assert.Empty(ClipFileSupport.FilesBesidesMarker(_root));
        Assert.Equal(1, _fakeRunner.CancelledRuns);
    }

    [Fact]
    public async Task AWaitingExport_IsCancelledBeforeItStarts_AndAUserHasThreeAtMost()
    {
        _fakeRunner.HoldUntilReleased();
        var first = await Service.StartAsync(_alice, _library.AddPlaylist("One", Movie("A", 10)), false, Ct);
        await _fakeRunner.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var second = await Service.StartAsync(_alice, _library.AddPlaylist("Two", Movie("B", 10)), false, Ct);
        await Service.StartAsync(_alice, _library.AddPlaylist("Three", Movie("C", 10)), false, Ct);

        Assert.Equal(PluginErrorKind.Conflict, (await Assert.ThrowsAsync<BookmarkException>(() => Service.StartAsync(_alice, _library.AddPlaylist("Four", Movie("D", 10)), false, Ct))).Kind);
        Assert.Equal(BookmarkPlaylistExportState.Cancelled, Service.Cancel(_alice, second.Id).State);

        _fakeRunner.Release();
        Assert.Equal(BookmarkPlaylistExportState.Completed, (await WaitAsync(first.Id)).State);
        Assert.Equal(BookmarkPlaylistExportState.Cancelled, (await WaitAsync(second.Id)).State);
        Assert.False(File.Exists(Path.Combine(_root, "Two.mp4")));
        Assert.Equal(2, _fakeRunner.Runs);
    }

    [Fact]
    public async Task TheClipMaintenance_LeavesTheExportAndItsPosterAlone_AndTheJanitorDeletesOnlyAStalePartialOfOne()
    {
        var poster = _directory.CreateFile(Path.Combine("metadata", "playlist", "poster.png"), "poster image");
        var playlist = _library.AddPlaylist("Best of [2024]", Movie("Heat", 60));
        _library.Posters[playlist] = poster;
        var done = await WaitAsync((await Service.StartAsync(_alice, playlist, false, Ct)).Id);
        Assert.Equal("Best of (2024).mp4", done.FileName);

        // A crash left another export's .partial before this start, and one is being written now.
        var stale = _directory.CreateFile(Path.Combine("clips", "Old trip [00.00.00-00.10.00] (0123456789abcdef0123456789abcdef).mp4.partial"), "half");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));
        var current = _directory.CreateFile(Path.Combine("clips", "New trip [00.00.00-00.10.00] (fedcba9876543210fedcba9876543210).mp4.partial"), "half");
        File.SetLastWriteTimeUtc(current, DateTime.UtcNow.AddHours(1));

        var artwork = new ClipArtwork(_store!, _files!, new FakeClipArtworkSource(), TestData.ConfigurationProvider(_configuration), NullLogger.Instance);
        _files!.Artwork = () => artwork;
        var artworkRun = await artwork.SyncAllAsync(Ct);
        var janitorRun = await ClipFileSupport.Janitor(_store!, _files, startedUtc: DateTime.UtcNow).RunAtStartupAsync(Ct);
        artwork.Dispose();

        Assert.Equal(0, artworkRun.Deleted);
        Assert.Equal(1, janitorRun.Partials!.Deleted);
        Assert.False(File.Exists(stale));
        Assert.Equal(
            ["Best of (2024)-poster.png", "Best of (2024).mp4", Path.GetFileName(current)],
            ClipFileSupport.FilesBesidesMarker(_root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ThreeVideosOfDifferentSizesAndRates_OneWithoutSound_AreJoinedIntoOneSoundFile()
    {
        var tools = FfmpegTools.Require();
        _mediaEncoder.SetupGet(m => m.EncoderPath).Returns(tools.Ffmpeg);
        _configuration.VideoPreset = "ultrafast";
        _service = CreateService(new FfmpegRunner(NullLogger<FfmpegRunner>.Instance));

        // 3 s of 640x360 at 25 with stereo sound; 2 s of 320x240 at 30 without sound; 4 s of 1280x720 at 23.976 with 5.1.
        var small = await SyntheticAsync(tools, "small.mkv", "testsrc2=size=640x360:rate=25", "sine=frequency=440:sample_rate=44100", "stereo", 3);
        var silent = await SyntheticAsync(tools, "silent.mp4", "testsrc=size=320x240:rate=30", null, null, 2);
        var large = await SyntheticAsync(tools, "large [hd].mkv", "smptehdbars=size=1280x720:rate=24000/1001", "sine=frequency=660:sample_rate=48000", "5.1", 4);
        var playlist = _library.AddPlaylist(
            "Synthetic trip",
            Movie("Small", 3, 640, 360, 25, small),
            Movie("Silent", 2, 320, 240, 30, silent, audio: false),
            Movie("Large", 4, 1280, 720, 23.976f, large));

        var estimate = await Service.EstimateAsync(_alice, playlist, false, Ct);
        Assert.Equal((1280, 720, 23.976), (estimate.Width, estimate.Height, estimate.FrameRate));

        var done = await WaitAsync((await Service.StartAsync(_alice, playlist, false, Ct)).Id, TimeSpan.FromMinutes(2));
        Assert.True(done.State == BookmarkPlaylistExportState.Completed, done.Error);
        var output = Path.Combine(_root, "Synthetic trip.mp4");

        var streams = await tools.ProbeStreamsAsync(output, Ct);
        var video = Assert.Single(streams, s => s["codec_type"] == "video");
        var audio = Assert.Single(streams, s => s["codec_type"] == "audio");
        Assert.Equal(2, streams.Count);
        Assert.Equal(("h264", "1280", "720", "24000/1001"), (video["codec_name"], video["width"], video["height"], video["r_frame_rate"]));
        Assert.Equal(("aac", "2", "48000"), (audio["codec_name"], audio["channels"], audio["sample_rate"]));
        var probe = await tools.ProbeAsync(output, Ct);
        Assert.InRange(probe.Duration, 8.9, 9.15);
        Assert.InRange(double.Parse(video["duration"], CultureInfo.InvariantCulture), 8.9, 9.1);
        Assert.InRange(double.Parse(audio["duration"], CultureInfo.InvariantCulture), 8.9, 9.1);
        Assert.Equal("Synthetic trip", await tools.ProbeTitleAsync(output, Ct));
        Assert.Equal(string.Empty, await tools.DecodeErrorsAsync(output, Ct));
        Assert.Equal(["Synthetic trip.mp4"], ClipFileSupport.FilesBesidesMarker(_root).Select(Path.GetFileName));
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "jfbm-export-" + done.Id.ToString("N") + ".txt"));
    }

    private async Task<string> SyntheticAsync(FfmpegTools tools, string name, string video, string? audio, string? layout, int seconds)
    {
        var path = _directory.Combine("media", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var args = new List<string> { "-hide_banner", "-y", "-f", "lavfi", "-i", video };
        if (audio is not null)
        {
            args.AddRange(["-f", "lavfi", "-i", audio]);
        }

        args.AddRange(["-t", seconds.ToString(CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]);
        if (audio is not null)
        {
            args.AddRange(["-c:a", "aac", "-af", "aformat=channel_layouts=" + layout]);
        }

        args.Add(path);
        await tools.RunAsync(args, Ct);
        return path;
    }

    private Movie Movie(string name, double seconds, int width = 1280, int height = 720, float frameRate = 25, string? path = null, bool audio = true)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = name, RunTimeTicks = (long)(seconds * TimeSpan.TicksPerSecond) };
        path ??= _directory.CreateFile(Path.Combine("media", movie.Id.ToString("N") + ".mkv"));
        _library.Sources[movie.Id] = [FakePlaylistExportLibrary.FileSource(movie.Id, path, seconds, width, height, frameRate, audio)];
        return movie;
    }

    private async Task<BookmarkPlaylistExportJobDto> WaitAsync(Guid exportId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var export = Service.GetExport(_alice, exportId);
            if (export.State is BookmarkPlaylistExportState.Completed or BookmarkPlaylistExportState.Failed or BookmarkPlaylistExportState.Cancelled)
            {
                return export;
            }

            Assert.True(DateTime.UtcNow < deadline, "The export didn't finish.");
            await Task.Delay(20, Ct);
        }
    }

    private PlaylistExportService CreateService(IFfmpegRunner runner)
    {
        _service?.Dispose();
        _store ??= BookmarkStore.Create(_directory.Combine("data", "bookmarks.json"), NullLogger.Instance);
        _files ??= ClipFileSupport.Ops(_store);
        var guard = ClipFileSupport.Guard(_store, configuration: _configuration);
        var configuration = TestData.ConfigurationProvider(_configuration);
        var gate = new BackgroundWriteGate(_store, NullLogger<BackgroundWriteGate>.Instance);
        if (_clips is null)
        {
            var host = new Mock<IServerApplicationHost>();
            host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
            _clips = new ClipExtractionService(
                _store,
                gate,
                new FakeClipPlanner(_directory),
                _fakeRunner,
                _files,
                ClipFileSupport.Janitor(_store, _files),
                guard,
                configuration,
                host.Object,
                new ListLogger<ClipExtractionService>());
        }

        var planner = new PlaylistExportPlanner(_library, _mediaEncoder.Object, configuration, BookmarkPaths.ForRoot(_directory.Combine("data")), _files);
        return new PlaylistExportService(planner, runner, _files, guard, _clips, _store, gate, configuration, _logger);
    }
}
