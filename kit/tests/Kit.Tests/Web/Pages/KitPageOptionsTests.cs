using System.Collections.Generic;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.Web.Pages;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Web.Pages;

/// <summary>
/// The page declarations of <see cref="KitOptions.Pages"/>, <see cref="KitPage"/>'s Ids and URLs, and
/// <see cref="PageHostModes"/>.
/// </summary>
public sealed class KitPageOptionsTests
{
    [Fact]
    public void Validate_AcceptsThePlannedPluginsPages()
    {
        Assert.Empty(Options("Bookmarks", new KitPage("clips", "My Clips", "video_library", "ClipsPage"), new KitPage("bookmarks", "My Bookmarks", "collections_bookmark", "BookmarksPage")).Validate());
        Assert.Empty(Options("UserRatings", new KitPage("toprated", "Top Rated", "star_half", "TopRatedPage")).Validate());
        Assert.Empty(Options("SmartSearch", new KitPage("search", "Advanced Search", "manage_search", "SearchPage")).Validate());
    }

    [Theory]
    [InlineData("Clips", "My Clips", "video_library", "ClipsPage")]
    [InlineData("clips.html", "My Clips", "video_library", "ClipsPage")]
    [InlineData("clips", "", "video_library", "ClipsPage")]
    [InlineData("clips", "My <Clips>", "video_library", "ClipsPage")]
    [InlineData("clips", "My Clips", "Video Library", "ClipsPage")]
    [InlineData("clips", "My Clips", "video_library", "Clips")]
    [InlineData("clips", "My Clips", "video_library", "clipsPage")]
    public void Validate_ReportsABrokenPage(string key, string title, string icon, string idSuffix)
    {
        Assert.NotEmpty(Options("Bookmarks", new KitPage(key, title, icon, idSuffix)).Validate());
    }

    [Fact]
    public void Validate_ReportsDuplicates_AndAnAssemblyTheBrowserCanNotName()
    {
        var clips = new KitPage("clips", "My Clips", "video_library", "ClipsPage");

        Assert.Single(Options("Bookmarks", clips, clips with { }).Validate(), p => p.Contains("'clips' is listed twice", System.StringComparison.Ordinal));
        Assert.Contains(Options("Bookmarks", clips, new KitPage("more", "More", "star_half", "ClipsPage")).Validate(), p => p.Contains("IdSuffix 'ClipsPage'", System.StringComparison.Ordinal));
        var mismatch = TestKit.Create(routePrefix: "Bookmarks", assemblyName: "Jellyfin.Plugin.KitTest");
        Assert.Contains(With(mismatch, clips).Validate(), p => p.Contains("Jellyfin.Plugin.Bookmarks", System.StringComparison.Ordinal));
    }

    [Fact]
    public void APage_HasItsPluginPagesIdAndShellUrl()
    {
        var page = new KitPage("toprated", "Top Rated", "star_half", "TopRatedPage");

        Assert.Equal("Jellyfin.Plugin.UserRatings.TopRatedPage", page.PluginPagesId("UserRatings"));
        Assert.Equal("/UserRatings/Pages/toprated", page.ShellUrl("UserRatings"));
        Assert.True(page.IsEnabled(null!));
    }

    [Theory]
    [InlineData(null, "FallbackOnly", true)]
    [InlineData("", "FallbackOnly", true)]
    [InlineData(" Auto ", "Auto", true)]
    [InlineData("pluginpagesonly", "PluginPagesOnly", true)]
    [InlineData("FallbackOnly", "FallbackOnly", true)]
    [InlineData("Plugin Pages", "FallbackOnly", false)]
    [InlineData("1", "FallbackOnly", false)]
    public void PageHostModes_ParseTheSetting(string? value, string expected, bool valid)
    {
        Assert.Equal(valid, PageHostModes.TryParse(value, out var mode));
        Assert.Equal(expected, mode.ToString());
        Assert.Equal(expected, PageHostModes.Parse(value).ToString());
    }

    private static KitOptions Options(string route, params KitPage[] pages)
        => With(TestKit.Create(routePrefix: route, schemaPrefix: route, assemblyName: "Jellyfin.Plugin." + route), pages);

    private static KitOptions With(KitOptions source, params KitPage[] pages)
        => new()
        {
            PluginName = source.PluginName,
            RoutePrefix = source.RoutePrefix,
            Prefix = source.Prefix,
            SchemaPrefix = source.SchemaPrefix,
            DataFolder = source.DataFolder,
            PluginAssembly = source.PluginAssembly,
            PluginId = source.PluginId,
            Pages = new List<KitPage>(pages),
        };
}
