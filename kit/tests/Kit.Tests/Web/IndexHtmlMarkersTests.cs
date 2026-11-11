using System;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.Web;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Web;

/// <summary>
/// The marker attributes of the naming rules and the injected script tag.
/// </summary>
public sealed class IndexHtmlMarkersTests
{
    [Theory]
    [InlineData("jfbm")]
    [InlineData("jfur")]
    [InlineData("jfar")]
    public void Markers_FollowTheNamingTable(string prefix)
    {
        Assert.Equal("data-" + prefix + "-plugin", IndexHtmlMarkers.Plugin(prefix));
        Assert.Equal("data-" + prefix + "-head", IndexHtmlMarkers.Head(prefix));
        Assert.Equal("data-" + prefix + "-page", IndexHtmlMarkers.Page(prefix));
    }

    [Fact]
    public void ScriptTag_IsRelative_Versioned_AndDeferred()
    {
        var options = WebKit.Options(prefix: "jfss", routePrefix: "SmartSearch");

        var tag = IndexHtmlMarkers.ScriptTag(options);

        Assert.Matches(new Regex("^<script data-jfss-plugin=\"\\d+\\.\\d+\\.\\d+\\.\\d+\" src=\"\\.\\./SmartSearch/ClientScript\\?v=\\d+\\.\\d+\\.\\d+\\.\\d+-[0-9a-f]{8}\" defer></script>$"), tag);
        Assert.Equal(WebKit.ExpectedTag(options), tag);
    }

    [Fact]
    public void CacheBuster_IsTheStartOfTheMvid()
    {
        var assembly = typeof(IndexHtmlMarkersTests).Assembly;

        Assert.StartsWith(IndexHtmlMarkers.CacheBuster(assembly), assembly.ManifestModule.ModuleVersionId.ToString("N"), StringComparison.Ordinal);
        Assert.Equal(8, IndexHtmlMarkers.CacheBuster(assembly).Length);
        Assert.Equal(assembly.GetName().Version!.ToString(), IndexHtmlMarkers.Version(assembly));
    }
}
