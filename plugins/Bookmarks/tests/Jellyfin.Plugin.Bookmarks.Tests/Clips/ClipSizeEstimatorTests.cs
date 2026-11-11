using System;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The free-space guard's estimator, for Fast and Accurate at several heights and bitrates: Fast = bitrate × length / 8 × 1.05 (the stated bitrate, else size × 8 / runtime); Accurate = a video budget by
/// output height (3, 5, 10, 25 Mbit/s), capped at 1.2 × the source's video bitrate, plus the audio bitrate, × length / 8 ×
/// 1.1, the output height following MaxVideoHeight.
/// </summary>
public sealed class ClipSizeEstimatorTests
{
    [Fact]
    public void Fast_IsTheSourcesBitrateOverTheLength_PlusFivePercent()
    {
        var source = new MediaSourceInfo { Bitrate = 8_000_000 };

        // 8 Mbit/s × 30 s = 30,000,000 bytes, × 1.05.
        Assert.Equal(31_500_000, ClipSizeEstimator.Estimate(Command(BookmarkClipMode.Fast, 30), source, Video(1080)));
    }

    [Fact]
    public void Fast_WithoutAStatedBitrate_UsesTheFileSizeOverTheRuntime()
    {
        // 1 GB over 1,000 s is 8.589934592 Mbit/s; 10 s of it is 10,737,418.24 bytes, × 1.05 = 11,274,289.15 → 11,274,290.
        var source = new MediaSourceInfo { Size = 1L << 30, RunTimeTicks = TimeSpan.FromSeconds(1000).Ticks };

        Assert.Equal(11_274_290, ClipSizeEstimator.Estimate(Command(BookmarkClipMode.Fast, 10), source, Video(1080)));
    }

    [Fact]
    public void Fast_WithNeitherBitrateNorSize_HasNoEstimate()
        => Assert.Null(ClipSizeEstimator.Estimate(Command(BookmarkClipMode.Fast, 10), new MediaSourceInfo { Size = 5000 }, Video(1080)));

    [Theory]
    [InlineData(480, 3_000_000)]
    [InlineData(576, 3_000_000)]
    [InlineData(577, 5_000_000)]
    [InlineData(720, 5_000_000)]
    [InlineData(1080, 10_000_000)]
    [InlineData(1440, 25_000_000)]
    [InlineData(2160, 25_000_000)]
    public void Accurate_IsTheBudgetOfTheOutputHeight_PlusAudio_PlusTenPercent(int height, int budget)
    {
        // No source bitrate known, so no cap; 192 kbit/s audio; 20 s.
        var expected = Expected(budget + 192_000.0, 20, 1.1);

        Assert.Equal(expected, ClipSizeEstimator.Estimate(Command(BookmarkClipMode.Accurate, 20, sourceHeight: height), new MediaSourceInfo(), Video(height)));
    }

    [Fact]
    public void Accurate_IsCappedAt120PercentOfTheSourcesVideoBitrate()
    {
        // A 1080p source at 2 Mbit/s: the budget 10 Mbit/s is capped at 2.4 Mbit/s.
        var video = Video(1080);
        video.BitRate = 2_000_000;

        var estimate = ClipSizeEstimator.Estimate(Command(BookmarkClipMode.Accurate, 20, sourceHeight: 1080), new MediaSourceInfo { Bitrate = 50_000_000 }, video);

        Assert.Equal(Expected((1.2 * 2_000_000) + 192_000, 20, 1.1), estimate);
    }

    [Fact]
    public void Accurate_WithoutTheStreamsBitrate_IsCappedByTheSourcesBitrate()
    {
        var estimate = ClipSizeEstimator.Estimate(Command(BookmarkClipMode.Accurate, 20, sourceHeight: 1080), new MediaSourceInfo { Bitrate = 3_000_000 }, Video(1080));

        Assert.Equal(Expected((1.2 * 3_000_000) + 192_000, 20, 1.1), estimate);
    }

    [Theory]
    [InlineData(2160, 720, 5_000_000)] // scaled down to 720 lines
    [InlineData(480, 1080, 3_000_000)] // never scaled up
    [InlineData(2160, 0, 25_000_000)] // 0 keeps the source's height
    public void Accurate_FollowsMaxVideoHeight(int sourceHeight, int maxHeight, int budget)
    {
        var estimate = ClipSizeEstimator.Estimate(Command(BookmarkClipMode.Accurate, 10, sourceHeight: sourceHeight, maxHeight: maxHeight), new MediaSourceInfo(), Video(sourceHeight));

        Assert.Equal(Expected(budget + 192_000.0, 10, 1.1), estimate);
    }

    [Theory]
    [InlineData(0, 10_000_000)] // unknown height: 1080 lines
    [InlineData(720, 5_000_000)] // unknown height with a maximum: the maximum
    public void Accurate_OfASourceOfUnknownHeight(int maxHeight, int budget)
    {
        var estimate = ClipSizeEstimator.Estimate(Command(BookmarkClipMode.Accurate, 10, sourceHeight: null, maxHeight: maxHeight), new MediaSourceInfo(), Video(null));

        Assert.Equal(Expected(budget + 192_000.0, 10, 1.1), estimate);
    }

    [Fact]
    public void Accurate_WithoutAudio_HasNoAudioPart()
    {
        // A video map and no audio map: the clip has no audio (FfmpegClipCommandBuilder's rule).
        var command = Command(BookmarkClipMode.Accurate, 10, sourceHeight: 720) with { AudioStreamIndex = null };

        Assert.Equal(Expected(5_000_000, 10, 1.1), ClipSizeEstimator.Estimate(command, new MediaSourceInfo(), Video(720)));
    }

    [Fact]
    public void Accurate_OfASourceWithoutVideo_IsItsAudio()
    {
        var command = Command(BookmarkClipMode.Accurate, 10) with { VideoStreamIndex = null };

        Assert.Equal(Expected(320_000, 10, 1.1), ClipSizeEstimator.Estimate(command with { Encoding = new ClipEncodingOptions { AudioBitrateKbps = 320 } }, new MediaSourceInfo(), null));
    }

    [Fact]
    public void AnEmptyRange_IsNothing()
        => Assert.Equal(0, ClipSizeEstimator.Estimate(Command(BookmarkClipMode.Accurate, 0), new MediaSourceInfo(), Video(1080)));

    // The estimator's own arithmetic, in its order: bits per second × seconds / 8 × the overhead, rounded up.
    private static long Expected(double bitsPerSecond, double seconds, double overhead) => (long)Math.Ceiling(bitsPerSecond * seconds / 8 * overhead);

    private static MediaStream Video(int? height) => new() { Type = MediaStreamType.Video, Index = 0, Height = height };

    private static ClipCommand Command(BookmarkClipMode mode, double seconds, int? sourceHeight = 1080, int maxHeight = 0) => new()
    {
        InputPath = "source.mkv",
        OutputPath = "clip.partial",
        Mode = mode,
        Start = TimeSpan.Zero,
        Duration = TimeSpan.FromSeconds(seconds),
        VideoStreamIndex = 0,
        AudioStreamIndex = 1,
        SourceHeight = sourceHeight,
        Encoding = new ClipEncodingOptions { MaxHeight = maxHeight, AudioBitrateKbps = 192 }
    };
}
