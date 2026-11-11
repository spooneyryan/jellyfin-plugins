using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Tests.Fixtures;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Web.Pages;

/// <summary>
/// The page shells of <see cref="IKitPages"/> (<c>GET /&lt;Route&gt;/Pages/{key}</c>): <c>[Authorize]</c>
/// <c>text/html</c> with <c>no-cache</c>, the page root with a plain-text note, 200 "turned off" for a known page that
/// is off, 404 only for keys the plugin never had, all read per request.
/// </summary>
public sealed class KitPagesShellTests
{
    [Fact]
    public async Task AnEnabledPage_IsTheRootWithItsMarker_AndThePlainTextNote()
    {
        var kit = new PageTestKit();
        await using var host = await StartAsync(kit);

        using var response = await host.Client.SendAsync(host.Request(HttpMethod.Get, "/Fx/Pages/alpha", "user"), TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Equal(
            "<div class=\"jfkt-page\" data-jfkt-page=\"alpha\"><p class=\"jfkt-shell-note\">This page needs the Kit Test web UI, which is turned off or failed to load.</p></div>",
            html);
    }

    [Fact]
    public async Task AKnownPageTurnedOff_Answers200_WithTheNote_AndNoMarker_ReadPerRequest()
    {
        var kit = new PageTestKit();
        await using var host = await StartAsync(kit);

        kit.Settings.Enabled.Remove("beta");
        using var off = await host.Client.SendAsync(host.Request(HttpMethod.Get, "/Fx/Pages/beta", "user"), TestContext.Current.CancellationToken);
        var offHtml = await off.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        kit.Settings.Enabled.Add("beta");
        var on = await host.Client.SendAsync(host.Request(HttpMethod.Get, "/Fx/Pages/beta", "user"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal("text/html; charset=utf-8", off.Content.Headers.ContentType?.ToString());
        Assert.Equal("<div class=\"jfkt-page\"><p class=\"jfkt-shell-note\">Beta is turned off by your server admin.</p></div>", offHtml);
        Assert.DoesNotContain("data-jfkt-page", offHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", offHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data-jfkt-page=\"beta\"", await on.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        on.Dispose();
    }

    [Theory]
    [InlineData("gamma")]
    [InlineData("Alpha")]
    [InlineData("alpha.html")]
    public async Task AKeyThePluginNeverHad_Is404ProblemDetails(string key)
    {
        await using var host = await StartAsync(new PageTestKit());

        using var response = await host.Client.SendAsync(host.Request(HttpMethod.Get, "/Fx/Pages/" + key, "user"), TestContext.Current.CancellationToken);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(KitPages.UnknownPageMessage, body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Anonymous_Is401_AndAdminsGetTheShellToo()
    {
        await using var host = await StartAsync(new PageTestKit());

        using var anonymous = await host.Client.SendAsync(host.Request(HttpMethod.Get, "/Fx/Pages/alpha"), TestContext.Current.CancellationToken);
        using var admin = await host.Client.SendAsync(host.Request(HttpMethod.Get, "/Fx/Pages/alpha", "admin"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    [Fact]
    public async Task ASettingThatThrows_CountsAsOff_WithOneWarning()
    {
        var kit = new PageTestKit();
        kit.Settings.ThrowOnEnabled = true;
        var logger = new ListLogger();
        await using var host = await StartAsync(kit, logger);

        for (var i = 0; i < 2; i++)
        {
            using var response = await host.Client.SendAsync(host.Request(HttpMethod.Get, "/Fx/Pages/beta", "user"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Beta is turned off by your server admin.", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Single(logger.At(LogLevel.Warning));
    }

    [Fact]
    public void TheMarkup_EncodesThePluginName_AndTheTitle()
    {
        var kit = new PageTestKit("Kit & <Test>");
        var page = new KitPage("gamma", "A \"quoted\" & <b>bold</b> title", "star_half", "GammaPage");

        Assert.Equal(
            "<div class=\"jfkt-page\" data-jfkt-page=\"gamma\"><p class=\"jfkt-shell-note\">This page needs the Kit &amp; &lt;Test&gt; web UI, which is turned off or failed to load.</p></div>",
            KitPageMarkup.Shell(kit.Options, page));
        Assert.Equal(
            "<div class=\"jfkt-page\"><p class=\"jfkt-shell-note\">A &quot;quoted&quot; &amp; &lt;b&gt;bold&lt;/b&gt; title is turned off by your server admin.</p></div>",
            KitPageMarkup.TurnedOff(kit.Options, page));
    }

    [Fact]
    public void TheShellMarker_IsTheOneTheHeadFragmentKeepsVisible()
    {
        var kit = new PageTestKit();
        var fragment = FallbackRouteHeadFragment.Build("jfkt", ("alpha", "Alpha"));

        Assert.Contains(":not([" + IndexHtmlMarkers.Page("jfkt") + "])", fragment, StringComparison.Ordinal);
        Assert.Contains(IndexHtmlMarkers.Page("jfkt") + "=\"alpha\"", KitPageMarkup.Shell(kit.Options, kit.Options.Pages[0]), StringComparison.Ordinal);
    }

    private static Task<MvcTestHost> StartAsync(PageTestKit kit, ListLogger? logger = null)
        => MvcTestHost.StartAsync(
            [typeof(FxPagesController)],
            services =>
            {
                services.AddSingleton(kit.Options);
                services.AddSingleton(new KitAbiStatus("Kit Test", "10.11", "10.11.5", true, "Active."));
                services.AddSingleton(kit.CreatePages(logger));
                services.AddKitPages();
            });
}
