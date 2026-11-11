using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Web;

/// <summary>
/// The injection middleware in the kit's copy of Jellyfin's web pipeline.
/// </summary>
public sealed class IndexHtmlInjectionStartupFilterTests
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("", "/web/index.html")]
    [InlineData("", "/web/")]
    [InlineData("", "/WEB/INDEX.HTML")]
    [InlineData("", "/web/index.html?probe=1")]
    [InlineData("/jf", "/jf/web/index.html")]
    [InlineData("/jf", "/jf/web/")]
    [InlineData("/jf", "/JF/web/index.html")]
    [InlineData("/media/jellyfin", "/media/jellyfin/web/")]
    [InlineData("", "/emby/web/index.html")]
    [InlineData("", "/EMBY/web/")]
    [InlineData("", "/mediabrowser/web/index.html")]
    [InlineData("/jf", "/jf/emby/web/")]
    [InlineData("/jf", "/jf/MediaBrowser/web/index.html")]
    public async Task Index_GetsOneTag_Uncompressed_Uncached_DespiteConditionalAndRangeHeaders(string baseUrl, string path)
    {
        var options = WebKit.Options();
        await using var host = await new WebHostHarness().WithBaseUrl(baseUrl).WithStartupFilter(WebKit.Filter(options)).StartAsync();

        // Validators from a HEAD, which passes through untouched (step 2).
        using var head = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path), Ct);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.NotNull(head.Headers.ETag);

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");
        request.Headers.IfNoneMatch.Add(head.Headers.ETag!);
        request.Headers.IfModifiedSince = head.Content.Headers.LastModified;
        request.Headers.Range = new RangeHeaderValue(0, 10);
        request.Headers.IfRange = new RangeConditionHeaderValue(head.Headers.ETag!);
        using var response = await host.Client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsByteArrayAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Expected(WebHostHarness.SyntheticIndexHtml, WebKit.ExpectedTag(options)), Utf8.GetString(body));
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Null(response.Headers.ETag);
        Assert.Null(response.Content.Headers.LastModified);
        Assert.Empty(response.Headers.AcceptRanges);
        Assert.Null(response.Content.Headers.ContentRange);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Equal(body.Length, response.Content.Headers.ContentLength);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TheTag_IsTheOneTheKitSpecifies()
    {
        var options = WebKit.Options(prefix: "jfbm", routePrefix: "Bookmarks");
        await using var host = await new WebHostHarness().WithStartupFilter(WebKit.Filter(options)).StartAsync();

        var html = await host.GetStringAsync("/web/index.html");

        // <script data-jfbm-plugin="<version>" src="../Bookmarks/ClientScript?v=<version>-<mvid8>" defer></script>
        var version = typeof(WebKit).Assembly.GetName().Version!.ToString();
        var mvid = typeof(WebKit).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..8];
        Assert.Contains(
            "<script data-jfbm-plugin=\"" + version + "\" src=\"../Bookmarks/ClientScript?v=" + version + "-" + mvid + "\" defer></script></body>",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Head_IsUntouched()
    {
        await using var host = await new WebHostHarness().WithStartupFilter(WebKit.Filter(WebKit.Options())).StartAsync();

        using var head = await host.SendAsync(HttpMethod.Head, "/web/index.html");

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(Utf8.GetByteCount(WebHostHarness.SyntheticIndexHtml), head.Content.Headers.ContentLength);
        Assert.NotNull(head.Headers.ETag);
        Assert.NotNull(head.Content.Headers.LastModified);
        Assert.Contains("bytes", head.Headers.AcceptRanges);
    }

    [Fact]
    public async Task OtherFiles_StayByteIdentical_Compressed_AndConditional()
    {
        await using var plain = await new WebHostHarness().StartAsync();
        await using var host = await new WebHostHarness().WithStartupFilter(WebKit.Filter(WebKit.Options())).StartAsync();

        foreach (var file in new[] { WebHostHarness.BundleFile, WebHostHarness.CssFile })
        {
            using var without = await plain.SendAsync(HttpMethod.Get, "/web/" + file, r => r.Headers.AcceptEncoding.ParseAdd("gzip"));
            using var with = await host.SendAsync(HttpMethod.Get, "/web/" + file, r => r.Headers.AcceptEncoding.ParseAdd("gzip"));

            Assert.Equal(HttpStatusCode.OK, with.StatusCode);
            Assert.Equal("gzip", Assert.Single(with.Content.Headers.ContentEncoding));
            Assert.NotNull(with.Headers.ETag);
            Assert.Equal(await Gunzip(without), await Gunzip(with));
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(host.WebRoot, file), Ct), await Gunzip(with));

            using var conditional = await host.SendAsync(HttpMethod.Get, "/web/" + file, r => r.Headers.IfNoneMatch.Add(with.Headers.ETag!));
            Assert.Equal(HttpStatusCode.NotModified, conditional.StatusCode);
        }
    }

    [Theory]
    [InlineData("", "/web/main.jellyfin.bundle.js")]
    [InlineData("", "/web")]
    [InlineData("", "/web/index.htm")]
    [InlineData("", "/web/sub/index.html")]
    [InlineData("", "/Items/web/")]
    [InlineData("", "/System/Info/Public")]
    [InlineData("/jf", "/web/index.html")]
    [InlineData("/jf", "/jfx/web/index.html")]
    [InlineData("/jf", "/jf/other/web/index.html")]
    [InlineData("", "/emby/emby/web/index.html")]
    [InlineData("", "/emby/mediabrowser/web/")]
    [InlineData("", "/other/emby/web/")]
    [InlineData("/jf", "/emby/jf/web/index.html")]
    public async Task OtherPaths_AreNeverBufferedOrTransformed(string baseUrl, string path)
    {
        var bodies = new ConcurrentQueue<string>();
        await using var host = await new WebHostHarness()
            .WithBaseUrl(baseUrl)
            .WithStartupFilter(WebKit.Filter(WebKit.Options()))
            .WithStartupFilter(_ => RecordBodyStream(bodies))
            .StartAsync();

        using var response = await host.Client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("data-jfkt-plugin", text, StringComparison.Ordinal);
        Assert.All(bodies, name => Assert.NotEqual(nameof(CappedBufferStream), name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/jf")]
    public async Task TheBaseUrl_IsReadOnlyForGetsOfPathsWithTheIndexSuffix(string baseUrl)
    {
        // Every request passes through the middleware, so the method and the cheap path suffix are tested before the
        // network configuration is read.
        var configuration = new Mock<IServerConfigurationManager>();
        configuration.Setup(c => c.GetConfiguration("network")).Returns(() => new NetworkConfiguration { BaseUrl = baseUrl });
        var options = WebKit.Options();
        await using var host = await new WebHostHarness()
            .WithBaseUrl(baseUrl)
            .WithStartupFilter(_ => new IndexHtmlInjectionStartupFilter(options, configuration.Object, WebKit.LoggerFactory(new ListLogger())))
            .StartAsync();

        foreach (var path in new[] { "/System/Info/Public", "/web/main.jellyfin.bundle.js", "/web/main.jellyfin.css", "/Items/x", "/Videos/x/stream.mp4", "/web", "/web/index.htm" })
        {
            using var response = await host.SendAsync(HttpMethod.Get, path);
        }

        using var head = await host.SendAsync(HttpMethod.Head, "/web/index.html");
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        configuration.Verify(c => c.GetConfiguration(It.IsAny<string>()), Times.Never);

        // Positive control: the index path does read it.
        Assert.Contains("data-jfkt-plugin", await host.GetStringAsync("/web/index.html"), StringComparison.Ordinal);
        configuration.Verify(c => c.GetConfiguration("network"), Times.AtLeastOnce);
    }

    [Fact]
    public async Task IndexRequests_AreBuffered()
    {
        // Positive control for the two tests around it: the recorder does see our buffer.
        var bodies = new ConcurrentQueue<string>();
        await using var host = await new WebHostHarness()
            .WithStartupFilter(WebKit.Filter(WebKit.Options()))
            .WithStartupFilter(_ => RecordBodyStream(bodies))
            .StartAsync();

        await host.GetStringAsync("/web/index.html");

        Assert.Equal(nameof(CappedBufferStream), Assert.Single(bodies));
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task OnlyGet_IsTransformed(string method)
    {
        var bodies = new ConcurrentQueue<string>();
        await using var host = await new WebHostHarness()
            .WithStartupFilter(WebKit.Filter(WebKit.Options()))
            .WithStartupFilter(_ => RecordBodyStream(bodies))
            .StartAsync();

        using var response = await host.SendAsync(new HttpMethod(method), "/web/index.html");
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("data-jfkt-plugin", text, StringComparison.Ordinal);
        Assert.NotEqual(nameof(CappedBufferStream), Assert.Single(bodies));
    }

    [Fact]
    public async Task WebWithoutSlash_IsARedirectLeftAlone()
    {
        await using var host = await new WebHostHarness().WithStartupFilter(WebKit.Filter(WebKit.Options())).StartAsync();

        using var response = await host.SendAsync(HttpMethod.Get, "/web");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.EndsWith("/web/", response.Headers.Location?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutsideTheBaseUrl_TheRedirectIsLeftAlone()
    {
        await using var host = await new WebHostHarness().WithBaseUrl("/jf").WithStartupFilter(WebKit.Filter(WebKit.Options())).StartAsync();

        using var response = await host.Client.GetAsync(new Uri("/web/index.html", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/jf/web/", response.Headers.Location?.ToString());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task WithoutBodyEnd_TheDocumentIsServedUnchanged()
    {
        const string Html = "<html><head></head><p>no body end</p></html>";
        await using var host = await new WebHostHarness().WithIndexHtml(Html).WithStartupFilter(WebKit.Filter(WebKit.Options())).StartAsync();

        using var response = await host.SendAsync(HttpMethod.Get, "/web/index.html");

        Assert.Equal(Html, await response.Content.ReadAsStringAsync(Ct));
        Assert.Null(response.Headers.ETag);
        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task AnExistingMarker_LeavesTheDocumentUnchanged()
    {
        var html = WebHostHarness.SyntheticIndexHtml.Replace("</body>", "<script data-jfkt-plugin=\"0\" src=\"x\"></script></body>", StringComparison.Ordinal);
        await using var host = await new WebHostHarness().WithIndexHtml(html).WithStartupFilter(WebKit.Filter(WebKit.Options())).StartAsync();

        Assert.Equal(html, await host.GetStringAsync("/web/index.html"));
    }

    [Fact]
    public async Task WebUiEnabled_IsReadOnEveryRequest_AndOffMeansUntouched()
    {
        var enabled = true;
        await using var plain = await new WebHostHarness().StartAsync();
        await using var host = await new WebHostHarness().WithStartupFilter(WebKit.Filter(WebKit.Options(webUiEnabled: _ => enabled))).StartAsync();

        Assert.Contains("data-jfkt-plugin", await host.GetStringAsync("/web/index.html"), StringComparison.Ordinal);

        enabled = false;
        using var off = await host.SendAsync(HttpMethod.Get, "/web/index.html", r => r.Headers.AcceptEncoding.ParseAdd("gzip"));
        using var reference = await plain.SendAsync(HttpMethod.Get, "/web/index.html", r => r.Headers.AcceptEncoding.ParseAdd("gzip"));
        Assert.Equal("gzip", Assert.Single(off.Content.Headers.ContentEncoding));
        Assert.NotNull(off.Headers.ETag);
        Assert.Equal(await Gunzip(reference), await Gunzip(off));
        using var conditional = await host.SendAsync(HttpMethod.Get, "/web/index.html", r => r.Headers.IfNoneMatch.Add(off.Headers.ETag!));
        Assert.Equal(HttpStatusCode.NotModified, conditional.StatusCode);

        enabled = true;
        Assert.Contains("data-jfkt-plugin", await host.GetStringAsync("/web/index.html"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebUiEnabled_GetsTheRequestServices()
    {
        var setting = new WebUiSetting { Enabled = false };
        await using var host = await new WebHostHarness()
            .WithServices(services => services.AddSingleton(setting))
            .WithStartupFilter(WebKit.Filter(WebKit.Options(webUiEnabled: services => services.GetRequiredService<WebUiSetting>().Enabled)))
            .StartAsync();

        Assert.DoesNotContain("data-jfkt-plugin", await host.GetStringAsync("/web/index.html"), StringComparison.Ordinal);
        setting.Enabled = true;
        Assert.Contains("data-jfkt-plugin", await host.GetStringAsync("/web/index.html"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailingSetting_LeavesIndexAlone_AndWarnsOnce()
    {
        var logger = new ListLogger();
        var options = WebKit.Options(webUiEnabled: _ => throw new InvalidOperationException("settings unavailable"));
        await using var host = await new WebHostHarness().WithStartupFilter(WebKit.Filter(options, logger)).StartAsync();

        Assert.Equal(WebHostHarness.SyntheticIndexHtml, await host.GetStringAsync("/web/index.html"));
        Assert.Equal(WebHostHarness.SyntheticIndexHtml, await host.GetStringAsync("/web/"));

        var warning = Assert.Single(logger.At(LogLevel.Warning));
        Assert.IsType<InvalidOperationException>(warning.Exception);
    }

    [Fact]
    public async Task AFailingTransform_ServesTheOriginalBytes_AndWarnsOncePerProcess()
    {
        // Invalid UTF-8 makes the transform throw: the document is served exactly as the inner pipeline produced it.
        byte[] original = [.. Utf8.GetBytes("<html><head></head><body>"), 0xC3, 0x28, .. Utf8.GetBytes("</body></html>")];
        var logger = new ListLogger();
        await using var host = await new WebHostHarness()
            .WithFile("index.html", original)
            .WithStartupFilter(WebKit.Filter(WebKit.Options(), logger))
            .StartAsync();

        for (var i = 0; i < 2; i++)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/web/index.html", r => r.Headers.AcceptEncoding.ParseAdd("gzip"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(original, await response.Content.ReadAsByteArrayAsync(Ct));
            Assert.Empty(response.Content.Headers.ContentEncoding);
            Assert.Null(response.Headers.ETag);
        }

        var warning = Assert.Single(logger.At(LogLevel.Warning));
        Assert.IsType<DecoderFallbackException>(warning.Exception);
        Assert.Contains("original index.html", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoInstancesOfOnePlugin_InsertOnce()
    {
        var options = WebKit.Options();
        await using var host = await new WebHostHarness().WithStartupFilter(WebKit.Filter(options)).WithStartupFilter(WebKit.Filter(options)).StartAsync();

        Assert.Equal(Expected(WebHostHarness.SyntheticIndexHtml, WebKit.ExpectedTag(options)), await host.GetStringAsync("/web/index.html"));
    }

    [Fact]
    public async Task TwoPlugins_EachInsertOnce_TheOuterTagLast()
    {
        var outer = WebKit.Options(prefix: "jfar", routePrefix: "AccessRoles");
        var inner = WebKit.Options(prefix: "jfur", routePrefix: "UserRatings");
        await using var host = await new WebHostHarness().WithStartupFilter(WebKit.Filter(outer)).WithStartupFilter(WebKit.Filter(inner)).StartAsync();

        var html = await host.GetStringAsync("/web/index.html");

        Assert.Equal(Expected(WebHostHarness.SyntheticIndexHtml, WebKit.ExpectedTag(inner) + WebKit.ExpectedTag(outer)), html);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NextToAJellyfinEnhancedLikeFilter_InEitherOrder_EachTagOnce_TheOuterLast(bool oursOuter)
    {
        var options = WebKit.Options();
        var harness = new WebHostHarness();
        if (oursOuter)
        {
            harness.WithStartupFilter(WebKit.Filter(options)).WithStartupFilter(_ => new JellyfinEnhancedLikeStartupFilter());
        }
        else
        {
            harness.WithStartupFilter(_ => new JellyfinEnhancedLikeStartupFilter()).WithStartupFilter(WebKit.Filter(options));
        }

        await using var host = await harness.StartAsync();

        // A validator still reaches us when we are inside JE, which doesn't strip If-None-Match.
        using var head = await host.SendAsync(HttpMethod.Head, "/web/index.html");
        using var response = await host.SendAsync(HttpMethod.Get, "/web/index.html", r =>
        {
            r.Headers.AcceptEncoding.ParseAdd("gzip, br");
            r.Headers.IfNoneMatch.Add(head.Headers.ETag!);
            r.Headers.IfModifiedSince = head.Content.Headers.LastModified;
        });
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, Count(html, "data-jfkt-plugin"));
        Assert.Equal(1, Count(html, JellyfinEnhancedLikeStartupFilter.Needle));
        var ours = html.IndexOf("data-jfkt-plugin", StringComparison.Ordinal);
        var je = html.IndexOf(JellyfinEnhancedLikeStartupFilter.Needle, StringComparison.Ordinal);
        Assert.Equal(oursOuter, ours > je);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Null(response.Headers.ETag);
        Assert.Null(response.Content.Headers.LastModified);
        Assert.Empty(response.Headers.AcceptRanges);
        Assert.Equal(Utf8.GetByteCount(html), response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task AJellyfinEnhancedLikeFilterAlone_Answers304_WhichIsWhyWeStripValidators()
    {
        await using var host = await new WebHostHarness().WithStartupFilter(_ => new JellyfinEnhancedLikeStartupFilter()).StartAsync();

        using var head = await host.SendAsync(HttpMethod.Head, "/web/index.html");
        using var conditional = await host.SendAsync(HttpMethod.Get, "/web/index.html", r => r.Headers.IfNoneMatch.Add(head.Headers.ETag!));

        Assert.Equal(HttpStatusCode.NotModified, conditional.StatusCode);
    }

    [Fact]
    public async Task AtTheCap_TheDocumentIsStillTransformed()
    {
        var html = Pad(WebHostHarness.SyntheticIndexHtml, IndexHtmlInjectionStartupFilter.MaxBufferedBytes);
        var options = WebKit.Options();
        await using var host = await new WebHostHarness().WithIndexHtml(html).WithStartupFilter(WebKit.Filter(options)).StartAsync();

        Assert.Equal(Expected(html, WebKit.ExpectedTag(options)), await host.GetStringAsync("/web/index.html"));
    }

    [Fact]
    public async Task PastTheCap_TheOriginalPassesThroughUnchanged_AndOneWarningIsLogged()
    {
        // The 1 MB overflow path (never exercised live).
        var html = Pad(WebHostHarness.SyntheticIndexHtml, IndexHtmlInjectionStartupFilter.MaxBufferedBytes + 1);
        var logger = new ListLogger();
        await using var host = await new WebHostHarness().WithIndexHtml(html).WithStartupFilter(WebKit.Filter(WebKit.Options(), logger)).StartAsync();

        for (var i = 0; i < 2; i++)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/web/index.html", r => r.Headers.AcceptEncoding.ParseAdd("gzip"));
            var body = await response.Content.ReadAsByteArrayAsync(Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(Utf8.GetBytes(html), body);
            Assert.Equal(body.Length, response.Content.Headers.ContentLength);
            Assert.Empty(response.Content.Headers.ContentEncoding);
        }

        Assert.Contains("larger than 1048576 bytes", Assert.Single(logger.At(LogLevel.Warning)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANon200Response_PassesThrough()
    {
        await using var host = await new WebHostHarness().WithStartupFilter(WebKit.Filter(WebKit.Options())).StartAsync();
        File.Delete(Path.Combine(host.WebRoot, "index.html"));

        using var response = await host.SendAsync(HttpMethod.Get, "/web/index.html");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.CacheControl);
    }

    [Theory]
    [InlineData("application/json", null)]
    [InlineData("text/plain", null)]
    [InlineData("text/html", "br")]
    public async Task NonHtmlOrEncodedResponses_PassThroughUntouched(string contentType, string? contentEncoding)
    {
        // Another plugin inside ours answers the index path itself.
        var body = Utf8.GetBytes("<html><body>not ours</body></html>");
        await using var host = await new WebHostHarness()
            .WithStartupFilter(WebKit.Filter(WebKit.Options()))
            .WithStartupFilter(_ => new MiddlewareStartupFilter(async (context, next) =>
            {
                context.Response.ContentType = contentType;
                context.Response.Headers.ETag = "\"inner\"";
                if (contentEncoding is not null)
                {
                    context.Response.Headers.ContentEncoding = contentEncoding;
                }

                await context.Response.Body.WriteAsync(body, context.RequestAborted);
            }))
            .StartAsync();

        using var response = await host.SendAsync(HttpMethod.Get, "/web/index.html");

        Assert.Equal(body, await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("\"inner\"", response.Headers.ETag?.Tag);
        Assert.Null(response.Headers.CacheControl);
    }

    [Fact]
    public async Task ExceptionsFromThePipeline_Propagate_AfterTheBodyIsRestored()
    {
        var seenByOuter = new ConcurrentQueue<string>();
        await using var host = await new WebHostHarness()
            .WithStartupFilter(_ => new MiddlewareStartupFilter(async (context, next) =>
            {
                var body = context.Response.Body;
                try
                {
                    await next(context);
                }
                catch (InvalidOperationException ex) when (ex.Message == "inner failure")
                {
                    seenByOuter.Enqueue(ReferenceEquals(body, context.Response.Body) ? "restored" : "replaced");
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                }
            }))
            .WithStartupFilter(WebKit.Filter(WebKit.Options()))
            .WithStartupFilter(_ => new MiddlewareStartupFilter((_, _) => throw new InvalidOperationException("inner failure")))
            .StartAsync();

        using var response = await host.SendAsync(HttpMethod.Get, "/web/index.html");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("restored", Assert.Single(seenByOuter));
    }

    [Fact]
    public async Task AnAssemblyWithoutAnMvid_DisablesInjection_WithoutThrowing()
    {
        var logger = new ListLogger();
        var options = TestKit.Create();

        var filter = new IndexHtmlInjectionStartupFilter(options, Mock.Of<IServerConfigurationManager>(), WebKit.LoggerFactory(logger));
        await using var host = await new WebHostHarness().WithStartupFilter(_ => filter).StartAsync();

        Assert.Null(filter.ScriptTag);
        Assert.Equal(WebHostHarness.SyntheticIndexHtml, await host.GetStringAsync("/web/index.html"));
        Assert.Single(logger.At(LogLevel.Error));
    }

    [Fact]
    public void AddIndexHtmlInjection_RegistersTheFilterOnce_ResolvableFromTheKitServices()
    {
        var services = new ServiceCollection();
        services.AddKit(WebKit.Options());
        services.AddIndexHtmlInjection().AddIndexHtmlInjection();
        services.AddSingleton(Mock.Of<IServerConfigurationManager>());
        services.AddLogging();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IStartupFilter));
        Assert.Equal(typeof(IndexHtmlInjectionStartupFilter), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        using var provider = services.BuildServiceProvider();
        var filter = Assert.IsType<IndexHtmlInjectionStartupFilter>(Assert.Single(provider.GetServices<IStartupFilter>()));
        Assert.Equal(WebKit.ExpectedTag(WebKit.Options()), filter.ScriptTag);
    }

    internal static string Expected(string html, string insertedBeforeBodyEnd)
    {
        var end = html.LastIndexOf("</body>", StringComparison.Ordinal);
        return html.Insert(end, insertedBeforeBodyEnd);
    }

    internal static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }

    internal static async Task<byte[]> Gunzip(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        if (!response.Content.Headers.ContentEncoding.Contains("gzip"))
        {
            return bytes;
        }

        using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var output = new MemoryStream();
        await input.CopyToAsync(output, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    // Pads the document with a comment before </body> so that it is exactly `bytes` long in UTF-8.
    private static string Pad(string html, int bytes)
    {
        var end = html.LastIndexOf("</body>", StringComparison.Ordinal);
        var filler = bytes - Utf8.GetByteCount(html) - "<!---->".Length;
        var padded = html.Insert(end, "<!--" + new string('x', filler) + "-->");
        Assert.Equal(bytes, Utf8.GetByteCount(padded));
        return padded;
    }

    private static MiddlewareStartupFilter RecordBodyStream(ConcurrentQueue<string> bodies)
        => new((context, next) =>
        {
            bodies.Enqueue(context.Response.Body.GetType().Name);
            return next(context);
        });

    private sealed class WebUiSetting
    {
        public bool Enabled { get; set; }
    }
}
