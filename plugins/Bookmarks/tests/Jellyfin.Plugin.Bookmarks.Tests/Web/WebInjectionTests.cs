using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Web;

/// <summary>
/// Bookmarks' web UI through the kit's index.html middleware, with exactly what the registrator
/// registers, in the kit's copy of Jellyfin's web pipeline. File Transformation is no longer involved: Bookmarks 1.0's
/// <c>data-bookmarks-plugin</c> tag is gone.
/// </summary>
public sealed class WebInjectionTests
{
    private static readonly string Mvid8 = typeof(Plugin).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..8];

    private static readonly string ExpectedTag =
        "<script data-jfbm-plugin=\"1.2.2.0\" src=\"../Bookmarks/ClientScript?v=1.2.2.0-" + Mvid8 + "\" defer></script>";

    private readonly PluginConfiguration _configuration = new();

    [Theory]
    [InlineData("", "/web/index.html")]
    [InlineData("", "/web/")]
    [InlineData("/jf", "/web/index.html")]
    [InlineData("/jf", "/web/")]
    [InlineData("", "/emby/web/index.html")]
    [InlineData("/jf", "/mediabrowser/web/")]
    public async Task IndexHtml_GetsTheBookmarksTagOnce(string baseUrl, string path)
    {
        await using var host = await StartAsync(baseUrl);

        using var response = await host.SendAsync(HttpMethod.Get, path, r => r.Headers.AcceptEncoding.ParseAdd("gzip, br"));
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(ExpectedTag + "</body>", html, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(html, "data-jfbm-plugin"));
        Assert.DoesNotContain("data-bookmarks-plugin", html, StringComparison.Ordinal);

        // The head fragment of the user pages, once, before </head>, since the My Bookmarks page (1.1.0).
        Assert.Single(Regex.Matches(html, "<style data-jfbm-head>"));
        Assert.Single(Regex.Matches(html, "<script data-jfbm-head>"));
        Assert.True(html.LastIndexOf("data-jfbm-head", StringComparison.Ordinal) < html.IndexOf("</head>", StringComparison.Ordinal));
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public void TheTagLoadsTheClientScriptRoute_UnderAnyBaseUrl()
    {
        // The relative src resolves against .../web/index.html to the controller's anonymous ClientScript route.
        var src = Regex.Match(ExpectedTag, "src=\"([^\"]+)\"").Groups[1].Value;
        var route = typeof(BookmarksController).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Single().Template;
        var action = typeof(BookmarksController).GetMethod(nameof(BookmarksController.GetClientScript))!
            .GetCustomAttributes(typeof(HttpGetAttribute), false).Cast<HttpGetAttribute>().Single().Template;

        foreach (var baseUrl in new[] { string.Empty, "/jf", "/media/jellyfin" })
        {
            var resolved = new Uri(new Uri("http://127.0.0.1:8097" + baseUrl + "/web/index.html"), src);
            Assert.Equal(baseUrl + "/" + route + "/" + action, resolved.AbsolutePath);
        }
    }

    [Fact]
    public async Task EnableWebUi_IsReadOnEveryRequest()
    {
        await using var host = await StartAsync(string.Empty);

        _configuration.EnableWebUi = false;
        var off = await host.GetStringAsync("/web/index.html");
        _configuration.EnableWebUi = true;
        var on = await host.GetStringAsync("/web/index.html");

        Assert.Equal(WebHostHarness.SyntheticIndexHtml, off);
        Assert.Contains(ExpectedTag, on, StringComparison.Ordinal);
    }

    [Fact]
    public void EnableWebUi_IsOnByDefault()
    {
        Assert.True(new PluginConfiguration().EnableWebUi);
    }

    [Fact]
    public async Task NextToJellyfinEnhanced_BothTagsAppearOnce()
    {
        // "Bookmarks" sorts before "Jellyfin Enhanced", so its middleware is the outer one on a real server.
        await using var host = await StartAsync(string.Empty, new JellyfinEnhancedLikeStartupFilter());

        var html = await host.GetStringAsync("/web/index.html");

        Assert.Single(Regex.Matches(html, "data-jfbm-plugin"));
        Assert.Single(Regex.Matches(html, Regex.Escape(JellyfinEnhancedLikeStartupFilter.Needle)));
        Assert.True(html.IndexOf(JellyfinEnhancedLikeStartupFilter.Needle, StringComparison.Ordinal) < html.IndexOf(ExpectedTag, StringComparison.Ordinal));
    }

    private async Task<WebHostHarness> StartAsync(string baseUrl, IStartupFilter? inner = null)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(new Version(10, 11, 5));
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, host.Object);
        var provider = new Mock<IPluginConfigurationProvider>();
        provider.Setup(p => p.GetConfiguration()).Returns(() => _configuration);

        var harness = new WebHostHarness()
            .WithBaseUrl(baseUrl)
            .WithServices(services =>
            {
                // The registrator's own descriptors for the kit options and the middleware, and the settings it reads.
                foreach (var descriptor in registered.Where(d => d.ServiceType == typeof(KitOptions) || d.ServiceType == typeof(IStartupFilter)))
                {
                    services.Add(descriptor);
                }

                services.AddSingleton(provider.Object);
            });
        if (inner is not null)
        {
            harness.WithStartupFilter(_ => inner);
        }

        return await harness.StartAsync();
    }
}
