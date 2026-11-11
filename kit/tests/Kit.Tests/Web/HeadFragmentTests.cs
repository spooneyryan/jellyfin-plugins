using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static Jellyfin.Plugin.Kit.Tests.Web.IndexHtmlInjectionStartupFilterTests;

namespace Jellyfin.Plugin.Kit.Tests.Web;

/// <summary>
/// The head fragment of the injection middleware: once before the first
/// <c>&lt;/head&gt;</c>, next to the script tag, rendered on every request.
/// </summary>
public sealed class HeadFragmentTests
{
    private const string Fragment = "<style data-jfkt-head>html.jfkt-route #fallbackPage > :not([data-jfkt-page]) { display:none !important }</style>";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("", "/web/index.html")]
    [InlineData("/jf", "/web/")]
    public async Task TheFragment_GoesBeforeTheFirstHeadEnd_NextToTheTag(string baseUrl, string path)
    {
        var options = WebKit.Options(headFragment: _ => Fragment);
        await using var host = await new WebHostHarness().WithBaseUrl(baseUrl).WithStartupFilter(WebKit.Filter(options)).StartAsync();

        var html = await host.GetStringAsync(path);

        var expected = Expected(WebHostHarness.SyntheticIndexHtml, WebKit.ExpectedTag(options));
        expected = expected.Insert(expected.IndexOf("</head>", StringComparison.Ordinal), Fragment);
        Assert.Equal(expected, html);
        Assert.Equal(1, Count(html, "data-jfkt-head"));
    }

    [Fact]
    public async Task TheFragment_IsRenderedOnEveryRequest_WithTheRequest()
    {
        var calls = 0;
        var options = WebKit.Options(headFragment: context =>
            $"<script data-jfkt-head>var anyManaged = {context.RequestServices.GetRequiredService<ServerWideFlag>().Value.ToString().ToLowerInvariant()}; var n = {Interlocked.Increment(ref calls)};</script>");
        var flag = new ServerWideFlag();
        await using var host = await new WebHostHarness()
            .WithServices(services => services.AddSingleton(flag))
            .WithStartupFilter(WebKit.Filter(options))
            .StartAsync();

        var first = await host.GetStringAsync("/web/index.html");
        flag.Value = true;
        var second = await host.GetStringAsync("/web/index.html");
        using var head = await host.SendAsync(HttpMethod.Head, "/web/index.html");

        Assert.Contains("<script data-jfkt-head>var anyManaged = false; var n = 1;</script></head>", first, StringComparison.Ordinal);
        Assert.Contains("<script data-jfkt-head>var anyManaged = true; var n = 2;</script></head>", second, StringComparison.Ordinal);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task WithoutHeadEnd_OnlyTheTagIsInserted()
    {
        var html = WebHostHarness.SyntheticIndexHtml.Replace("</head>", string.Empty, StringComparison.Ordinal);
        var options = WebKit.Options(headFragment: _ => Fragment);
        await using var host = await new WebHostHarness().WithIndexHtml(html).WithStartupFilter(WebKit.Filter(options)).StartAsync();

        Assert.Equal(Expected(html, WebKit.ExpectedTag(options)), await host.GetStringAsync("/web/index.html"));
    }

    [Fact]
    public async Task WithoutBodyEnd_NeitherIsInserted()
    {
        const string Html = "<html><head></head><p>no body end</p></html>";
        await using var host = await new WebHostHarness()
            .WithIndexHtml(Html)
            .WithStartupFilter(WebKit.Filter(WebKit.Options(headFragment: _ => Fragment)))
            .StartAsync();

        Assert.Equal(Html, await host.GetStringAsync("/web/index.html"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithoutAFragment_TheOutputIsByteIdenticalToTheTagOnlyInjection(bool delegateReturnsNothing)
    {
        var withoutFeature = WebKit.Options();
        var withEmpty = WebKit.Options(headFragment: delegateReturnsNothing ? _ => null : _ => string.Empty);
        await using var tagOnly = await new WebHostHarness().WithStartupFilter(WebKit.Filter(withoutFeature)).StartAsync();
        await using var host = await new WebHostHarness().WithStartupFilter(WebKit.Filter(withEmpty)).StartAsync();

        using var expected = await tagOnly.SendAsync(HttpMethod.Get, "/web/index.html");
        using var actual = await host.SendAsync(HttpMethod.Get, "/web/index.html");

        Assert.Equal(await expected.Content.ReadAsByteArrayAsync(Ct), await actual.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(expected.Content.Headers.ContentLength, actual.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task TwoInstancesOfOnePlugin_InsertTheFragmentOnce()
    {
        var options = WebKit.Options(headFragment: _ => Fragment);
        await using var host = await new WebHostHarness().WithStartupFilter(WebKit.Filter(options)).WithStartupFilter(WebKit.Filter(options)).StartAsync();

        var html = await host.GetStringAsync("/web/index.html");

        Assert.Equal(1, Count(html, "data-jfkt-head"));
        Assert.Equal(1, Count(html, "data-jfkt-plugin"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TwoPluginsAndAJellyfinEnhancedLikeFilter_EachInsertOnce(bool jellyfinEnhancedOuter)
    {
        var accessRoles = WebKit.Options(prefix: "jfar", routePrefix: "AccessRoles", headFragment: _ => "<script data-jfar-head>var anyManaged = true;</script>");
        var bookmarks = WebKit.Options(prefix: "jfbm", routePrefix: "Bookmarks", headFragment: _ => FallbackRouteHeadFragment.Build("jfbm", ("clips", "My Clips")));
        var harness = new WebHostHarness();
        if (jellyfinEnhancedOuter)
        {
            harness.WithStartupFilter(_ => new JellyfinEnhancedLikeStartupFilter());
        }

        harness.WithStartupFilter(WebKit.Filter(accessRoles)).WithStartupFilter(WebKit.Filter(bookmarks));
        if (!jellyfinEnhancedOuter)
        {
            harness.WithStartupFilter(_ => new JellyfinEnhancedLikeStartupFilter());
        }

        await using var host = await harness.StartAsync();
        using var response = await host.SendAsync(HttpMethod.Get, "/web/index.html", r => r.Headers.AcceptEncoding.ParseAdd("gzip"));
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, Count(html, "<script data-jfar-head>"));
        Assert.Equal(1, Count(html, "<style data-jfbm-head>"));
        Assert.Equal(1, Count(html, "<script data-jfbm-head>"));
        Assert.Equal(1, Count(html, "data-jfar-plugin"));
        Assert.Equal(1, Count(html, "data-jfbm-plugin"));
        Assert.Equal(1, Count(html, JellyfinEnhancedLikeStartupFilter.Needle));

        // Both fragments sit in the head, the inner plugin's first; both tags in the body.
        var headEnd = html.IndexOf("</head>", StringComparison.Ordinal);
        Assert.True(html.IndexOf("<style data-jfbm-head>", StringComparison.Ordinal) < html.IndexOf("<script data-jfar-head>", StringComparison.Ordinal));
        Assert.True(html.IndexOf("<script data-jfar-head>", StringComparison.Ordinal) < headEnd);
        Assert.True(html.IndexOf("data-jfbm-plugin", StringComparison.Ordinal) > headEnd);
    }

    [Fact]
    public async Task AFragmentWithoutItsMarker_IsLeftOut_WithOneWarning()
    {
        var logger = new ListLogger();
        var options = WebKit.Options(headFragment: _ => "<style>.unmarked{}</style>");
        await using var host = await new WebHostHarness().WithStartupFilter(WebKit.Filter(options, logger)).StartAsync();

        var first = await host.GetStringAsync("/web/index.html");
        await host.GetStringAsync("/web/index.html");

        Assert.Equal(Expected(WebHostHarness.SyntheticIndexHtml, WebKit.ExpectedTag(options)), first);
        Assert.Contains("data-jfkt-head", Assert.Single(logger.At(LogLevel.Warning)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThrowingFragment_ServesTheOriginalBytes_AndWarnsOnce()
    {
        var logger = new ListLogger();
        var options = WebKit.Options(headFragment: _ => throw new InvalidOperationException("fragment failed"));
        await using var host = await new WebHostHarness().WithStartupFilter(WebKit.Filter(options, logger)).StartAsync();

        Assert.Equal(WebHostHarness.SyntheticIndexHtml, await host.GetStringAsync("/web/index.html"));
        Assert.Equal(WebHostHarness.SyntheticIndexHtml, await host.GetStringAsync("/web/index.html"));
        Assert.IsType<InvalidOperationException>(Assert.Single(logger.At(LogLevel.Warning)).Exception);
    }

    [Fact]
    public async Task WithTheWebUiOff_NoFragmentEither()
    {
        var calls = 0;
        var options = WebKit.Options(webUiEnabled: _ => false, headFragment: _ =>
        {
            Interlocked.Increment(ref calls);
            return Fragment;
        });
        await using var host = await new WebHostHarness().WithStartupFilter(WebKit.Filter(options)).StartAsync();

        Assert.Equal(WebHostHarness.SyntheticIndexHtml, await host.GetStringAsync("/web/index.html"));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task TheGeneratedFallbackFragment_IsInsertedAsBuilt()
    {
        var fragment = FallbackRouteHeadFragment.Build("jfkt", ("clips", "My Clips"), ("bookmarks", "My Bookmarks"));
        var options = WebKit.Options(headFragment: _ => fragment);
        await using var host = await new WebHostHarness().WithStartupFilter(WebKit.Filter(options)).StartAsync();

        var html = await host.GetStringAsync("/web/index.html");

        Assert.Contains(fragment + "</head>", html, StringComparison.Ordinal);
        Assert.Equal(2, Count(html, "data-jfkt-head"));
    }

    private sealed class ServerWideFlag
    {
        public bool Value { get; set; }
    }
}
