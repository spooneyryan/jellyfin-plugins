using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.Web.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Web.Pages;

/// <summary>
/// <see cref="PluginPagesBridge"/> against fake Plugin Pages 2.4.x and 3.x
/// assemblies in collectible load contexts, and the checks of <see cref="PluginPageRegistration"/>.
/// </summary>
public sealed class PluginPagesBridgeTests
{
    private static readonly PluginPageRegistration Clips = new("Jellyfin.Plugin.Bookmarks.ClipsPage", "/Bookmarks/Pages/clips", "My Clips", "video_library");

    [Fact]
    public void Version24_RegistersThroughTheDiManager_WithTheFourFields()
    {
        using var fake = FakePluginPages.Version24();
        var manager = fake.CreateManager();
        using var services = Services(fake.ManagerInterface, manager);

        var result = PluginPagesBridge.TryRegister(services, Clips, new ListLogger(), [typeof(object).Assembly, fake.Assembly]);

        Assert.True(result.Registered);
        Assert.Equal(PluginPagesBridge.Api24, result.Api);
        Assert.Equal("Jellyfin.Plugin.PluginPages 2.4.7.0", result.Detail);
        Assert.Equal([("Jellyfin.Plugin.Bookmarks.ClipsPage", "/Bookmarks/Pages/clips", "My Clips", "video_library")], FakePluginPages.Registered(manager));
    }

    [Fact]
    public void Version3_RegistersThroughPluginInterface_WithCamelCaseJson()
    {
        using var fake = FakePluginPages.Version3();

        var result = PluginPagesBridge.TryRegister(new ServiceCollection().BuildServiceProvider(), Clips, new ListLogger(), [fake.Assembly]);

        Assert.True(result.Registered);
        Assert.Equal(PluginPagesBridge.Api3x, result.Api);
        using var json = JsonDocument.Parse(fake.LastRegisteredJson!);
        Assert.Equal(
            ["id", "url", "displayText", "icon"],
            json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("/Bookmarks/Pages/clips", json.RootElement.GetProperty("url").GetString());
        Assert.Equal("My Clips", json.RootElement.GetProperty("displayText").GetString());
    }

    [Fact]
    public void WithoutPluginPages_NothingIsRegistered()
    {
        var result = PluginPagesBridge.TryRegister(new ServiceCollection().BuildServiceProvider(), Clips, new ListLogger(), [typeof(object).Assembly]);

        Assert.False(result.Registered);
        Assert.Equal(PluginPagesBridge.NotInstalled, result.Api);
    }

    [Theory]
    [InlineData("Jellyfin.Plugin.PluginPages.Extra")]
    [InlineData("jellyfin.plugin.pluginpages")]
    [InlineData("Jellyfin.Plugin.PluginPagesX")]
    public void OnlyTheExactAssemblyName_Counts(string name)
    {
        using var decoy = FakePluginPages.Version24(assemblyName: name);
        var manager = decoy.CreateManager();
        using var services = Services(decoy.ManagerInterface, manager);

        var result = PluginPagesBridge.TryRegister(services, Clips, new ListLogger(), [decoy.Assembly]);

        Assert.Equal(PluginPagesBridge.NotInstalled, result.Api);
        Assert.Empty(FakePluginPages.Registered(manager));
    }

    [Fact]
    public void AManagerThatThrows_IsRejected_WithOneWarning()
    {
        using var fake = FakePluginPages.Version24(throwing: true);
        using var services = Services(fake.ManagerInterface, fake.CreateManager());
        var logger = new ListLogger();

        var result = PluginPagesBridge.TryRegister(services, Clips, logger, [fake.Assembly]);

        Assert.False(result.Registered);
        Assert.Equal(PluginPagesBridge.Rejected, result.Api);
        Assert.Contains("Plugin Pages refused the page.", result.Detail, StringComparison.Ordinal);
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public void AnUnexpectedInterface_OrNoManagerInDi_IsReported()
    {
        using var renamed = FakePluginPages.Version24(pageTypeName: "Jellyfin.Plugin.PluginPages.Library.PluginPageV2");
        using var services = Services(renamed.ManagerInterface, renamed.CreateManager());
        using var registered = FakePluginPages.Version24();
        var logger = new ListLogger();

        var noPageType = PluginPagesBridge.TryRegister(services, Clips, logger, [renamed.Assembly]);
        var notInDi = PluginPagesBridge.TryRegister(new ServiceCollection().BuildServiceProvider(), Clips, logger, [registered.Assembly]);

        Assert.Equal(PluginPagesBridge.UnexpectedInterface, noPageType.Api);
        Assert.Equal(PluginPagesBridge.UnexpectedInterface, notInDi.Api);
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    [Fact]
    public void AnInvalidPage_IsNeverSent()
    {
        using var fake = FakePluginPages.Version24();
        var manager = fake.CreateManager();
        using var services = Services(fake.ManagerInterface, manager);

        var result = PluginPagesBridge.TryRegister(services, Clips with { Url = "https://attacker.example/x" }, new ListLogger(), [fake.Assembly]);

        Assert.Equal(PluginPagesBridge.InvalidPage, result.Api);
        Assert.Empty(FakePluginPages.Registered(manager));
    }

    [Fact]
    public void TheDefaultLookup_SeesAssembliesInEveryLoadContext()
    {
        using var fake = FakePluginPages.Version24(assemblyName: "Jellyfin.Plugin.PluginPagesProbe" + Guid.NewGuid().ToString("N")[..8]);

        Assert.Contains(fake.Assembly, PluginPagesBridge.LoadedAssemblies);
        Assert.NotSame(typeof(PluginPagesBridge).Assembly, PluginPagesBridge.Find([fake.Assembly, typeof(PluginPagesBridge).Assembly]));
    }

    [Fact]
    public void Registration_AcceptsThePlannedPluginsPages()
    {
        foreach (var page in new PluginPageRegistration[]
        {
            Clips,
            new("Jellyfin.Plugin.Bookmarks.BookmarksPage", "/Bookmarks/Pages/bookmarks", "My Bookmarks", "collections_bookmark"),
            new("Jellyfin.Plugin.UserRatings.TopRatedPage", "/UserRatings/Pages/toprated", "Top Rated", "star_half"),
            new("Jellyfin.Plugin.SmartSearch.SearchPage", "/SmartSearch/Pages/search", "Advanced Search", "manage_search"),
        })
        {
            Assert.Empty(page.Validate());
        }
    }

    [Theory]
    [InlineData("Bookmarks.ClipsPage", "/Bookmarks/Pages/clips", "My Clips", "video_library")]
    [InlineData("Jellyfin.Plugin.Bookmarks.Clips", "/Bookmarks/Pages/clips", "My Clips", "video_library")]
    [InlineData("Jellyfin.Plugin.Bookmarks.ClipsPage", "Bookmarks/Pages/clips", "My Clips", "video_library")]
    [InlineData("Jellyfin.Plugin.Bookmarks.ClipsPage", "//attacker.example/x", "My Clips", "video_library")]
    [InlineData("Jellyfin.Plugin.Bookmarks.ClipsPage", "http://attacker.example/x", "My Clips", "video_library")]
    [InlineData("Jellyfin.Plugin.Bookmarks.ClipsPage", "/Bookmarks/Pages/clips&tab=x", "My Clips", "video_library")]
    [InlineData("Jellyfin.Plugin.Bookmarks.ClipsPage", "/Bookmarks/Pages/clips?tab=x", "My Clips", "video_library")]
    [InlineData("Jellyfin.Plugin.Bookmarks.ClipsPage", "/Bookmarks/Pages/clips#x", "My Clips", "video_library")]
    [InlineData("Jellyfin.Plugin.Bookmarks.ClipsPage", "/Bookmarks/Pages/../x", "My Clips", "video_library")]
    [InlineData("Jellyfin.Plugin.Bookmarks.ClipsPage", "/Bookmarks\\Pages", "My Clips", "video_library")]
    [InlineData("Jellyfin.Plugin.Bookmarks.ClipsPage", "/Bookmarks/Pages/clips", "<img src=x onerror=alert(1)>", "video_library")]
    [InlineData("Jellyfin.Plugin.Bookmarks.ClipsPage", "/Bookmarks/Pages/clips", "Clips & more", "video_library")]
    [InlineData("Jellyfin.Plugin.Bookmarks.ClipsPage", "/Bookmarks/Pages/clips", "", "video_library")]
    [InlineData("Jellyfin.Plugin.Bookmarks.ClipsPage", "/Bookmarks/Pages/clips", "My Clips", "video_library\" onmouseover=\"x")]
    [InlineData("Jellyfin.Plugin.Bookmarks.ClipsPage", "/Bookmarks/Pages/clips", "My Clips", "Video")]
    public void Registration_RefusesWhatPluginPagesWouldMishandle(string id, string url, string displayText, string icon)
    {
        Assert.NotEmpty(new PluginPageRegistration(id, url, displayText, icon).Validate());
    }

    // ---------------------------------------------------------------------------- TryListPages

    [Fact]
    public void Version24_ListsThePagesItsManagerHolds_InOrder()
    {
        using var fake = FakePluginPages.Version24();
        var manager = fake.CreateManager();
        using var services = Services(fake.ManagerInterface, manager);
        PluginPagesBridge.TryRegister(services, new("Jellyfin.Plugin.JellyfinEnhanced.BookmarksPage", "/JellyfinEnhanced/bookmarksPage", "Bookmarks", "bookmark"), new ListLogger(), [fake.Assembly]);
        PluginPagesBridge.TryRegister(services, Clips, new ListLogger(), [fake.Assembly]);

        var state = PluginPagesBridge.TryListPages(services, [typeof(object).Assembly, fake.Assembly]);

        Assert.True(state.Installed);
        Assert.Null(state.Problem);
        Assert.Equal(
            [("Jellyfin.Plugin.JellyfinEnhanced.BookmarksPage", "/JellyfinEnhanced/bookmarksPage", "Bookmarks"), ("Jellyfin.Plugin.Bookmarks.ClipsPage", "/Bookmarks/Pages/clips", "My Clips")],
            state.Pages.Select(p => (p.Id, p.Url, p.DisplayText)));
    }

    [Fact]
    public void TryListPages_WithoutPluginPages_IsNotInstalled()
    {
        var state = PluginPagesBridge.TryListPages(new ServiceCollection().BuildServiceProvider(), [typeof(object).Assembly]);

        Assert.False(state.Installed);
        Assert.Empty(state.Pages);
        Assert.Null(state.Problem);
    }

    [Fact]
    public void TryListPages_ReportsAnUnexpectedInterface_AndAManagerMissingFromDi()
    {
        using var noList = FakePluginPages.Version24(getPages: false);
        using var withList = Services(noList.ManagerInterface, noList.CreateManager());
        var unexpected = PluginPagesBridge.TryListPages(withList, [noList.Assembly]);
        Assert.True(unexpected.Installed);
        Assert.Empty(unexpected.Pages);
        Assert.Contains("unexpected interface", unexpected.Problem, StringComparison.Ordinal);

        using var fake = FakePluginPages.Version24();
        var notInDi = PluginPagesBridge.TryListPages(new ServiceCollection().BuildServiceProvider(), [fake.Assembly]);
        Assert.True(notInDi.Installed);
        Assert.Contains("unexpected interface", notInDi.Problem, StringComparison.Ordinal);

        using var v3 = FakePluginPages.Version3();
        var threeX = PluginPagesBridge.TryListPages(new ServiceCollection().BuildServiceProvider(), [v3.Assembly]);
        Assert.True(threeX.Installed);
        Assert.Contains("unexpected interface", threeX.Problem, StringComparison.Ordinal);
    }

    private static ServiceProvider Services(Type managerInterface, object manager)
    {
        var services = new ServiceCollection();
        services.AddSingleton(managerInterface, manager);
        return services.BuildServiceProvider();
    }
}
