using System.Linq;
using Emby.Naming.Common;
using Jellyfin.Plugin.LibraryMaintenance.Move.Planning;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Planning;

/// <summary>
/// The sidecar name rule: subtitles, audio and other companions the stem
/// and a dot; an <c>.nfo</c> and images only in 10.11.5's exact forms; an extras suffix only for a known extra; never a
/// longer stem (<c>Ep 10</c> for <c>Ep 1</c>), and each file to the longest stem that matches it.
/// </summary>
public sealed class SidecarMatcherTests
{
    private static readonly string[] _suffixes = new NamingOptions().VideoExtraRules
        .Where(r => r.RuleType == Emby.Naming.Video.ExtraRuleType.Suffix).Select(r => r.Token).ToArray();

    [Theory]
    [InlineData("Ep 1.srt", true)]
    [InlineData("Ep 1.en.forced.srt", true)]
    [InlineData("EP 1.ASS", true)]
    [InlineData("Ep 1.mka", true)]
    [InlineData("Ep 1.idx", true)]
    [InlineData("Ep 1.nfo", true)]
    [InlineData("ep 1.NFO", true)]
    [InlineData("Ep 1.jpg", true)]
    [InlineData("Ep 1-poster.jpg", true)]
    [InlineData("Ep 1-thumb.TBN", true)]
    [InlineData("Ep 1-fanart.webp", true)]
    [InlineData("Ep 1-fanart-2.jpg", true)]
    [InlineData("Ep 1-backdrop3.png", true)]
    [InlineData("Ep 1-Ep 1-fanart.jpg", true)]
    [InlineData("Ep 1-clearlogo.png", true)]
    [InlineData("Ep 1.en.nfo", false)]
    [InlineData("Ep 1.Extended.nfo", false)]
    [InlineData("Ep 1.fanart.jpg", false)]
    [InlineData("Ep 1_fanart.webp", false)]
    [InlineData("Ep 1-fanart-0.jpg", false)]
    [InlineData("Ep 1-fanart-21.jpg", false)]
    [InlineData("Ep 1-backdrop.png", true)]
    [InlineData("Ep 1-backdrop-3.png", false)]
    [InlineData("Ep 1-screenshot.jpg", false)]
    [InlineData("Ep 10.srt", false)]
    [InlineData("Ep 10-poster.jpg", false)]
    [InlineData("Ep 1-notes.txt", false)]
    [InlineData("Ep 1-poster.old.jpg", false)]
    [InlineData("Ep 1 - poster.jpg", false)]
    [InlineData("Ep 1", false)]
    [InlineData("Ep 1.", false)]
    [InlineData("Ep 1-.jpg", false)]
    [InlineData("Ep 1-trailer.mkv", false)]
    [InlineData("Other.srt", false)]
    public void IsSidecarName(string name, bool expected)
        => Assert.Equal(expected, SidecarMatcher.IsSidecarName(name, "Ep 1"));

    [Theory]
    [InlineData("Heat-trailer.mkv", true)]
    [InlineData("Heat_sample.mkv", true)]
    [InlineData("Heat-featurette.mp4", true)]
    [InlineData("Heat- trailer.mkv", true)]
    [InlineData("Heat.sample.mkv", true)]
    [InlineData("Heat - notes.mkv", false)]
    [InlineData("Heater-trailer.mkv", false)]
    public void IsExtraName_TheStemAndADotOrAnExtrasSuffix(string name, bool expected)
        => Assert.Equal(expected, SidecarMatcher.IsExtraName(name, "Heat", _suffixes));

    [Theory]
    [InlineData("Ep 1.trickplay", true)]
    [InlineData("EP 1.TRICKPLAY", true)]
    [InlineData("Ep 1.trickplay.old", false)]
    [InlineData("Ep 1.en.trickplay", false)]
    [InlineData("Ep 10.trickplay", false)]
    [InlineData("Ep 1.extras", false)]
    [InlineData("Ep 1", false)]
    [InlineData(".trickplay", false)]
    public void IsTrickplayFolder_IsTheStemsOwnOnly(string name, bool expected)
    {
        // 10.11.5 saves trickplay with the media as Path.ChangeExtension(fileName, ".trickplay") (PathManager.cs:66-73).
        Assert.Equal(expected, SidecarMatcher.IsTrickplayFolder(name, "Ep 1"));
        Assert.False(SidecarMatcher.IsTrickplayFolder(".trickplay", string.Empty));
    }

    [Fact]
    public void IsVideo_ReadsJellyfinsExtensions()
    {
        var naming = new NamingOptions();
        Assert.True(SidecarMatcher.IsVideo("a.MKV", naming));
        Assert.True(SidecarMatcher.IsVideo("VTS_01_1.vob", naming));
        Assert.False(SidecarMatcher.IsVideo("a.mka", naming));
        Assert.False(SidecarMatcher.IsVideo("a.srt", naming));
        Assert.False(SidecarMatcher.IsVideo("mkv", naming));
    }
}
