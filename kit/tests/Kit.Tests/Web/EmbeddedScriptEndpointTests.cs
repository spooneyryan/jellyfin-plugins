using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Web;

/// <summary>
/// The ClientScript and AdminScript actions over HTTP, in Jellyfin's pipeline (WebHostHarness): anonymous,
/// <c>text/javascript; charset=utf-8</c>, <c>no-cache</c>, the MVID as ETag and a 304 for a current copy.
/// </summary>
public sealed class EmbeddedScriptEndpointTests
{
    [Theory]
    [InlineData("", "ClientScript")]
    [InlineData("/jf", "ClientScript")]
    [InlineData("", "AdminScript")]
    [InlineData("/jf", "AdminScript")]
    public async Task Script_IsAnonymousJavaScript_WithNoCacheAndTheMvidAsETag(string baseUrl, string action)
    {
        await using var host = await StartAsync(baseUrl);
        var scripts = host.Services.GetRequiredService<EmbeddedScript>();
        var expected = action == "ClientScript" ? scripts.ClientScriptText : scripts.AdminScriptText!;

        using var response = await host.SendAsync(HttpMethod.Get, "/Fx/" + action);
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Equal(scripts.ETag.Tag.ToString(), response.Headers.ETag?.Tag);
        Assert.Equal(expected, new UTF8Encoding(false, true).GetString(body));
        Assert.Null(response.Content.Headers.LastModified);
    }

    [Theory]
    [InlineData("ClientScript")]
    [InlineData("AdminScript")]
    public async Task ACurrentCopy_Gets304(string action)
    {
        await using var host = await StartAsync(string.Empty);
        var etag = host.Services.GetRequiredService<EmbeddedScript>().ETag.Tag.ToString();

        using var current = await host.SendAsync(HttpMethod.Get, "/Fx/" + action, r => r.Headers.TryAddWithoutValidation("If-None-Match", etag));
        using var stale = await host.SendAsync(HttpMethod.Get, "/Fx/" + action, r => r.Headers.TryAddWithoutValidation("If-None-Match", "\"0000\""));

        Assert.Equal(HttpStatusCode.NotModified, current.StatusCode);
        Assert.Empty(await current.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
    }

    [Fact]
    public async Task ACompressedResponse_DecodesToTheScript()
    {
        await using var host = await StartAsync(string.Empty);
        var expected = host.Services.GetRequiredService<EmbeddedScript>().ClientScriptText;

        using var response = await host.SendAsync(HttpMethod.Get, "/Fx/ClientScript", r => r.Headers.AcceptEncoding.ParseAdd("gzip"));
        await using var raw = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        await using var body = response.Content.Headers.ContentEncoding.Contains("gzip") ? new GZipStream(raw, CompressionMode.Decompress) : raw;
        using var reader = new StreamReader(body, Encoding.UTF8);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APluginWithoutAdminFiles_Answers404ProblemDetails()
    {
        var plugin = new ResourceAssembly()
            .WithResourcesOf(typeof(EmbeddedScriptEndpointTests).Assembly, EmbeddedScript.KitScriptResource, EmbeddedScript.KitCssResource, WebAnchorCatalog.KitResource)
            .With("Web.js.10-a.js", "var a = 1;");
        await using var host = await new WebHostHarness()
            .WithServices(services => services.AddSingleton(EmbeddedScriptTests.Options(plugin)).AddEmbeddedScript())
            .WithMvc(typeof(FxScriptsController))
            .StartAsync();

        using var client = await host.SendAsync(HttpMethod.Get, "/Fx/ClientScript");
        using var admin = await host.SendAsync(HttpMethod.Get, "/Fx/AdminScript");

        Assert.Equal(HttpStatusCode.OK, client.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, admin.StatusCode);
        Assert.Equal("application/problem+json", admin.Content.Headers.ContentType?.MediaType);
    }

    private static Task<WebHostHarness> StartAsync(string baseUrl)
        => new WebHostHarness()
            .WithBaseUrl(baseUrl)
            .WithServices(services => services.AddSingleton(WebKit.Options()).AddEmbeddedScript())
            .WithMvc(typeof(FxScriptsController))
            .StartAsync();

    /// <summary>A plugin controller's two script actions, as the kit's API describes them.</summary>
    [ApiController]
    [Route("Fx")]
    public sealed class FxScriptsController : ControllerBase
    {
        private readonly IEmbeddedScript _scripts;

        public FxScriptsController(IEmbeddedScript scripts)
        {
            _scripts = scripts ?? throw new ArgumentNullException(nameof(scripts));
        }

        [HttpGet("ClientScript")]
        [AllowAnonymous]
        public IActionResult ClientScript() => _scripts.ClientScript();

        [HttpGet("AdminScript")]
        [AllowAnonymous]
        public IActionResult AdminScript() => _scripts.AdminScript();
    }
}
