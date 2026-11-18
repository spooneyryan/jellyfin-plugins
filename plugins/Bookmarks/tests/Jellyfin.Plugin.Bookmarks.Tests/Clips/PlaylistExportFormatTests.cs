using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Bookmarks.Clips;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// A playlist export's format, estimate and names (since 1.5.0 / 2.3.0): the largest video's picture fitted into
/// 1920x1080 (or the clip settings' maximum height at 16:9), the frame rate most of the running time has, the size and time
/// estimates, the warning over 3 hours or 20 GiB, the file name from the playlist's name with the clip rules, the
/// <c>.partial</c> the janitor knows and the poster beside it.
/// </summary>
public sealed class PlaylistExportFormatTests
{
    public static TheoryData<int[][], int, int, int> Sizes => new()
    {
        // The largest picture, as it is when it fits.
        { [[640, 360], [320, 240], [1280, 720]], 0, 1280, 720 },
        { [[1440, 1080], [640, 480]], 0, 1440, 1080 },

        // Larger than 1080p: fitted, its shape kept.
        { [[3840, 2160], [1280, 720]], 0, 1920, 1080 },
        { [[4096, 1716]], 0, 1920, 804 },
        { [[1080, 1920]], 0, 608, 1080 },

        // The clip settings' maximum height, as a 16:9 box.
        { [[1920, 1080]], 720, 1280, 720 },
        { [[1920, 1080]], 480, 854, 480 },
        { [[640, 360]], 480, 640, 360 },

        // No size known: the box.
        { [], 0, 1920, 1080 },
        { [], 720, 1280, 720 }
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void TheSize_IsTheLargestVideosFittedIntoTheBox_EvenAndShapeKept(int[][] sizes, int maxHeight, int width, int height)
    {
        var items = new List<PlaylistExportItem>();
        foreach (var size in sizes)
        {
            items.Add(Item(10, size[0], size[1], 25));
        }

        Assert.Equal((width, height), PlaylistExportFormat.ChooseSize(items, maxHeight));
    }

    [Fact]
    public void TheFrameRate_IsTheOneMostOfTheRunningTimeHas_SnappedToTheStandardOnes()
    {
        // 3 s at 25, 2 s at 30, 4 s at 23.976: the 23.976 video is the longest.
        Assert.Equal((24000, 1001), PlaylistExportFormat.ChooseFrameRate([Item(3, 640, 360, 25), Item(2, 320, 240, 30), Item(4, 1280, 720, 23.976)]));

        // Two rates as long: the higher one.
        Assert.Equal((30, 1), PlaylistExportFormat.ChooseFrameRate([Item(2, 640, 360, 25), Item(2, 640, 360, 30)]));

        // Many short videos at one rate beat one longer at another.
        Assert.Equal((25, 1), PlaylistExportFormat.ChooseFrameRate([Item(3, 640, 360, 25), Item(3, 640, 360, 25), Item(5, 640, 360, 60)]));

        // None known: 30.
        Assert.Equal((30, 1), PlaylistExportFormat.ChooseFrameRate([Item(3, 640, 360, null)]));
        Assert.Equal((30, 1), PlaylistExportFormat.ChooseFrameRate([]));
    }

    [Theory]
    [InlineData(23.976, 24000, 1001)]
    [InlineData(23.9760246, 24000, 1001)]
    [InlineData(29.97, 30000, 1001)]
    [InlineData(59.94, 60000, 1001)]
    [InlineData(24, 24, 1)]
    [InlineData(25.0001, 25, 1)]
    [InlineData(50, 50, 1)]
    [InlineData(60, 60, 1)]
    [InlineData(119.88, 60, 1)]
    [InlineData(240, 60, 1)]
    [InlineData(12.5, 12500, 1000)]
    public void TheFrameRate_IsSnapped_AndCappedAt60(double rate, int numerator, int denominator)
    {
        Assert.Equal((numerator, denominator), PlaylistExportFormat.Snap(rate));
    }

    [Fact]
    public void TheFormat_WritesItsFrameRateForFfmpeg()
    {
        Assert.Equal("24000/1001", new PlaylistExportFormat(1280, 720, 24000, 1001).FrameRateText);
        Assert.Equal("25", new PlaylistExportFormat(1280, 720, 25, 1).FrameRateText);
        Assert.Equal(new PlaylistExportFormat(1280, 720, 24000, 1001), PlaylistExportFormat.Choose([Item(3, 640, 360, 25), Item(4, 1280, 720, 23.976)], 0));
    }

    [Fact]
    public void TheSizeEstimate_IsTheClipBudgetAtTheExportsHeight_CappedByTheSourceWhenNotUpscaled_PlusAudio_PlusTenPercent()
    {
        var format = new PlaylistExportFormat(1280, 720, 25, 1);
        var encoding = new ClipEncodingOptions();

        // 100 s at 720p: 5 Mb/s of video and 192 kb/s of audio.
        Assert.Equal(Bytes((5_000_000 + 192_000) * 100.0), PlaylistExportEstimator.EstimateBytes([Item(100, 1280, 720, 25)], format, encoding));

        // A 1 Mb/s source at the export's height: 1.2 Mb/s at most.
        Assert.Equal(Bytes((1_200_000 + 192_000) * 100.0), PlaylistExportEstimator.EstimateBytes([Item(100, 1280, 720, 25) with { SourceBitrate = 1_000_000 }], format, encoding));

        // The same source upscaled: the budget.
        Assert.Equal(Bytes((5_000_000 + 192_000) * 100.0), PlaylistExportEstimator.EstimateBytes([Item(100, 640, 360, 25) with { SourceBitrate = 1_000_000 }], format, encoding));

        // The videos add up; the audio bitrate is the clip setting's.
        Assert.Equal(
            Bytes(((5_000_000 + 128_000) * 60.0) + ((5_000_000 + 128_000) * 40.0)),
            PlaylistExportEstimator.EstimateBytes([Item(60, 1280, 720, 25), Item(40, 320, 240, 30)], format, new ClipEncodingOptions { AudioBitrateKbps = 128 }));

        Assert.Equal(0, PlaylistExportEstimator.EstimateBytes([], format, encoding));
    }

    [Fact]
    public void TheTimeEstimate_GoesByTheExportsHeightAndThePreset_SlowerForToneMapping()
    {
        // veryfast (the default) at 720p: 6 times real time, plus 10 %.
        var hd = new PlaylistExportFormat(1280, 720, 25, 1);
        Assert.Equal(TimeSpan.FromSeconds(11), PlaylistExportEstimator.EstimateTime([Item(60, 1280, 720, 25)], hd, new ClipEncodingOptions()));

        // ultrafast: 3 times faster; slow: 4 times slower.
        Assert.Equal(TimeSpan.FromSeconds(4), PlaylistExportEstimator.EstimateTime([Item(60, 1280, 720, 25)], hd, new ClipEncodingOptions { Preset = "ultrafast" }));
        Assert.Equal(TimeSpan.FromSeconds(44), PlaylistExportEstimator.EstimateTime([Item(60, 1280, 720, 25)], hd, new ClipEncodingOptions { Preset = "slow" }));

        // 1080p at half the speed; an HDR video tone-mapped at half again.
        var fullHd = new PlaylistExportFormat(1920, 1080, 25, 1);
        Assert.Equal(TimeSpan.FromSeconds(22), PlaylistExportEstimator.EstimateTime([Item(60, 1920, 1080, 25)], fullHd, new ClipEncodingOptions()));
        Assert.Equal(TimeSpan.FromSeconds(44), PlaylistExportEstimator.EstimateTime([Item(60, 1920, 1080, 25) with { Tonemap = TonemapMode.TonemapX }], fullHd, new ClipEncodingOptions()));
    }

    [Fact]
    public void AnExport_IsLarge_OverThreeHoursOr20GiB()
    {
        Assert.False(PlaylistExportEstimator.IsLarge(TimeSpan.FromHours(3), 20L * 1024 * 1024 * 1024));
        Assert.True(PlaylistExportEstimator.IsLarge(TimeSpan.FromHours(3).Add(TimeSpan.FromSeconds(1)), 1));
        Assert.True(PlaylistExportEstimator.IsLarge(TimeSpan.FromMinutes(1), (20L * 1024 * 1024 * 1024) + 1));
    }

    [Theory]
    [InlineData("Road trip", "Road trip.mp4")]
    [InlineData("Best of [2024]", "Best of (2024).mp4")]
    [InlineData("Heat [00.01.00-00.02.00]", "Heat (00.01.00-00.02.00).mp4")]
    [InlineData("Bob: The Builder", "Bob - The Builder.mp4")]
    [InlineData("con", "con_.mp4")]
    [InlineData("  ", "Playlist.mp4")]
    [InlineData(null, "Playlist.mp4")]
    public void TheFileName_IsThePlaylistsName_WithTheClipRules_AndNeverAClipsName(string? playlistName, string expected)
    {
        var name = PlaylistExportNaming.BuildFileName(playlistName);

        Assert.Equal(expected, name);
        Assert.False(ClipNamePattern.IsClipFileName(name));
    }

    [Fact]
    public void ATakenName_GetsANumber()
    {
        var taken = new HashSet<string> { Path.Combine("clips", "Road trip.mp4"), Path.Combine("clips", "Road trip (2).mp4") };

        Assert.Equal(Path.Combine("clips", "Road trip (3).mp4"), ClipFileNaming.MakeUnique("clips", PlaylistExportNaming.BuildFileName("Road trip"), taken.Contains));
    }

    [Fact]
    public void ThePartial_IsNamedAsTheJanitorKnows_AndThePosterIsBesideTheExport()
    {
        var output = Path.Combine("clips", "alice", "Road trip (2).mp4");
        var id = Guid.Parse("0123456789abcdef0123456789abcdef");

        var partial = PlaylistExportNaming.BuildTempPath(output, TimeSpan.FromSeconds(3725), id);

        Assert.Equal(Path.Combine("clips", "alice", "Road trip (2) [00.00.00-01.02.05] (0123456789abcdef0123456789abcdef).mp4.partial"), partial);
        Assert.True(ClipNamePattern.IsPartialFileName(Path.GetFileName(partial)));
        Assert.Equal(Path.Combine("clips", "alice", "Road trip (2)-poster.jpg"), PlaylistExportNaming.BuildPosterPath(output, ".jpg"));
        Assert.Null(ClipArtworkNames.Parse(Path.GetFileName(PlaylistExportNaming.BuildPosterPath(output, ".jpg"))));
    }

    private static long Bytes(double bits) => (long)Math.Ceiling(Math.Round(bits / 8 * 1.1, 3));

    private static PlaylistExportItem Item(double seconds, int width, int height, double? frameRate) => new()
    {
        ItemId = Guid.NewGuid(),
        Name = "Video",
        InputPath = "video.mkv",
        Duration = TimeSpan.FromSeconds(seconds),
        VideoStreamIndex = 0,
        AudioStreamIndex = 1,
        Width = width,
        Height = height,
        FrameRate = frameRate
    };
}
