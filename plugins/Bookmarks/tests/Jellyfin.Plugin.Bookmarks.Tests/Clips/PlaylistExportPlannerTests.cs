using System;
using System.IO;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// What a playlist export joins (since 1.5.0 / 2.3.0): every video entry the user may see and download, whose file is a
/// regular one that is there, in playlist order, each whole; the others listed with their reason (an entry the user may not
/// see without its name); the folder and the name as the user's clips; the poster; a playlist the user may not open is
/// the same as one that doesn't exist.
/// </summary>
public sealed class PlaylistExportPlannerTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly FakePlaylistExportLibrary _library = new();
    private readonly Mock<IMediaEncoder> _mediaEncoder = new();
    private readonly PluginConfiguration _configuration = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly PlaylistExportPlanner _planner;

    public PlaylistExportPlannerTests()
    {
        _configuration.ClipOutputPath = _directory.Combine("clips");
        _mediaEncoder.SetupGet(m => m.EncoderPath).Returns(@"C:\jellyfin\ffmpeg.exe");
        _planner = new PlaylistExportPlanner(_library, _mediaEncoder.Object, TestData.ConfigurationProvider(_configuration), BookmarkPaths.ForRoot(_directory.Combine("data")));
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void TheVideos_AreJoinedInPlaylistOrder_EachWhole_AndTheOthersListedWithTheirReason()
    {
        var episode = Video(new Episode { Name = "Pilot" }, 120, 1920, 1080, 23.976f);
        var movie = Video(new Movie { Name = "Heat" }, 60, 1280, 720, 25);
        var clip = Video(new Video { Name = "Heat - Bank job [00.01.00-00.02.00]" }, 30, 640, 360, 25, audio: false);
        var song = new MediaBrowser.Controller.Entities.Audio.Audio { Id = Guid.NewGuid(), Name = "Theme" };
        var folder = new Folder { Id = Guid.NewGuid(), Name = "Season 1" };
        var hidden = Video(new Movie { Name = "Secret" }, 60, 1280, 720, 25);
        _library.Hidden.Add(hidden.Id);
        var locked = Video(new Movie { Name = "Locked" }, 60, 1280, 720, 25);
        _library.NotDownloadable.Add(locked.Id);
        var offline = Video(new Movie { Name = "Offline" }, 60, 1280, 720, 25, createFile: false);
        var strm = Video(new Movie { Name = "Stream" }, 60, 1280, 720, 25, extension: ".strm");
        var remote = Video(new Movie { Name = "Remote" }, 60, 1280, 720, 25);
        _library.Sources[remote.Id][0].Protocol = MediaProtocol.Http;
        var disc = Video(new Movie { Name = "Disc" }, 60, 1280, 720, 25);
        _library.Sources[disc.Id][0].VideoType = VideoType.BluRay;
        var unscanned = Video(new Movie { Name = "New" }, 0, 1280, 720, 25);
        var noPicture = Video(new Movie { Name = "Radio" }, 60, 1280, 720, 25);
        _library.Sources[noPicture.Id][0].MediaStreams = _library.Sources[noPicture.Id][0].MediaStreams.Where(s => s.Type != MediaStreamType.Video).ToList();
        var playlist = _library.AddPlaylist("Road trip", movie, song, hidden, episode, null, folder, locked, offline, strm, remote, disc, unscanned, noPicture, clip);

        var plan = _planner.Plan(playlist, _alice)!;

        Assert.Equal([movie.Id, episode.Id, clip.Id], plan.Items.Select(i => i.ItemId));
        Assert.Equal(
            [
                ("Theme", PlaylistExportSkipReasons.NotAVideo),
                (null, PlaylistExportSkipReasons.NotAvailable),
                (null, PlaylistExportSkipReasons.NotInLibrary),
                ("Season 1", PlaylistExportSkipReasons.NotAVideo),
                ("Locked", PlaylistExportSkipReasons.NotDownloadable),
                ("Offline", PlaylistExportSkipReasons.FileMissing),
                ("Stream", PlaylistExportSkipReasons.NotAFile),
                ("Remote", PlaylistExportSkipReasons.NotAFile),
                ("Disc", PlaylistExportSkipReasons.NotAFile),
                ("New", PlaylistExportSkipReasons.LengthUnknown),
                ("Radio", PlaylistExportSkipReasons.NoVideoStream)
            ],
            plan.Skipped.Select(s => (s.Name, s.Reason)));
        Assert.Equal(TimeSpan.FromSeconds(210), plan.Duration);
        Assert.Equal(new PlaylistExportFormat(1920, 1080, 24000, 1001), plan.Format);
        Assert.Null(plan.Problem);

        var first = plan.Items[0];
        Assert.Equal((_library.Sources[movie.Id][0].Path, TimeSpan.FromSeconds(60), 0, 1), (first.InputPath, first.Duration, first.VideoStreamIndex, first.AudioStreamIndex));
        Assert.Null(plan.Items[2].AudioStreamIndex);
        Assert.Equal(@"C:\jellyfin\ffmpeg.exe", plan.EncoderPath);
    }

    [Fact]
    public void TheAudio_IsTheOneJellyfinPlaysForTheUser_ElseTheFilesDefault()
    {
        var movie = Video(new Movie { Name = "Heat" }, 60, 1280, 720, 25);
        var source = _library.Sources[movie.Id][0];
        source.MediaStreams =
        [
            .. source.MediaStreams,
            new MediaStream { Index = 2, Type = MediaStreamType.Audio, Language = "fre" },
            new MediaStream { Index = 3, Type = MediaStreamType.Subtitle }
        ];
        var playlist = _library.AddPlaylist("Films", movie);

        Assert.Equal(1, _planner.Plan(playlist, _alice)!.Items[0].AudioStreamIndex);

        // The user's language preference, as the media source manager sets it.
        source.DefaultAudioStreamIndex = 2;
        Assert.Equal(2, _planner.Plan(playlist, _alice)!.Items[0].AudioStreamIndex);
    }

    [Fact]
    public void TheVideosOwnVersion_IsTaken_ElseItsFirst()
    {
        var movie = Video(new Movie { Name = "Heat" }, 60, 1280, 720, 25);
        var own = _library.Sources[movie.Id][0];
        var other = FakePlaylistExportLibrary.FileSource(Guid.NewGuid(), _directory.CreateFile(Path.Combine("media", "Heat 4K.mkv")), 61, 3840, 2160);
        _library.Sources[movie.Id] = [other, own];
        var playlist = _library.AddPlaylist("Films", movie);

        Assert.Equal(own.Path, _planner.Plan(playlist, _alice)!.Items[0].InputPath);

        _library.Sources[movie.Id] = [other];
        Assert.Equal(other.Path, _planner.Plan(playlist, _alice)!.Items[0].InputPath);
    }

    [Fact]
    public void APlaylistTheUserMayNotOpen_IsNotFound()
    {
        var movie = Video(new Movie { Name = "Heat" }, 60, 1280, 720, 25);
        var bobs = _library.AddPlaylist("Bob's", movie);
        var bob = TestData.CreateUser("bob");
        _library.PlaylistUsers[bobs] = [bob.Id];

        Assert.Null(_planner.Plan(bobs, _alice));
        Assert.Null(_planner.Plan(Guid.NewGuid(), _alice));
        Assert.NotNull(_planner.Plan(bobs, bob));
    }

    [Fact]
    public void APlaylistWithoutAVideo_HasAProblem_AndSoDoesOneOfTooMany()
    {
        var song = new MediaBrowser.Controller.Entities.Audio.Audio { Id = Guid.NewGuid(), Name = "Theme" };
        var none = _planner.Plan(_library.AddPlaylist("Songs", song), _alice)!;
        Assert.Equal(PlaylistExportPlanner.NoVideosMessage, none.Problem);
        Assert.Equal(new PlaylistExportFormat(1920, 1080, 30, 1), none.Format);

        var movie = Video(new Movie { Name = "Heat" }, 1, 1280, 720, 25);
        var many = _planner.Plan(_library.AddPlaylist("Many", Enumerable.Repeat<BaseItem?>(movie, PlaylistExportPlanner.MaxItems + 1).ToArray()), _alice)!;
        Assert.Equal(PlaylistExportPlanner.MaxItems + 1, many.Items.Count);
        Assert.StartsWith("An export joins at most 100 videos; this playlist has 101.", many.Problem, StringComparison.Ordinal);

        var enough = _planner.Plan(_library.AddPlaylist("Enough", Enumerable.Repeat<BaseItem?>(movie, PlaylistExportPlanner.MaxItems).ToArray()), _alice)!;
        Assert.Null(enough.Problem);
    }

    [Fact]
    public void TheExport_GoesWhereTheUsersClipsGo_NamedAfterThePlaylist_AndNeverOverAFile()
    {
        var movie = Video(new Movie { Name = "Heat" }, 60, 1280, 720, 25);
        var playlist = _library.AddPlaylist("Road trip: [best]", movie);
        var root = _directory.Combine("clips");
        _configuration.OrganizeClipsByUser = false;

        var plan = _planner.Plan(playlist, _alice)!;
        Assert.Equal(Path.Combine(root, "Road trip - (best).mp4"), plan.OutputPath);
        Assert.Equal(root, plan.OutputFolder);

        _directory.CreateFile(Path.Combine("clips", "Road trip - (best).mp4"));
        _directory.CreateFile(Path.Combine("clips", "Road trip - (best) (2).mp4.partial"));
        Assert.Equal(Path.Combine(root, "Road trip - (best) (3).mp4"), _planner.Plan(playlist, _alice)!.OutputPath);

        _configuration.OrganizeClipsByUser = true;
        plan = _planner.Plan(playlist, _alice)!;
        Assert.Equal(Path.Combine(root, "alice"), plan.OutputFolder);
        Assert.Equal(Path.Combine(root, "alice", "Road trip - (best).mp4"), plan.OutputPath);
    }

    [Fact]
    public void ThePoster_IsThePlaylists_ElseTheFirstVideos()
    {
        var episode = Video(new Episode { Name = "Pilot" }, 60, 1280, 720, 25);
        var movie = Video(new Movie { Name = "Heat" }, 60, 1280, 720, 25);
        var playlist = _library.AddPlaylist("Shows", episode, movie);
        _library.Posters[episode.Id] = "episode.jpg";
        _library.Posters[movie.Id] = "movie.jpg";

        Assert.Equal("episode.jpg", _planner.Plan(playlist, _alice)!.PosterPath);

        _library.Posters[playlist] = "playlist.png";
        Assert.Equal("playlist.png", _planner.Plan(playlist, _alice)!.PosterPath);
    }

    [Fact]
    public void TheEstimate_AddsUp_AndALongExportIsLarge()
    {
        var movie = Video(new Movie { Name = "Long" }, 4 * 3600, 1280, 720, 25);
        var plan = _planner.Plan(_library.AddPlaylist("Marathon", movie), _alice)!;

        Assert.Equal(PlaylistExportEstimator.EstimateBytes(plan.Items, plan.Format, plan.Encoding), plan.EstimatedBytes);
        Assert.Equal(PlaylistExportEstimator.EstimateTime(plan.Items, plan.Format, plan.Encoding), plan.EstimatedTime);
        Assert.True(plan.IsLarge);
    }

    [Fact]
    public void WithoutFfmpeg_OrAClipFolderSetting_ThatIsntAFolder_ThePlanFails()
    {
        var playlist = _library.AddPlaylist("Films", Video(new Movie { Name = "Heat" }, 60, 1280, 720, 25));

        _configuration.ClipOutputPath = "C:\\clips\0bad";
        Assert.Throws<ClipExtractionException>(() => _planner.Plan(playlist, _alice));

        _configuration.ClipOutputPath = _directory.Combine("clips");
        _mediaEncoder.SetupGet(m => m.EncoderPath).Returns(string.Empty);
        Assert.Throws<ClipExtractionException>(() => _planner.Plan(playlist, _alice));
    }

    private T Video<T>(T video, double seconds, int width, int height, float frameRate, bool audio = true, bool createFile = true, string extension = ".mkv")
        where T : Video
    {
        video.Id = Guid.NewGuid();
        video.RunTimeTicks = (long)(seconds * TimeSpan.TicksPerSecond);
        var relative = Path.Combine("media", video.Id.ToString("N") + extension);
        var path = createFile ? _directory.CreateFile(relative) : _directory.Combine(relative);
        _library.Sources[video.Id] = [FakePlaylistExportLibrary.FileSource(video.Id, path, seconds, width, height, frameRate, audio)];
        return video;
    }
}
