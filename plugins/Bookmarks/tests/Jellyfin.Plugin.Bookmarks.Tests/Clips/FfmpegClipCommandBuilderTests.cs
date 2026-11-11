using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

public class FfmpegClipCommandBuilderTests
{
    private const string InputPath = @"Q:\Video\Heat (1995)\Heat (1995).mkv";
    private const string OutputPath = @"C:\clips\Heat (1995) [01.02.03-01.03.33].mp4.partial";

    [Fact]
    public void Build_Accurate_SeeksBeforeInputAndReencodesToMp4()
    {
        var args = FfmpegClipCommandBuilder.Build(CreateCommand());

        Assert.Equal("3723.5", ValueAfter(args, "-ss"));
        Assert.True(IndexOf(args, "-ss") < IndexOf(args, "-i"), "-ss must come before -i for fast input seeking");
        Assert.Equal("file:" + InputPath, ValueAfter(args, "-i"));
        Assert.Equal("90.25", ValueAfter(args, "-t"));
        Assert.Equal("libx264", ValueAfter(args, "-c:v"));
        Assert.Equal("veryfast", ValueAfter(args, "-preset"));
        Assert.Equal("20", ValueAfter(args, "-crf"));
        Assert.Equal("aac", ValueAfter(args, "-c:a"));
        Assert.Equal("192k", ValueAfter(args, "-b:a"));
        Assert.Equal("2", ValueAfter(args, "-ac"));
        Assert.Equal("+faststart", ValueAfter(args, "-movflags"));
        Assert.Equal("mp4", ValueAfter(args, "-f"));
        Assert.Equal("file:" + OutputPath, args[^1]);
    }

    [Fact]
    public void Build_ReportsProgressAndNeverReadsStdin()
    {
        var args = FfmpegClipCommandBuilder.Build(CreateCommand());

        Assert.Equal("pipe:1", ValueAfter(args, "-progress"));
        Assert.Contains("-nostdin", args);
        Assert.Contains("-y", args);
    }

    [Fact]
    public void Build_MapsSelectedStreamsAndDropsTheRest()
    {
        var args = FfmpegClipCommandBuilder.Build(CreateCommand() with { VideoStreamIndex = 0, AudioStreamIndex = 2 });

        var maps = args.Select((arg, i) => (arg, i)).Where(x => x.arg == "-map").Select(x => args[x.i + 1]).ToList();
        Assert.Equal(new[] { "0:0", "0:2" }, maps);
        Assert.Contains("-sn", args);
        Assert.Contains("-dn", args);
        Assert.Equal("-1", ValueAfter(args, "-map_chapters"));
    }

    [Fact]
    public void Build_Fast_CopiesStreamsIntoMatroska()
    {
        var args = FfmpegClipCommandBuilder.Build(CreateCommand(BookmarkClipMode.Fast));

        Assert.Equal("copy", ValueAfter(args, "-c"));
        Assert.Equal("make_zero", ValueAfter(args, "-avoid_negative_ts"));
        Assert.Equal("matroska", ValueAfter(args, "-f"));
        Assert.DoesNotContain("libx264", args);
        Assert.DoesNotContain("-vf", args);
    }

    [Fact]
    public void Build_WithoutAudioStream_OmitsAudioOptions()
    {
        var args = FfmpegClipCommandBuilder.Build(CreateCommand() with { AudioStreamIndex = null });

        Assert.DoesNotContain("-c:a", args);
        Assert.Single(args, a => a == "-map");
    }

    [Fact]
    public void Build_WithUnknownStreams_LetsFfmpegChooseAndKeepsAudio()
    {
        var args = FfmpegClipCommandBuilder.Build(CreateCommand() with { VideoStreamIndex = null, AudioStreamIndex = null });

        Assert.DoesNotContain("-map", args);
        Assert.Equal("aac", ValueAfter(args, "-c:a"));
    }

    [Fact]
    public void Build_WithTitle_WritesTitleMetadata()
    {
        var args = FfmpegClipCommandBuilder.Build(CreateCommand() with { Title = "Heat (1995) - Bank job" });

        Assert.Equal("title=Heat (1995) - Bank job", ValueAfter(args, "-metadata"));
    }

    [Fact]
    public void Build_UsesInvariantDecimalSeparator()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var args = FfmpegClipCommandBuilder.Build(CreateCommand());

            Assert.Equal("3723.5", ValueAfter(args, "-ss"));
            Assert.Equal("90.25", ValueAfter(args, "-t"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Build_WithTonemapping_TagsOutputAsBt709()
    {
        var args = FfmpegClipCommandBuilder.Build(CreateCommand() with { Tonemap = TonemapMode.TonemapX });

        Assert.Equal("bt709", ValueAfter(args, "-color_primaries"));
        Assert.Equal("bt709", ValueAfter(args, "-color_trc"));
        Assert.Equal("bt709", ValueAfter(args, "-colorspace"));
    }

    [Fact]
    public void BuildVideoFilter_Default_KeepsEvenDimensionsAnd8BitOutput()
    {
        var filter = FfmpegClipCommandBuilder.BuildVideoFilter(CreateCommand());

        Assert.Equal("scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p", filter);
    }

    [Fact]
    public void BuildVideoFilter_Interlaced_DeinterlacesFirst()
    {
        var filter = FfmpegClipCommandBuilder.BuildVideoFilter(CreateCommand() with { IsInterlaced = true });

        Assert.StartsWith("bwdif=0:-1:0,", filter, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1080, 720, "scale=-2:720")]
    [InlineData(720, 1080, "scale=trunc(iw/2)*2:trunc(ih/2)*2")]
    [InlineData(1080, 0, "scale=trunc(iw/2)*2:trunc(ih/2)*2")]
    public void BuildVideoFilter_OnlyDownscales(int sourceHeight, int maxHeight, string expectedScale)
    {
        var command = CreateCommand() with
        {
            SourceHeight = sourceHeight,
            Encoding = new ClipEncodingOptions { MaxHeight = maxHeight }
        };

        var filter = FfmpegClipCommandBuilder.BuildVideoFilter(command);

        Assert.Contains(expectedScale + ",", filter, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "peak=100")]
    [InlineData(true, "peak=400")]
    public void BuildVideoFilter_TonemapX_UsesDolbyVisionPeak(bool isDolbyVision, string expectedPeak)
    {
        var command = CreateCommand() with { Tonemap = TonemapMode.TonemapX, IsDolbyVision = isDolbyVision };

        var filter = FfmpegClipCommandBuilder.BuildVideoFilter(command);

        Assert.Contains("tonemapx=tonemap=bt2390", filter, StringComparison.Ordinal);
        Assert.Contains(expectedPeak, filter, StringComparison.Ordinal);
        Assert.EndsWith("format=yuv420p", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildVideoFilter_ZScale_UsesStockTonemapChain()
    {
        var filter = FfmpegClipCommandBuilder.BuildVideoFilter(CreateCommand() with { Tonemap = TonemapMode.ZScale });

        Assert.Contains("zscale=t=linear:npl=100", filter, StringComparison.Ordinal);
        Assert.Contains("tonemap=tonemap=hable", filter, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1.5, "1.5")]
    [InlineData(3723.0004, "3723")]
    [InlineData(-2, "0")]
    public void FormatSeconds_FormatsMillisecondPrecision(double seconds, string expected)
    {
        Assert.Equal(expected, FfmpegClipCommandBuilder.FormatSeconds(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void ClipEncodingOptions_FromConfiguration_ClampsInvalidValues()
    {
        var options = ClipEncodingOptions.FromConfiguration(new Configuration.PluginConfiguration
        {
            VideoPreset = "LUDICROUS",
            VideoCrf = 99,
            MaxVideoHeight = -5,
            AudioBitrateKbps = 5,
            AudioChannels = 20
        });

        Assert.Equal("veryfast", options.Preset);
        Assert.Equal(51, options.Crf);
        Assert.Equal(0, options.MaxHeight);
        Assert.Equal(32, options.AudioBitrateKbps);
        Assert.Equal(8, options.AudioChannels);
    }

    [Fact]
    public void ClipEncodingOptions_FromConfiguration_AcceptsPresetInAnyCase()
    {
        var options = ClipEncodingOptions.FromConfiguration(new Configuration.PluginConfiguration { VideoPreset = " Medium " });

        Assert.Equal("medium", options.Preset);
    }

    private static ClipCommand CreateCommand(BookmarkClipMode mode = BookmarkClipMode.Accurate) => new()
    {
        InputPath = InputPath,
        OutputPath = OutputPath,
        Mode = mode,
        Start = TimeSpan.FromSeconds(3723.5),
        Duration = TimeSpan.FromSeconds(90.25),
        VideoStreamIndex = 0,
        AudioStreamIndex = 1
    };

    private static int IndexOf(IReadOnlyList<string> args, string option)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == option)
            {
                return i;
            }
        }

        return -1;
    }

    private static string ValueAfter(IReadOnlyList<string> args, string option)
    {
        var index = IndexOf(args, option);
        Assert.True(index >= 0 && index + 1 < args.Count, $"Missing option {option}");
        return args[index + 1];
    }
}
