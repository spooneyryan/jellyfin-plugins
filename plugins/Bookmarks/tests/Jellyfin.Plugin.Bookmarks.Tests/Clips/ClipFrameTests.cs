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
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// A clip's first frame as its thumbnail (since 1.7.0 / 2.5.0, <c>UseClipFirstFrameAsThumbnail</c>): on real files in
/// temporary clip folders, the frame is written as <c>&lt;clip&gt;-poster.jpg</c> and <c>&lt;clip&gt;-thumb.jpg</c> with the
/// clip file's time, the film's backdrop stays the fanart, a frame of the file there is never taken again while a file made
/// again gets a new one, a failure leaves no file of its own and falls back to the film's images, and with the setting off
/// the artwork is the film's as before.
/// </summary>
public sealed class ClipFrameTests : IDisposable
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
    private readonly FakeClipFrameExtractor _frames = new();
    private readonly FakeClipFileSystem _fileSystem = new();
    private readonly ListLogger _logger = new();
    private readonly PluginConfiguration _configuration = new();
    private readonly BookmarkStore _store;
    private readonly ClipFileOps _files;
    private readonly ClipArtwork _artwork;

    public ClipFrameTests()
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
        _artwork = new ClipArtwork(_store, _files, _source, TestData.ConfigurationProvider(_configuration), _logger, _frames);
        _files.Artwork = () => _artwork;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _artwork.Dispose();
        _store.Dispose();
        _directory.Dispose();
    }

    [Fact]
    public void TheSetting_IsOnByDefault()
        => Assert.True(new PluginConfiguration().UseClipFirstFrameAsThumbnail);

    [Fact]
    public async Task Sync_WritesTheFirstFrame_AsThePosterAndTheThumb_WithTheClipFilesTime_AndTheFilmsBackdropAsTheFanart()
    {
        var clip = await AddClipAsync(ClipName);
        _source.LibraryClips.Add(clip.Clip!.FilePath!);

        var run = await _artwork.SyncAsync(clip.Id, Ct);

        Assert.Equal([clip.Clip.FilePath!], _frames.Calls);
        Assert.Equal("first frame", Text(Art("poster", ".jpg")));
        Assert.Equal("first frame", Text(Art("thumb", ".jpg")));
        Assert.Equal("backdrop image", Text(Art("fanart", ".png")));
        var clipTime = File.GetLastWriteTimeUtc(clip.Clip.FilePath!);
        Assert.Equal(clipTime, File.GetLastWriteTimeUtc(Art("poster", ".jpg")));
        Assert.Equal(clipTime, File.GetLastWriteTimeUtc(Art("thumb", ".jpg")));
        Assert.Equal(
            [Stem + "-fanart.png", Stem + "-poster.jpg", Stem + "-thumb.jpg", ClipName],
            Directory.GetFiles(_alice).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal((3, 1, 1, 1), (run.Written, run.Frames, run.ClipsWritten, run.Refreshed));

        // Again: the frame is the file's already; ffmpeg isn't run, nothing written, no refresh.
        var again = await _artwork.SyncAsync(clip.Id, Ct);
        Assert.Single(_frames.Calls);
        Assert.Equal((0, 0, 3, 0), (again.Written, again.Frames, again.UpToDate, again.Refreshed));
        Assert.Single(_source.Refreshed);

        // The daily pass leaves it too.
        var daily = await _artwork.SyncAllAsync(Ct);
        Assert.Single(_frames.Calls);
        Assert.Equal(0, daily.Written);
    }

    [Fact]
    public async Task AClipFileMadeAgain_UnderTheSameName_GetsANewFrame()
    {
        var clip = await AddClipAsync(ClipName);
        await _artwork.SyncAsync(clip.Id, Ct);

        // The remake replaces the file (a new last write time); its frame is taken again.
        await File.WriteAllTextAsync(clip.Clip!.FilePath!, "clip made again", Ct);
        File.SetLastWriteTimeUtc(clip.Clip.FilePath!, DateTime.UtcNow.AddMinutes(5));
        _frames.Frame = "the new first frame";
        var run = await _artwork.SyncAllAsync(Ct);

        Assert.Equal(2, _frames.Calls.Count);
        Assert.Equal("the new first frame", Text(Art("poster", ".jpg")));
        Assert.Equal("the new first frame", Text(Art("thumb", ".jpg")));
        Assert.Equal(1, run.Frames);
    }

    [Fact]
    public async Task AnOlderPosterInAnotherFormat_Goes_AndTheFilmsPosterIsReplacedByTheFrame()
    {
        var clip = await AddClipAsync(ClipName);
        Write(Art("poster", ".webp"), "the film's poster, from before");
        Write(Art("poster", ".jpg"), "the film's poster, from before");

        await _artwork.SyncAsync(clip.Id, Ct);

        Assert.Equal("first frame", Text(Art("poster", ".jpg")));
        Assert.False(File.Exists(Art("poster", ".webp")));
        Assert.Empty(Directory.GetFiles(_alice, "*.tmp"));
    }

    [Fact]
    public async Task AFailedFrame_IsAWarningWithTheBookmarksIdOnly_LeavesNoFileOfItsOwn_AndTheFilmsImagesAreUsed()
    {
        var clip = await AddClipAsync(ClipName);
        _frames.Failure = new ClipExtractionException("ffmpeg failed with exit code 1: " + ClipName + ": Invalid data");

        var run = await _artwork.SyncAsync(clip.Id, Ct);

        Assert.Equal("poster image", Text(Art("poster", ".jpg")));
        Assert.Equal("backdrop image", Text(Art("fanart", ".png")));
        Assert.False(File.Exists(Art("thumb", ".jpg")));
        Assert.Empty(Directory.GetFiles(_alice, "*.tmp"));
        Assert.All(_frames.Outputs, o => Assert.False(File.Exists(o)));
        Assert.Equal((1, 0, 0), (run.FramesFailed, run.Frames, run.Failed));
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.Contains("first frame", StringComparison.Ordinal) && e.Message.Contains(clip.Id.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Entries.Where(e => e.Level >= LogLevel.Information), e => e.Message.Contains("Bank job", StringComparison.Ordinal) || e.Message.Contains("Heat", StringComparison.Ordinal));

        // It is tried again later, and then the frame takes the poster's place.
        _frames.Failure = null;
        await _artwork.SyncAllAsync(Ct);
        Assert.Equal("first frame", Text(Art("poster", ".jpg")));
        Assert.Equal("first frame", Text(Art("thumb", ".jpg")));
    }

    [Fact]
    public async Task WithTheSettingOff_TheArtworkIsTheFilmsAsBefore_AndAFrameWithoutAnImageToReplaceItGoes()
    {
        var clip = await AddClipAsync(ClipName);
        await _artwork.SyncAsync(clip.Id, Ct);
        var own = _directory.CreateFile(Path.Combine("clips", "alice", "Holiday-thumb.jpg"), "a user's own");

        _configuration.UseClipFirstFrameAsThumbnail = false;
        await _artwork.SyncAllAsync(Ct);

        Assert.Single(_frames.Calls);
        Assert.Equal("poster image", Text(Art("poster", ".jpg")));
        Assert.Equal("backdrop image", Text(Art("fanart", ".png")));
        Assert.False(File.Exists(Art("thumb", ".jpg")));           // a movie has no thumb of its own: the frame goes
        Assert.True(File.Exists(own));

        // A new clip with the setting off: exactly the film's images, no ffmpeg.
        var other = await AddClipAsync("Heat (1995) [00.10.00-00.11.00].mkv");
        await _artwork.SyncAsync(other.Id, Ct);
        Assert.Single(_frames.Calls);
        Assert.Equal(
            ["Heat (1995) [00.10.00-00.11.00]-fanart.png", "Heat (1995) [00.10.00-00.11.00]-poster.jpg", "Heat (1995) [00.10.00-00.11.00].mkv"],
            Directory.GetFiles(_alice, "Heat (1995) [00.10.00-00.11.00]*").Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WithTheVideosArtworkOff_TheFrameIsStillWritten_WithoutAFanart_AlsoForAClipWhoseVideoIsGone()
    {
        _configuration.GiveClipsVideoArtwork = false;
        var clip = await AddClipAsync(ClipName, itemId: Guid.NewGuid());

        var run = await _artwork.SyncAllAsync(Ct);

        Assert.Null(run.Skipped);
        Assert.Equal(
            [Stem + "-poster.jpg", Stem + "-thumb.jpg", ClipName],
            Directory.GetFiles(_alice).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        // Both off: nothing, as before.
        _configuration.UseClipFirstFrameAsThumbnail = false;
        var off = await _artwork.SyncAllAsync(Ct);
        Assert.Equal("GiveClipsVideoArtwork and UseClipFirstFrameAsThumbnail are off", off.Skipped);
        Assert.Single(_frames.Calls);
        Assert.NotNull(clip);
    }

    [Fact]
    public async Task ARename_MovesTheFrame_WhichStaysTheFilesFrame_SoItIsntTakenAgain()
    {
        var user = TestData.CreateUser();
        var clip = await AddClipAsync(ClipName, userId: user.Id);
        await _artwork.SyncAsync(clip.Id, Ct);

        var renamed = await new ClipFileRenamer(_store, _files, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).RenameAsync(user, clip.Id, changeName: true, "Vault", renameFile: true, ifUpdatedAt: null, Ct);

        const string NewStem = "Vault - Heat (1995) [01.52.10-01.55.00]";
        Assert.Equal(Path.Combine(_alice, NewStem + ".mp4"), renamed.Clip!.FilePath);
        Assert.Equal(
            [NewStem + "-fanart.png", NewStem + "-poster.jpg", NewStem + "-thumb.jpg", NewStem + ".mp4"],
            Directory.GetFiles(_alice).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("first frame", Text(Path.Combine(_alice, NewStem + "-thumb.jpg")));
        Assert.Single(_frames.Calls);
    }

    [Fact]
    public async Task ARenameInCaseAlone_RenamesTheFramesInCaseToo_SoNoneIsLeftUnderTheOldName()
    {
        // Found on a sandbox: on Windows the frame's new name found the old file itself, so the move was skipped and the old
        // name's frame stayed, beside the new name's (and after the clip's delete) until the daily maintenance.
        var user = TestData.CreateUser();
        var clip = await AddClipAsync(ClipName, userId: user.Id);
        await _artwork.SyncAsync(clip.Id, Ct);

        var renamed = await new ClipFileRenamer(_store, _files, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).RenameAsync(user, clip.Id, changeName: true, "BANK JOB", renameFile: true, ifUpdatedAt: null, Ct);

        const string NewStem = "BANK JOB - Heat (1995) [01.52.10-01.55.00]";
        Assert.Equal(Path.Combine(_alice, NewStem + ".mp4"), renamed.Clip!.FilePath);
        Assert.Equal(
            [NewStem + "-fanart.png", NewStem + "-poster.jpg", NewStem + "-thumb.jpg", NewStem + ".mp4"],
            Directory.GetFiles(_alice).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Single(_frames.Calls);

        await ClipFileSingleDelete.RunAsync(_store, _files, clip.Id, onlyIfMissing: false, requireRecognisedFolder: true, _ => true, null, _ => null, null, forAdmin: false, null, Ct);
        Assert.Empty(Directory.GetFiles(_alice));
    }

    [Fact]
    public async Task ADelete_DeletesTheFramesToo()
    {
        var clip = await AddClipAsync(ClipName);
        await _artwork.SyncAsync(clip.Id, Ct);

        await ClipFileSingleDelete.RunAsync(_store, _files, clip.Id, onlyIfMissing: false, requireRecognisedFolder: true, _ => true, null, _ => null, null, forAdmin: false, null, Ct);

        Assert.Empty(Directory.GetFiles(_alice));
    }

    [Fact]
    public async Task APosterPathThatIsAFolder_IsLeftAsItIs_AndNoFrameIsTaken()
    {
        var clip = await AddClipAsync(ClipName);
        Directory.CreateDirectory(Art("poster", ".jpg"));

        await _artwork.SyncAsync(clip.Id, Ct);

        Assert.Empty(_frames.Calls);
        Assert.True(Directory.Exists(Art("poster", ".jpg")));
        Assert.False(File.Exists(Art("thumb", ".jpg")));
    }

    [Fact]
    public async Task NoFrameIsTaken_OfAClipNotFinished_OrAFileNotNamedAsBookmarksNamesClips()
    {
        var failed = await AddClipAsync("Heat [00.00.05-00.00.06].mp4", status: BookmarkClipStatus.Failed);
        var oldName = await AddClipAsync("Heat 1995 clip.mp4");

        await _artwork.SyncAsync(failed.Id, Ct);
        await _artwork.SyncAsync(oldName.Id, Ct);

        Assert.Empty(_frames.Calls);
        Assert.DoesNotContain(Directory.GetFiles(_root, "*", SearchOption.AllDirectories), f => ClipArtworkNames.Parse(Path.GetFileName(f)) is not null);
    }

    [Fact]
    public void TheCommand_TakesTheFirstVideoFrame_AsAJpeg_NeverOverAFile()
    {
        var args = FfmpegFrameCommandBuilder.Build(@"C:\clips\-x [00.00.01-00.00.02].mp4", @"C:\clips\-x [00.00.01-00.00.02]-poster.jpg.0123.tmp");

        Assert.Equal("file:C:\\clips\\-x [00.00.01-00.00.02].mp4", ValueAfter(args, "-i"));
        Assert.Equal("file:C:\\clips\\-x [00.00.01-00.00.02]-poster.jpg.0123.tmp", args[^1]);
        Assert.Equal("0:v:0", ValueAfter(args, "-map"));
        Assert.Equal("1", ValueAfter(args, "-frames:v"));
        Assert.Equal("mjpeg", ValueAfter(args, "-c:v"));
        Assert.Equal("image2", ValueAfter(args, "-f"));
        Assert.Equal("1", ValueAfter(args, "-update"));
        Assert.Equal(FfmpegFrameCommandBuilder.VideoFilter, ValueAfter(args, "-vf"));
        Assert.Contains("-n", args);
        Assert.DoesNotContain("-y", args);
        Assert.DoesNotContain("-ss", args);
        Assert.Contains("-an", args);
        Assert.Equal("pipe:1", ValueAfter(args, "-progress"));
    }

    [Fact]
    public async Task TheExtractor_RunsJellyfinsFfmpeg_AndFailsWithoutOne_OrWithoutAnImage()
    {
        var output = _directory.Combine("frame.jpg.tmp");
        var runner = new WritingRunner();
        await new ClipFrameExtractor(() => @"C:\jellyfin\ffmpeg.exe", runner).ExtractFirstFrameAsync(@"C:\clips\a [00.00.01-00.00.02].mp4", output, Ct);
        Assert.Equal(@"C:\jellyfin\ffmpeg.exe", runner.Ffmpeg);
        Assert.Equal("file:" + output, runner.Arguments![^1]);

        var none = await Assert.ThrowsAsync<ClipExtractionException>(() => new ClipFrameExtractor(() => string.Empty, runner).ExtractFirstFrameAsync("a.mp4", output, Ct));
        Assert.Equal("ffmpeg isn't configured on this server.", none.Message);

        runner.Write = false;
        await Assert.ThrowsAsync<ClipExtractionException>(() => new ClipFrameExtractor(() => "ffmpeg", runner).ExtractFirstFrameAsync("a.mp4", _directory.Combine("never.jpg.tmp"), Ct));

        runner.Hang = true;
        var slow = await Assert.ThrowsAsync<ClipExtractionException>(() => new ClipFrameExtractor(() => "ffmpeg", runner) { Timeout = TimeSpan.FromMilliseconds(50) }.ExtractFirstFrameAsync("a.mp4", output, Ct));
        Assert.Contains("too long", slow.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithRealFfmpeg_TheImageIsTheClipsVeryFirstFrame()
    {
        // A clip whose first frame is red and every later one blue: the image must be red.
        var tools = FfmpegTools.Require();
        var clip = Path.Combine(_alice, "Frame - Test (2020) [00.00.01-00.00.02].mp4");
        await tools.RunAsync(
            [
                "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                "-f", "lavfi", "-i", "color=c=red:s=320x180:r=25:d=0.04",
                "-f", "lavfi", "-i", "color=c=blue:s=320x180:r=25:d=1",
                "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0,format=yuv420p[v]", "-map", "[v]", "-c:v", "libx264", clip
            ],
            Ct);
        var output = _directory.Combine("frame.jpg.0123456789abcdef0123456789abcdef.tmp");
        var extractor = new ClipFrameExtractor(() => tools.Ffmpeg, new FfmpegRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegRunner>.Instance));

        await extractor.ExtractFirstFrameAsync(clip, output, Ct);

        var bytes = await File.ReadAllBytesAsync(output, Ct);
        Assert.True(bytes.Length > 100 && bytes[0] == 0xFF && bytes[1] == 0xD8, "a JPEG");
        var streams = await tools.ProbeStreamsAsync(output, Ct);
        Assert.Equal(("320", "180"), (streams[0]["width"], streams[0]["height"]));
        var pixel = _directory.Combine("pixel.rgb");
        await tools.RunAsync(["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-i", "file:" + output, "-vf", "scale=1:1", "-f", "rawvideo", "-pix_fmt", "rgb24", pixel], Ct);
        var rgb = await File.ReadAllBytesAsync(pixel, Ct);
        Assert.True(rgb[0] > 200 && rgb[2] < 60, $"the first frame is red, not {rgb[0]},{rgb[1]},{rgb[2]}");

        // ffmpeg failing (not a video): an exception, for ClipArtwork to fall back on.
        var notVideo = _directory.CreateFile("not a video [00.00.01-00.00.02].mp4", "text");
        await Assert.ThrowsAsync<ClipExtractionException>(() => extractor.ExtractFirstFrameAsync(notVideo, _directory.Combine("none.jpg.tmp"), Ct));
    }

    private static string ValueAfter(System.Collections.Generic.IReadOnlyList<string> args, string name) => args[args.ToList().IndexOf(name) + 1];

    private static string Text(string path) => File.ReadAllText(path);

    private static void Write(string path, string text) => File.WriteAllText(path, text);

    private string Art(string kind, string extension) => Path.Combine(_alice, Stem + "-" + kind + extension);

    private async Task<Bookmark> AddClipAsync(
        string fileName,
        Guid? itemId = null,
        BookmarkClipStatus status = BookmarkClipStatus.Completed,
        Guid? userId = null)
    {
        var path = Path.Combine(_alice, fileName);
        await File.WriteAllTextAsync(path, "clip", Ct);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
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

    // A runner that writes the output the arguments name (or doesn't), or waits until it is cancelled.
    private sealed class WritingRunner : IFfmpegRunner
    {
        public bool Write { get; set; } = true;

        public bool Hang { get; set; }

        public string? Ffmpeg { get; private set; }

        public System.Collections.Generic.IReadOnlyList<string>? Arguments { get; private set; }

        public async Task RunAsync(string ffmpegPath, System.Collections.Generic.IReadOnlyList<string> arguments, TimeSpan expectedDuration, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            (Ffmpeg, Arguments) = (ffmpegPath, arguments);
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (Write)
            {
                await File.WriteAllTextAsync(arguments[^1]["file:".Length..], "jpeg", cancellationToken);
            }
        }
    }
}
