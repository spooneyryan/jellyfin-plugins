using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// A named clip with the real ffmpeg (skipped without ffmpeg): the manager, the
/// real planner and the clip worker make a clip of a named range bookmark of "Test Pattern (2020)", and its file is named
/// <c>Test Pattern (2020) - Bank job [00.01.05-00.01.15].mp4</c> and titled with the name (<c>ffprobe -show_entries
/// format_tags=title</c>), not the note. A rename leaves that file's name as it is; the clip made next carries the
/// new name, and the old file goes.
/// </summary>
public sealed class NamedClipFfmpegTests : IAsyncLifetime
{
    private readonly TempDirectory _directory = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private BookmarkStore? _store;
    private ClipExtractionService? _service;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ANamedRangeClip_IsNamedAndTitledWithTheName_AndARenameReachesTheNextClip()
    {
        var tools = FfmpegTools.Require();
        var source = _directory.Combine("media", "Test Pattern (2020).mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await tools.RunAsync(
            [
                "-hide_banner", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-t", "80",
                "-c:v", "libx264", "-preset", "ultrafast", "-g", "50", "-pix_fmt", "yuv420p",
                "-c:a", "aac",
                source
            ],
            Ct);
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Test Pattern", ProductionYear = 2020, RunTimeTicks = 80 * TestData.OneSecond, Path = source };
        var manager = await StartAsync(tools, movie, source);

        var bookmark = await manager.CreateBookmarkAsync(
            _alice,
            new BookmarkCreateRequest
            {
                ItemId = movie.Id,
                StartTicks = 65 * TestData.OneSecond,
                EndTicks = 75 * TestData.OneSecond,
                Name = " Bank job ",
                Note = "Watch the reflection in the window"
            },
            Ct);
        await manager.StartClipAsync(_alice, bookmark.Id, BookmarkClipMode.Accurate, isAdmin: false, Ct);
        var first = await WaitForClipAsync(bookmark.Id);

        var clipFolder = _directory.Combine("data", "bookmarks", "clips", "alice");
        var firstFile = Path.Combine(clipFolder, "Test Pattern (2020) - Bank job [00.01.05-00.01.15].mp4");
        Assert.Equal(firstFile, first.FilePath);
        Assert.True(File.Exists(firstFile));
        Assert.Equal("Test Pattern (2020) - Bank job (0:01:05-0:01:15)", await tools.ProbeTitleAsync(firstFile, Ct));
        var probe = await tools.ProbeAsync(firstFile, Ct);
        Assert.InRange(probe.Duration, 9.9, 10.15);
        Assert.Contains(("video", "h264"), probe.Streams);

        // A rename keeps the clip file's name: the DTO names the same file, which is still there.
        var renamed = await manager.RenameBookmarkAsync(_alice, bookmark.Id, new BookmarkRenameRequest { Name = "Vault", IfUpdatedAt = bookmark.UpdatedAt }, Ct);
        Assert.Equal("Vault", renamed.Name);
        Assert.Equal(Path.GetFileName(firstFile), renamed.Clip!.FileName);
        Assert.True(File.Exists(firstFile));

        // The next clip uses the new name; the previous file goes once the bookmark points at the new one.
        await manager.StartClipAsync(_alice, bookmark.Id, BookmarkClipMode.Fast, isAdmin: false, Ct);
        var second = await WaitForClipAsync(bookmark.Id);
        var secondFile = Path.Combine(clipFolder, "Test Pattern (2020) - Vault [00.01.05-00.01.15].mkv");
        Assert.Equal(secondFile, second.FilePath);
        Assert.Equal("Test Pattern (2020) - Vault (0:01:05-0:01:15)", await tools.ProbeTitleAsync(secondFile, Ct));
        Assert.False(File.Exists(firstFile));
        Assert.Equal([secondFile], Directory.GetFiles(clipFolder));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.StopAsync(CancellationToken.None);
            _service.Dispose();
        }

        _store?.Dispose();
        _directory.Dispose();
    }

    private async Task<IBookmarkManager> StartAsync(FfmpegTools tools, Movie movie, string source)
    {
        var library = new Mock<ILibraryManager>();
        library.Setup(m => m.GetItemById<Video>(movie.Id, It.IsAny<User?>())).Returns(movie);
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUserById(_alice.Id)).Returns(_alice);
        var mediaSources = new Mock<IMediaSourceManager>();
        mediaSources.Setup(m => m.GetStaticMediaSources(movie, false, _alice)).Returns(
        [
            new MediaSourceInfo
            {
                Id = movie.Id.ToString("N"),
                Path = source,
                Protocol = MediaProtocol.File,
                VideoType = VideoType.VideoFile,
                Container = "mkv",
                Size = new FileInfo(source).Length,
                RunTimeTicks = movie.RunTimeTicks,
                MediaStreams =
                [
                    new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Width = 320, Height = 240 },
                    new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", IsDefault = true }
                ]
            }
        ]);
        var encoder = new Mock<IMediaEncoder>();
        encoder.SetupGet(m => m.EncoderPath).Returns(tools.Ffmpeg);
        var applicationPaths = new Mock<IApplicationPaths>();
        applicationPaths.SetupGet(p => p.DataPath).Returns(_directory.Combine("data"));
        var configuration = TestData.ConfigurationProvider(new PluginConfiguration());
        var planner = new ClipPlanner(
            library.Object,
            users.Object,
            mediaSources.Object,
            encoder.Object,
            configuration,
            new BookmarkPaths(new KitPaths(applicationPaths.Object, BookmarksKit.Options)));

        _store = BookmarkStore.Create(_directory.Combine("data", "bookmarks", "bookmarks.json"), NullLogger.Instance);
        var files = ClipFileSupport.Ops(_store);
        var guard = ClipFileSupport.Guard(_store);
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        _service = new ClipExtractionService(
            _store,
            new BackgroundWriteGate(_store, NullLogger<BackgroundWriteGate>.Instance),
            planner,
            new FfmpegRunner(NullLogger<FfmpegRunner>.Instance),
            files,
            ClipFileSupport.Janitor(_store, files),
            guard,
            configuration,
            host.Object,
            NullLogger<ClipExtractionService>.Instance);
        await _service.StartAsync(Ct);

        return new BookmarkManager(_store, library.Object, _service, planner, files, guard, configuration, NullLogger<BookmarkManager>.Instance);
    }

    private async Task<ClipInfo> WaitForClipAsync(Guid bookmarkId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        for (; ; )
        {
            if (_store!.Get(bookmarkId)?.Clip is { Status: BookmarkClipStatus.Completed or BookmarkClipStatus.Failed or BookmarkClipStatus.Cancelled } clip)
            {
                Assert.True(clip.Status == BookmarkClipStatus.Completed, clip.Error);
                return clip;
            }

            Assert.True(DateTime.UtcNow < deadline, "The clip didn't finish.");
            await Task.Delay(50, Ct);
        }
    }
}
