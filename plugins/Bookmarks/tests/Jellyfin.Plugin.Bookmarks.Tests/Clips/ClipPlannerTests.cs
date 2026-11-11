using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport;
using MediaBrowser.Common.Configuration;
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

public sealed partial class ClipPlannerTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<IMediaSourceManager> _mediaSourceManager = new();
    private readonly Mock<IMediaEncoder> _mediaEncoder = new();
    private readonly PluginConfiguration _configuration = new();
    private readonly User _user = TestData.CreateUser("alice");
    private readonly Movie _movie = TestData.CreateMovie();
    private readonly MediaSourceInfo _source;
    private readonly ClipPlanner _planner;

    public ClipPlannerTests()
    {
        _source = new MediaSourceInfo
        {
            Id = "source-1",
            Path = _directory.CreateFile(Path.Combine("media", "Heat (1995).mkv")),
            Protocol = MediaProtocol.File,
            VideoType = VideoType.VideoFile,
            MediaStreams =
            [
                new MediaStream { Index = 0, Type = MediaStreamType.Video, Height = 1080 },
                new MediaStream { Index = 1, Type = MediaStreamType.Audio, Language = "eng", IsDefault = true },
                new MediaStream { Index = 2, Type = MediaStreamType.Audio, Language = "eng", Title = "Commentary" },
                new MediaStream { Index = 3, Type = MediaStreamType.Subtitle }
            ]
        };

        _userManager.Setup(m => m.GetUserById(_user.Id)).Returns(_user);
        _libraryManager.Setup(m => m.GetItemById<Video>(_movie.Id, _user)).Returns(_movie);
        _mediaSourceManager.Setup(m => m.GetStaticMediaSources(_movie, false, _user)).Returns(() => [_source]);
        _mediaEncoder.SetupGet(m => m.EncoderPath).Returns(@"C:\jellyfin\ffmpeg.exe");

        var applicationPaths = new Mock<IApplicationPaths>();
        applicationPaths.SetupGet(p => p.DataPath).Returns(_directory.Combine("data"));

        _planner = new ClipPlanner(
            _libraryManager.Object,
            _userManager.Object,
            _mediaSourceManager.Object,
            _mediaEncoder.Object,
            TestData.ConfigurationProvider(_configuration),
            new BookmarkPaths(new KitPaths(applicationPaths.Object, BookmarksKit.Options)));
    }

    [Fact]
    public void Plan_BuildsCommandForBookmarkRange()
    {
        var plan = _planner.Plan(Bookmark(), BookmarkClipMode.Accurate);

        Assert.Equal(@"C:\jellyfin\ffmpeg.exe", plan.EncoderPath);
        Assert.Equal(_source.Path, plan.Command.InputPath);
        Assert.Equal(TimeSpan.FromSeconds(60), plan.Command.Start);
        Assert.Equal(TimeSpan.FromSeconds(30), plan.Duration);
        Assert.Equal(plan.Duration, plan.Command.Duration);
        Assert.Equal(0, plan.Command.VideoStreamIndex);
        Assert.Equal(1080, plan.Command.SourceHeight);
        Assert.Equal(plan.TempPath, plan.Command.OutputPath);
        Assert.Equal(plan.OutputPath + ".partial", plan.TempPath);
        Assert.Equal("Heat (1995) - Bank job (0:01:00-0:01:30)", plan.Command.Title);
    }

    [Fact]
    public void Plan_EstimatesTheClipsSize_ForTheFreeSpaceGuard()
    {
        // The plan carries the estimate the request keeps with the job, for the free-space guard.
        _source.Bitrate = 8_000_000;

        var fast = _planner.Plan(Bookmark(), BookmarkClipMode.Fast);
        var accurate = _planner.Plan(Bookmark(), BookmarkClipMode.Accurate);

        // 8 Mbit/s × 30 s / 8 × 1.05.
        Assert.Equal(31_500_000, fast.EstimatedBytes);

        // 1080 lines: 10 Mbit/s, capped at 1.2 × 8 Mbit/s, plus 192 kbit/s of audio, × 30 s / 8 × 1.1.
        Assert.Equal((long)Math.Ceiling(((1.2 * 8_000_000) + 192_000) * 30 / 8 * 1.1), accurate.EstimatedBytes);
        Assert.Equal(Path.GetDirectoryName(accurate.OutputPath), accurate.OutputFolder);
    }

    [Fact]
    public void Plan_WritesToDefaultFolderPerUser()
    {
        var plan = _planner.Plan(Bookmark(), BookmarkClipMode.Accurate);

        Assert.Equal(
            _directory.Combine("data", "bookmarks", "clips", "alice", "Heat (1995) - Bank job [00.01.00-00.01.30].mp4"),
            plan.OutputPath);
    }

    [Fact]
    public void Plan_UsesConfiguredFolderWithoutUserSubfolder()
    {
        _configuration.ClipOutputPath = _directory.Combine("library", "Clips");
        _configuration.OrganizeClipsByUser = false;

        var plan = _planner.Plan(Bookmark(), BookmarkClipMode.Fast);

        Assert.Equal(_directory.Combine("library", "Clips", "Heat (1995) - Bank job [00.01.00-00.01.30].mkv"), plan.OutputPath);
    }

    [Fact]
    public void Plan_AvoidsOverwritingOtherFiles()
    {
        var first = _planner.Plan(Bookmark(), BookmarkClipMode.Accurate);
        _directory.CreateFile(Path.GetRelativePath(_directory.Path, first.OutputPath));

        var second = _planner.Plan(Bookmark(), BookmarkClipMode.Accurate);

        Assert.EndsWith("[00.01.00-00.01.30] (2).mp4", second.OutputPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_TreatsNamesBeingWrittenAsTaken()
    {
        var first = _planner.Plan(Bookmark(), BookmarkClipMode.Accurate);
        _directory.CreateFile(Path.GetRelativePath(_directory.Path, first.TempPath));

        var second = _planner.Plan(Bookmark(), BookmarkClipMode.Accurate);

        Assert.NotEqual(first.OutputPath, second.OutputPath);
        Assert.EndsWith("(2).mp4", second.OutputPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_TooLong_Throws()
    {
        _configuration.MaxClipDurationSeconds = 20;

        var ex = Assert.Throws<ClipExtractionException>(() => _planner.Plan(Bookmark(), BookmarkClipMode.Accurate));

        Assert.Equal("Clips can be at most 0:20 long; this one is 0:30.", ex.Message);
    }

    [Fact]
    public void Plan_NoLimit_AllowsLongClips()
    {
        _configuration.MaxClipDurationSeconds = 0;

        var plan = _planner.Plan(Bookmark() with { StartTicks = 0, EndTicks = 9000 * TestData.OneSecond }, BookmarkClipMode.Accurate);

        Assert.Equal(TimeSpan.FromSeconds(9000), plan.Duration);
    }

    [Fact]
    public void Plan_ARemakeGetsAFreshName_NeverThePreviousClipsFile()
    {
        // The previous clip may be in use (watched, downloaded), so a remake never writes over it; before 1.1 the planner
        // treated the previous clip's path as free. A planner without the clip file operations (only these
        // tests) still does so since Bookmarks 1.2.1, where the real one keeps the name to replace the file
        // (Plan_ARemakeKeepsItsOwnFilesName_ToReplaceIt_UnlessSomethingElseHoldsThatPath).
        var first = _planner.Plan(Bookmark(), BookmarkClipMode.Accurate);
        _directory.CreateFile(Path.GetRelativePath(_directory.Path, first.OutputPath));

        var again = _planner.Plan(Bookmark() with { Clip = new ClipInfo { FilePath = first.OutputPath } }, BookmarkClipMode.Accurate);

        Assert.Equal(Path.Combine(Path.GetDirectoryName(first.OutputPath)!, "Heat (1995) - Bank job [00.01.00-00.01.30] (2).mp4"), again.OutputPath);
        Assert.Equal(again.OutputPath + ".partial", again.TempPath);

        // Once the previous file is gone, the canonical name is free again.
        File.Delete(first.OutputPath);
        Assert.Equal(first.OutputPath, _planner.Plan(Bookmark() with { Clip = new ClipInfo { FilePath = first.OutputPath } }, BookmarkClipMode.Accurate).OutputPath);
    }

    [Fact]
    public void Plan_TheClipFolder_IsTheNormalisedRootBeforeThePerUserFolder()
    {
        // The worker records the plan's Root in ClipRoots before it writes anything under it.
        var configured = _directory.Combine("clips out") + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar;
        _configuration.ClipOutputPath = "  " + configured + "  ";

        var byUser = _planner.Plan(Bookmark(), BookmarkClipMode.Accurate);
        _configuration.OrganizeClipsByUser = false;
        var flat = _planner.Plan(Bookmark(), BookmarkClipMode.Fast);
        _configuration.ClipOutputPath = string.Empty;
        var byDefault = _planner.Plan(Bookmark(), BookmarkClipMode.Fast);

        Assert.Equal(_directory.Combine("clips out"), byUser.Root);
        Assert.Equal(Path.Combine(byUser.Root, "alice"), Path.GetDirectoryName(byUser.OutputPath));
        Assert.Equal(_directory.Combine("clips out"), flat.Root);
        Assert.Equal(flat.Root, Path.GetDirectoryName(flat.OutputPath));
        Assert.Equal(_directory.Combine("data", "bookmarks", "clips"), byDefault.Root);
        Assert.True(Path.IsPathFullyQualified(byUser.OutputPath));
    }

    [Fact]
    public void Plan_AClipFolderThatIsntAPath_IsRefused()
    {
        _configuration.ClipOutputPath = "C:\\clips\0bad";

        var ex = Assert.Throws<ClipExtractionException>(() => _planner.Plan(Bookmark(), BookmarkClipMode.Accurate));

        Assert.Equal("The clip folder in the plugin's settings isn't a valid folder path.", ex.Message);
    }

    [Fact]
    public void Plan_UsesTheAudioTrackThatWasPlaying()
    {
        var plan = _planner.Plan(Bookmark() with { AudioStreamIndex = 2 }, BookmarkClipMode.Accurate);

        Assert.Equal(2, plan.Command.AudioStreamIndex);
    }

    [Fact]
    public void Plan_FallsBackToTheDefaultAudioTrack()
    {
        _source.DefaultAudioStreamIndex = 2;

        Assert.Equal(2, _planner.Plan(Bookmark(), BookmarkClipMode.Accurate).Command.AudioStreamIndex);
        Assert.Equal(2, _planner.Plan(Bookmark() with { AudioStreamIndex = 7 }, BookmarkClipMode.Accurate).Command.AudioStreamIndex);
    }

    [Fact]
    public void Plan_WithoutDefaultAudio_UsesDefaultFlaggedTrack()
    {
        Assert.Equal(1, _planner.Plan(Bookmark(), BookmarkClipMode.Accurate).Command.AudioStreamIndex);
    }

    [Fact]
    public void Plan_IgnoresExternalAudioTracks()
    {
        // Only the file's own streams are passed to ffmpeg, so an external track falls back to the default.
        _source.MediaStreams =
        [
            new MediaStream { Index = 0, Type = MediaStreamType.Video },
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, IsDefault = true },
            new MediaStream { Index = 2, Type = MediaStreamType.Audio, IsExternal = true, Path = "external.ac3" }
        ];

        var plan = _planner.Plan(Bookmark() with { AudioStreamIndex = 2 }, BookmarkClipMode.Accurate);

        Assert.Equal(1, plan.Command.AudioStreamIndex);
    }

    [Fact]
    public void Plan_PicksTheBookmarkedVersion()
    {
        var other = new MediaSourceInfo
        {
            Id = "source-2",
            Path = _directory.CreateFile(Path.Combine("media", "Heat (1995) - 4K.mkv")),
            Protocol = MediaProtocol.File
        };
        _mediaSourceManager.Setup(m => m.GetStaticMediaSources(_movie, false, _user)).Returns(() => [_source, other]);

        Assert.Equal(other.Path, _planner.Plan(Bookmark() with { MediaSourceId = "SOURCE-2" }, BookmarkClipMode.Accurate).Command.InputPath);
        Assert.Equal(_source.Path, _planner.Plan(Bookmark() with { MediaSourceId = "gone" }, BookmarkClipMode.Accurate).Command.InputPath);
    }

    [Theory]
    [InlineData(VideoRangeType.HDR10, true, true, "TonemapX")]
    [InlineData(VideoRangeType.HDR10, false, true, "ZScale")]
    [InlineData(VideoRangeType.DOVI, false, true, "None")]
    [InlineData(VideoRangeType.DOVI, true, false, "TonemapX")]
    [InlineData(VideoRangeType.HDR10, false, false, "None")]
    public void Plan_PicksToneMappingForHdr(VideoRangeType rangeType, bool hasTonemapX, bool hasZScale, string expected)
    {
        MakeHdr(_source.MediaStreams[0], rangeType);
        _mediaEncoder.Setup(m => m.SupportsFilter("tonemapx")).Returns(hasTonemapX);
        _mediaEncoder.Setup(m => m.SupportsFilter("zscale")).Returns(hasZScale);

        var plan = _planner.Plan(Bookmark(), BookmarkClipMode.Accurate);

        Assert.Equal(expected, plan.Command.Tonemap.ToString());
        Assert.Equal(rangeType == VideoRangeType.DOVI, plan.Command.IsDolbyVision);
    }

    [Fact]
    public void Plan_SdrSource_IsNotToneMapped()
    {
        _mediaEncoder.Setup(m => m.SupportsFilter(It.IsAny<string>())).Returns(true);

        Assert.Equal(TonemapMode.None, _planner.Plan(Bookmark(), BookmarkClipMode.Accurate).Command.Tonemap);
    }

    [Fact]
    public void Plan_WithoutEnd_Throws()
    {
        Assert.Throws<ClipExtractionException>(() => _planner.Plan(Bookmark() with { EndTicks = null }, BookmarkClipMode.Accurate));
    }

    [Fact]
    public void Plan_Disc_Throws()
    {
        _source.VideoType = VideoType.BluRay;

        var ex = Assert.Throws<ClipExtractionException>(() => _planner.Plan(Bookmark(), BookmarkClipMode.Accurate));
        Assert.Contains("regular video files", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_RemoteStream_Throws()
    {
        _source.Protocol = MediaProtocol.Http;

        Assert.Throws<ClipExtractionException>(() => _planner.Plan(Bookmark(), BookmarkClipMode.Accurate));
    }

    [Fact]
    public void Plan_MissingFile_Throws()
    {
        File.Delete(_source.Path);

        var ex = Assert.Throws<ClipExtractionException>(() => _planner.Plan(Bookmark(), BookmarkClipMode.Accurate));
        Assert.Contains("can't be found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_ItemNoLongerVisible_Throws()
    {
        _libraryManager.Setup(m => m.GetItemById<Video>(_movie.Id, _user)).Returns((Video?)null);

        Assert.Throws<ClipExtractionException>(() => _planner.Plan(Bookmark(), BookmarkClipMode.Accurate));
    }

    [Fact]
    public void Plan_NoFfmpeg_Throws()
    {
        _mediaEncoder.SetupGet(m => m.EncoderPath).Returns(string.Empty);

        Assert.Throws<ClipExtractionException>(() => _planner.Plan(Bookmark(), BookmarkClipMode.Accurate));
    }

    [Theory]
    [InlineData("Vault", "Bank job", "Heat (1995) - Vault")]
    [InlineData(null, "Bank job", "Heat (1995)")]
    [InlineData("  ", "Bank job", "Heat (1995)")]
    [InlineData(null, "secret plans\nline two", "Heat (1995)")]
    public void Plan_FileNameAndTitle_UseTheName_NeverTheNote(string? name, string? note, string stem)
    {
        // Since 1.2.0 a clip's file name and its title metadata take the bookmark's name, never its note
        // (admins see the file names and paths, and download the files); a bookmark without a name gives the video's name and
        // the range alone.
        var plan = _planner.Plan(Bookmark() with { Name = name, Note = note }, BookmarkClipMode.Accurate);

        Assert.Equal($"{stem} [00.01.00-00.01.30].mp4", Path.GetFileName(plan.OutputPath));
        Assert.Equal($"{stem} (0:01:00-0:01:30)", plan.Command.Title);
        foreach (var word in (note ?? string.Empty).Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.DoesNotContain(word, plan.OutputPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(word, plan.TempPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(word, plan.Command.Title!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Plan_NoNameOrNote_UsesTheItemAlone()
    {
        var plan = _planner.Plan(Bookmark() with { Name = null, Note = null }, BookmarkClipMode.Accurate);

        Assert.Equal("Heat (1995) [00.01.00-00.01.30].mp4", Path.GetFileName(plan.OutputPath));
        Assert.Equal("Heat (1995) (0:01:00-0:01:30)", plan.Command.Title);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Plan_AnAdoptedRecord_NeverLooksUpAnItem(bool emptyId)
    {
        // HasLibraryItem guards every lookup; 10.11.5 throws for GetItemById(Guid.Empty).
        _libraryManager.Setup(m => m.GetItemById<Video>(It.IsAny<Guid>(), It.IsAny<User?>())).Throws(new ArgumentException("Guid can't be empty"));
        var adopted = Bookmark() with { Origin = BookmarkOrigin.Adopted, ItemId = emptyId ? Guid.Empty : _movie.Id };

        var ex = Assert.Throws<ClipExtractionException>(() => _planner.Plan(adopted, BookmarkClipMode.Accurate));

        Assert.Equal("The bookmarked video is no longer in the library.", ex.Message);
        _libraryManager.Verify(m => m.GetItemById<Video>(It.IsAny<Guid>(), It.IsAny<User?>()), Times.Never);
    }

    public void Dispose() => _directory.Dispose();

    // VideoRange/VideoRangeType are computed from the stream's colour metadata.
    private static void MakeHdr(MediaStream stream, VideoRangeType rangeType)
    {
        if (rangeType == VideoRangeType.DOVI)
        {
            stream.DvProfile = 5;
            stream.RpuPresentFlag = 1;
            stream.BlPresentFlag = 1;
            stream.DvBlSignalCompatibilityId = 0;
        }
        else
        {
            stream.ColorTransfer = "smpte2084";
            stream.ColorPrimaries = "bt2020";
        }

        Assert.Equal(VideoRange.HDR, stream.VideoRange);
        Assert.Equal(rangeType, stream.VideoRangeType);
    }

    [Fact]
    public async Task Plan_NeverTakesANameARecordNames_EvenWithItsFileGone_NorOneWaitingToBeDeleted_NorARenamesPath()
    {
        // A bug since 1.0, found while building 1.2.0's rename: a name free on disk that a "File missing" record names went to
        // a new clip, so two records named one file, and the first one's Delete, Forget or remake deleted the second's clip.
        var data = _directory.Combine("data", "bookmarks");
        var folder = Path.Combine(data, "clips", "alice");
        string Named(string suffix) => Path.Combine(folder, "Heat (1995) - Bank job [00.01.00-00.01.30]" + suffix + ".mp4");
        using var store = ClipFileSupport.StoreWithClipRoots(Path.Combine(data, "bookmarks.json"), Path.Combine(data, "clips"));
        await store.AddAsync(Bookmark() with { Id = Guid.NewGuid(), Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = Named(string.Empty), FileSize = 4 } }, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Named(string.Empty)));

        // 1.1's rule (a file there, or its .partial) took it.
        Assert.Equal(Named(string.Empty), _planner.Plan(Bookmark(), BookmarkClipMode.Accurate).OutputPath);
        Assert.Equal(Named(" (2)"), Planner(ClipFileSupport.Ops(store)).Plan(Bookmark(), BookmarkClipMode.Accurate).OutputPath);

        await store.AddPendingFileDeleteAsync(Named(" (2)"), TestContext.Current.CancellationToken);
        Assert.Equal(Named(" (3)"), Planner(ClipFileSupport.Ops(store)).Plan(Bookmark(), BookmarkClipMode.Accurate).OutputPath);

        new ClipRenameJournal(data, PhysicalClipFileSystem.Instance, NullLogger.Instance).Add(new ClipRenameEntry { BookmarkId = Guid.NewGuid(), From = Path.Combine(folder, "elsewhere.mp4"), To = Named(" (3)") });
        Assert.Equal(Named(" (4)"), Planner(ClipFileSupport.Ops(store)).Plan(Bookmark(), BookmarkClipMode.Accurate).OutputPath);
    }

    [Fact]
    public async Task Plan_NeverTakesANameARecordNames_EvenWithItsFileGone_NorOneWaitingToBeDeleted()
    {
        // Bookmarks 1.1.3 (the planner's part of the fix above, a bug since 1.0): a name free on disk that a "File missing"
        // record names went to a new clip, so two records named one file, and the first one's delete or remake
        // deleted the second's clip (reproduced on 1.1.2).
        var data = _directory.Combine("data", "bookmarks");
        var folder = Path.Combine(data, "clips", "alice");
        string Named(string suffix) => Path.Combine(folder, "Heat (1995) - Bank job [00.01.00-00.01.30]" + suffix + ".mp4");
        using var store = ClipFileSupport.StoreWithClipRoots(Path.Combine(data, "bookmarks.json"), Path.Combine(data, "clips"));
        await store.AddAsync(Bookmark() with { Id = Guid.NewGuid(), Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = Named(string.Empty), FileSize = 4 } }, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Named(string.Empty)));

        // 1.1.2's rule (a file there, or its .partial) took it.
        Assert.Equal(Named(string.Empty), _planner.Plan(Bookmark(), BookmarkClipMode.Accurate).OutputPath);
        Assert.Equal(Named(" (2)"), Planner(ClipFileSupport.Ops(store)).Plan(Bookmark(), BookmarkClipMode.Accurate).OutputPath);

        // Another user's record takes it too, and a file waiting to be deleted.
        await store.AddAsync(Bookmark() with { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = Named(" (2)"), FileSize = 4 } }, TestContext.Current.CancellationToken);
        Assert.Equal(Named(" (3)"), Planner(ClipFileSupport.Ops(store)).Plan(Bookmark(), BookmarkClipMode.Accurate).OutputPath);
        await store.AddPendingFileDeleteAsync(Named(" (3)"), TestContext.Current.CancellationToken);
        Assert.Equal(Named(" (4)"), Planner(ClipFileSupport.Ops(store)).Plan(Bookmark(), BookmarkClipMode.Accurate).OutputPath);

        // A file there and a clip being written still take a name, as in 1.1.2; on Windows a record's path in another case is
        // the same name.
        _directory.CreateFile(Path.GetRelativePath(_directory.Path, Named(" (4)")));
        _directory.CreateFile(Path.GetRelativePath(_directory.Path, Named(" (5)") + ".partial"));
        await store.AddAsync(Bookmark() with { Id = Guid.NewGuid(), Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = Named(" (6)").ToUpperInvariant(), FileSize = 4 } }, TestContext.Current.CancellationToken);
        Assert.Equal(Named(OperatingSystem.IsWindows() ? " (7)" : " (6)"), Planner(ClipFileSupport.Ops(store)).Plan(Bookmark(), BookmarkClipMode.Accurate).OutputPath);
    }

    [Fact]
    public async Task Plan_ARemakeKeepsItsOwnFilesName_ToReplaceIt_UnlessSomethingElseHoldsThatPath()
    {
        // Bookmarks 1.2.1: a remake took " (2)" (since 1.1) whether its own file was there or gone; it keeps its own file's name
        // now, to replace the file (ReplacesOwnFile), while nothing else holds that path.
        var data = _directory.Combine("data", "bookmarks");
        var previous = Path.Combine(data, "clips", "alice", "Heat (1995) - Bank job [00.01.00-00.01.30].mp4");
        var second = Path.Combine(Path.GetDirectoryName(previous)!, "Heat (1995) - Bank job [00.01.00-00.01.30] (2).mp4");
        using var store = ClipFileSupport.StoreWithClipRoots(Path.Combine(data, "bookmarks.json"), Path.Combine(data, "clips"));
        var own = Bookmark() with { Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = previous, FileSize = 4 } };
        await store.AddAsync(own, TestContext.Current.CancellationToken);
        var planner = Planner(ClipFileSupport.Ops(store));

        // Its file gone, and there.
        var plan = planner.Plan(own, BookmarkClipMode.Accurate);
        Assert.Equal((previous, true), (plan.OutputPath, plan.ReplacesOwnFile));
        _directory.CreateFile(Path.GetRelativePath(_directory.Path, previous));
        plan = planner.Plan(own, BookmarkClipMode.Accurate);
        Assert.Equal((previous, true), (plan.OutputPath, plan.ReplacesOwnFile));

        // Another mode is another name: a fresh file, nothing replaced.
        Assert.False(planner.Plan(own, BookmarkClipMode.Fast).ReplacesOwnFile);

        // A clip being written under it (its .partial), another record naming it, or a file waiting to be deleted there:
        // " (2)", nothing replaced, as before.
        _directory.CreateFile(Path.GetRelativePath(_directory.Path, previous + ".partial"));
        plan = planner.Plan(own, BookmarkClipMode.Accurate);
        Assert.Equal((second, false), (plan.OutputPath, plan.ReplacesOwnFile));
        File.Delete(previous + ".partial");

        var other = Bookmark() with { Id = Guid.NewGuid(), Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = previous, FileSize = 4 } };
        await store.AddAsync(other, TestContext.Current.CancellationToken);
        plan = Planner(ClipFileSupport.Ops(store)).Plan(own, BookmarkClipMode.Accurate);
        Assert.Equal((second, false), (plan.OutputPath, plan.ReplacesOwnFile));
        await store.RemoveAsync(other.Id, TestContext.Current.CancellationToken);

        await store.AddPendingFileDeleteAsync(previous, TestContext.Current.CancellationToken);
        plan = Planner(ClipFileSupport.Ops(store)).Plan(own, BookmarkClipMode.Accurate);
        Assert.Equal((second, false), (plan.OutputPath, plan.ReplacesOwnFile));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070015), false)] // ERROR_NOT_READY: a drive that isn't ready
    [InlineData(unchecked((int)0x80070079), false)] // ERROR_SEM_TIMEOUT: a share that doesn't answer
    [InlineData(0, true)] // access denied
    public void Plan_AClipFolderThatCantAnswerALookUp_FailsAtTheFirstName_TheClipFolderCantBeReached(int hresult, bool accessDenied)
    {
        // Found after Bookmarks 1.1.3: a look-up that fails for another reason than "not found" counted as
        // a taken name, so the planner asked about up to a thousand numbered names, each as slow as the folder, on a request's
        // thread or under the worker's name gate. Now the first one ends the plan with a plain answer.
        var data = _directory.Combine("data", "bookmarks");
        using var store = ClipFileSupport.StoreWithClipRoots(Path.Combine(data, "bookmarks.json"), Path.Combine(data, "clips"));
        var lookups = 0;
        var fileSystem = new FakeClipFileSystem
        {
            AttributesFailure = path =>
            {
                if (!Path.GetFileName(path).StartsWith("Heat (1995) - Bank job", StringComparison.Ordinal))
                {
                    return null;
                }

                lookups++;
                return accessDenied ? new UnauthorizedAccessException("Access to the path is denied.") : new IOException("The clip folder failed.", hresult);
            }
        };

        var ex = Assert.Throws<ClipExtractionException>(() => Planner(ClipFileSupport.Ops(store, fileSystem: fileSystem)).Plan(Bookmark(), BookmarkClipMode.Accurate));

        Assert.True(ex.FolderUnreachable);
        Assert.Equal(ClipFileOps.FolderUnreachableClipMessage, ex.Message);
        Assert.IsType<ClipFolderUnreachableException>(ex.InnerException);

        // Since 1.2.0 the first look-up that fails is asked once more, a pause later: two.
        Assert.Equal(2, lookups);
        Assert.False(Directory.Exists(Path.Combine(data, "clips", "alice")));
    }

    private ClipPlanner Planner(ClipFileOps files)
    {
        var applicationPaths = new Mock<IApplicationPaths>();
        applicationPaths.SetupGet(p => p.DataPath).Returns(_directory.Combine("data"));
        return new ClipPlanner(
            _libraryManager.Object,
            _userManager.Object,
            _mediaSourceManager.Object,
            _mediaEncoder.Object,
            TestData.ConfigurationProvider(_configuration),
            new BookmarkPaths(new KitPaths(applicationPaths.Object, BookmarksKit.Options)),
            files);
    }

    // Named, since 1.2.0 a clip file takes its bookmark's name alone.
    private Bookmark Bookmark() => TestData.CreateBookmark(_user.Id, 60, 90, _movie.Id) with { Name = "Bank job" };
}
