using System;
using Jellyfin.Plugin.Bookmarks.Clips;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

public class FfmpegProgressParserTests
{
    [Theory]
    [InlineData("out_time_us=1500000", 1.5)]
    [InlineData("out_time_ms=1500000", 1.5)] // microseconds, despite the name
    [InlineData("out_time=00:01:02.500000", 62.5)]
    [InlineData("out_time_us=0", 0)]
    [InlineData(" out_time_us = 250000 ", 0.25)]
    public void TryParseOutputTime_ParsesPositions(string line, double expectedSeconds)
    {
        Assert.True(FfmpegProgressParser.TryParseOutputTime(line, out var time));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), time);
    }

    [Theory]
    [InlineData("out_time_us=N/A")]
    [InlineData("out_time=N/A")]
    [InlineData("out_time=-00:00:00.021333")]
    [InlineData("out_time_us=-21333")]
    [InlineData("frame=100")]
    [InlineData("progress=continue")]
    [InlineData("=5")]
    [InlineData("")]
    [InlineData("garbage")]
    public void TryParseOutputTime_IgnoresOtherLines(string line)
    {
        Assert.False(FfmpegProgressParser.TryParseOutputTime(line, out _));
    }
}
