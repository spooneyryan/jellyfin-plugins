using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Web.Pages;

/// <summary>
/// <see cref="FallbackRouteHeadFragment.Render"/> (the user pages on top of the head fragment): the fallback-route
/// fragment of every known page plus the page-host state the client script reads (known pages, pages turned off,
/// <c>PageHostMode</c>, <c>ShowFallbackNav</c>), rendered per <c>index.html</c> request, anonymous and unable to end
/// its script element.
/// </summary>
public sealed class FallbackRouteRenderTests
{
    private static readonly IServiceProvider NoServices = new ServiceCollection().BuildServiceProvider();

    [Fact]
    public void TheFragment_IsTheHeadFragmentOfEveryKnownPage_FollowedByTheState()
    {
        var kit = new PageTestKit();
        kit.Settings.Enabled.Remove("beta");
        kit.Settings.Mode = "auto";
        kit.Settings.Nav = false;

        var fragment = FallbackRouteHeadFragment.Render(kit.Options, NoServices)!;

        var expectedHead = FallbackRouteHeadFragment.Build("jfkt", ("alpha", "Alpha"), ("beta", "Beta"));
        Assert.StartsWith(expectedHead, fragment, StringComparison.Ordinal);
        Assert.Equal(
            "<script type=\"application/json\" data-jfkt-head data-jfkt-pages>{\"mode\":\"Auto\",\"nav\":false,\"off\":[\"beta\"],\"pages\":[\"alpha\",\"beta\"]}</script>",
            fragment[expectedHead.Length..]);
    }

    [Fact]
    public void BrokenSettings_TakeTheirDefaults()
    {
        var kit = new PageTestKit();
        kit.Settings.ThrowOnEnabled = true;
        kit.Settings.ThrowOnMode = true;

        var state = State(FallbackRouteHeadFragment.Render(kit.Options, NoServices)!);

        Assert.Equal("FallbackOnly", state.GetProperty("mode").GetString());
        Assert.True(state.GetProperty("nav").GetBoolean());
        Assert.Equal("[]", state.GetProperty("off").GetRawText(), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(null, "FallbackOnly")]
    [InlineData("PLUGINPAGESONLY", "PluginPagesOnly")]
    [InlineData("nonsense", "FallbackOnly")]
    public void TheMode_IsNormalised(string? configured, string expected)
    {
        var kit = new PageTestKit();
        kit.Settings.Mode = configured;

        Assert.Equal(expected, State(FallbackRouteHeadFragment.Render(kit.Options, NoServices)!).GetProperty("mode").GetString());
    }

    [Fact]
    public void APluginWithoutPages_HasNoFragment()
    {
        Assert.Null(FallbackRouteHeadFragment.Render(TestKit.Options, NoServices));
    }

    [Fact]
    public void NothingInTheState_CanEndItsScriptElement()
    {
        var kit = new PageTestKit();
        var options = new KitOptions
        {
            PluginName = kit.Options.PluginName,
            RoutePrefix = kit.Options.RoutePrefix,
            Prefix = kit.Options.Prefix,
            SchemaPrefix = kit.Options.SchemaPrefix,
            DataFolder = kit.Options.DataFolder,
            PluginAssembly = kit.Options.PluginAssembly,
            Pages = [new KitPage("alpha", "</script><script>alert(1)</script>", "video_library", "AlphaPage")],
            PageHostMode = _ => "</script><b>",
        };

        var fragment = FallbackRouteHeadFragment.Render(options, NoServices)!;

        Assert.Equal(2, IndexHtmlInjectionStartupFilterTests.Count(fragment, "</script>"));
        Assert.True(fragment.All(c => c < 128), "The fragment should be ASCII.");
    }

    [Fact]
    public async Task InIndexHtml_TheFragmentFollowsTheSettingsOnEveryRequest_Once()
    {
        var kit = new PageTestKit();
        var options = WithFragment(kit);
        await using var host = await new WebHostHarness().WithStartupFilter(WebKit.Filter(options)).StartAsync();

        var first = await host.GetStringAsync("/web/index.html");
        kit.Settings.Enabled.Remove("alpha");
        var second = await host.GetStringAsync("/web/");

        Assert.Equal(3, IndexHtmlInjectionStartupFilterTests.Count(first, "data-jfkt-head"));
        Assert.True(first.IndexOf("data-jfkt-pages", StringComparison.Ordinal) < first.IndexOf("</head>", StringComparison.Ordinal));
        Assert.Contains("\"off\":[]", first, StringComparison.Ordinal);
        Assert.Contains("\"off\":[\"alpha\"]", second, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(second, "data-jfkt-pages"));
    }

    private static KitOptions WithFragment(PageTestKit kit)
    {
        var source = kit.Options;
        KitOptions? options = null;
        options = new KitOptions
        {
            PluginName = source.PluginName,
            RoutePrefix = source.RoutePrefix,
            Prefix = source.Prefix,
            SchemaPrefix = source.SchemaPrefix,
            DataFolder = source.DataFolder,
            PluginAssembly = source.PluginAssembly,
            Pages = source.Pages,
            PageHostMode = source.PageHostMode,
            ShowFallbackNav = source.ShowFallbackNav,
            HeadFragment = context => FallbackRouteHeadFragment.Render(options!, context.RequestServices),
        };
        return options;
    }

    private static JsonElement State(string fragment)
    {
        const string Open = "data-jfkt-pages>";
        var start = fragment.IndexOf(Open, StringComparison.Ordinal) + Open.Length;
        return JsonDocument.Parse(fragment[start..fragment.LastIndexOf("</script>", StringComparison.Ordinal)]).RootElement;
    }
}
