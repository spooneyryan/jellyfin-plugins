using System;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Tests.Fixtures;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.Tests.Web;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.TestSupport;

/// <summary>
/// The harness itself: it reproduces what the injection middleware meets in Jellyfin 10.11.5 (validators,
/// 304s, compression, BaseUrl redirects), and <see cref="WebHostHarness.WithMvc(Type[])"/> gives MVC with
/// <c>[ApiController]</c> validation, Jellyfin's authorisation and global filters in Jellyfin's order.
/// </summary>
public sealed class WebHostHarnessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithoutPlugins_IndexHasTheStaticFileBehaviourOurMiddlewareUndoes()
    {
        await using var host = await new WebHostHarness().StartAsync();

        using var response = await host.SendAsync(HttpMethod.Get, "/web/index.html", r => r.Headers.AcceptEncoding.ParseAdd("gzip"));
        using var conditional = await host.SendAsync(HttpMethod.Get, "/web/index.html", r => r.Headers.IfNoneMatch.Add(response.Headers.ETag!));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.NotNull(response.Headers.ETag);
        Assert.NotNull(response.Content.Headers.LastModified);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Equal(Encoding.UTF8.GetBytes(WebHostHarness.SyntheticIndexHtml), await IndexHtmlInjectionStartupFilterTests.Gunzip(response));
        Assert.Equal(HttpStatusCode.NotModified, conditional.StatusCode);
    }

    [Theory]
    [InlineData("", "/emby/web/index.html")]
    [InlineData("", "/MediaBrowser/web/")]
    [InlineData("/jf", "/emby/web/")]
    public async Task LegacyPrefixes_ServeTheWebClient_AsJellyfinsUsePathTrimDoes(string baseUrl, string path)
    {
        await using var host = await new WebHostHarness().WithBaseUrl(baseUrl).StartAsync();

        using var response = await host.SendAsync(HttpMethod.Get, path, r => r.Headers.AcceptEncoding.ParseAdd("gzip"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.NotNull(response.Headers.ETag);
        Assert.Equal(Encoding.UTF8.GetBytes(WebHostHarness.SyntheticIndexHtml), await IndexHtmlInjectionStartupFilterTests.Gunzip(response));
    }

    [Theory]
    [InlineData("/emby/emby/web/index.html")]
    [InlineData("/emby/mediabrowser/web/")]
    public async Task OnlyOneLegacyPrefix_IsRemoved(string path)
    {
        await using var host = await new WebHostHarness().StartAsync();

        using var response = await host.SendAsync(HttpMethod.Get, path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("", "/", "/web/")]
    [InlineData("/jf", "/jf", "/jf/web/")]
    [InlineData("/jf", "/web/index.html", "/jf/web/")]
    public async Task PathsOutsideTheBaseUrl_AreRedirectedToTheWebClient(string baseUrl, string path, string location)
    {
        await using var host = await new WebHostHarness().WithBaseUrl(baseUrl).StartAsync();

        using var response = await host.Client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData("jf", "/jf")]
    [InlineData("/jf/", "/jf")]
    [InlineData("", "")]
    public async Task BaseUrl_IsNormalisedLikeJellyfins(string configured, string expected)
    {
        await using var harness = new WebHostHarness();

        Assert.Equal(expected, harness.WithBaseUrl(configured).BaseUrl);
    }

    [Fact]
    public async Task ConfiguringAfterStart_Throws()
    {
        await using var host = await new WebHostHarness().StartAsync();

        Assert.Throws<InvalidOperationException>(() => host.WithBaseUrl("/jf"));
        Assert.Throws<InvalidOperationException>(() => host.WithMvc(typeof(UserController)));
    }

    [Fact]
    public async Task WithMvc_ValidatesBodiesLikeApiControllers()
    {
        var log = new ConcurrentQueue<string>();
        await using var host = await StartMvc(log);

        using var valid = await Post(host, "{\"AudioLanguagePreference\":\"eng\",\"MaxRating\":5}");
        using var invalid = await Post(host, "{\"MaxRating\":\"many\"}");
        var problem = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync(Ct)).RootElement;

        Assert.Equal(HttpStatusCode.NoContent, valid.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
        Assert.True(problem.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task WithMvc_GlobalFiltersRunInOrder_TheFirstBeforeModelValidation()
    {
        // Access Roles' filter at -10000 runs before [ApiController] validation (-2000) and JE's filters (0), and last on
        // the way out.
        var log = new ConcurrentQueue<string>();
        await using var host = await StartMvc(log);

        using var valid = await Post(host, "{\"AudioLanguagePreference\":\"eng\"}");
        Assert.Equal(["access-roles:before", "je:before", "action", "je:after", "access-roles:after"], log);

        log.Clear();
        using var invalid = await Post(host, "{\"MaxRating\":99}");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(["access-roles:before", "access-roles:after"], log);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("user", HttpStatusCode.Forbidden)]
    [InlineData("apikey", HttpStatusCode.OK)]
    [InlineData("admin", HttpStatusCode.OK)]
    public async Task WithMvc_UsesJellyfinsRequiresElevation(string? caller, HttpStatusCode expected)
    {
        await using var host = await StartMvc(new ConcurrentQueue<string>());

        using var response = await host.SendAsync(HttpMethod.Get, "/Trailers/Admin", r => AddCaller(r, caller));

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("admin", false, "true")]
    [InlineData("admin", true, "false")]
    [InlineData("user", false, "false")]
    public async Task WithMvc_IsElevatedAsync_FollowsAVetoingHandler(string caller, bool veto, string expected)
    {
        await using var host = await StartMvc(new ConcurrentQueue<string>(), services => services.AddSingleton<IAuthorizationHandler, FxVetoHandler>());

        using var response = await host.SendAsync(HttpMethod.Get, "/Trailers/IsElevated", r =>
        {
            AddCaller(r, caller);
            if (veto)
            {
                r.Headers.Add(FxVetoHandler.Header, "1");
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task WithMvc_HeadAndBodyInjectionHappenOnceEach_AndApiResponsesStayCompressed()
    {
        var options = WebKit.Options(headFragment: _ => "<style data-jfkt-head>.x{}</style>");
        await using var host = await new WebHostHarness()
            .WithBaseUrl("/jf")
            .WithStartupFilter(WebKit.Filter(options))
            .WithStartupFilter(_ => new JellyfinEnhancedLikeStartupFilter())
            .WithMvc(typeof(UserController), typeof(TrailersController))
            .StartAsync();

        var html = await host.GetStringAsync("/web/index.html");
        using var api = await host.SendAsync(HttpMethod.Get, "/Trailers/IsElevated", r =>
        {
            AddCaller(r, "admin");
            r.Headers.AcceptEncoding.ParseAdd("gzip");
        });

        Assert.Equal(1, IndexHtmlInjectionStartupFilterTests.Count(html, "data-jfkt-head"));
        Assert.Equal(1, IndexHtmlInjectionStartupFilterTests.Count(html, "data-jfkt-plugin"));
        Assert.Equal(1, IndexHtmlInjectionStartupFilterTests.Count(html, JellyfinEnhancedLikeStartupFilter.Needle));
        Assert.Equal("gzip", Assert.Single(api.Content.Headers.ContentEncoding));
        Assert.Equal("true", Encoding.UTF8.GetString(await IndexHtmlInjectionStartupFilterTests.Gunzip(api)));
    }

    private static Task<WebHostHarness> StartMvc(ConcurrentQueue<string> log, Action<IServiceCollection>? services = null)
        => new WebHostHarness()
            .WithServices(s =>
            {
                s.AddSingleton(log);
                services?.Invoke(s);
            })
            .WithMvc(
                options =>
                {
                    options.Filters.Add(new RecordingFilter("je", 0, log));
                    options.Filters.Add(new RecordingFilter("access-roles", -10000, log));
                },
                typeof(UserController),
                typeof(TrailersController))
            .StartAsync();

    private static Task<HttpResponseMessage> Post(WebHostHarness host, string json)
        => host.SendAsync(HttpMethod.Post, "/Users/Configuration?userId=" + Guid.NewGuid().ToString("N"), r =>
        {
            AddCaller(r, "user");
            r.Content = new StringContent(json, Encoding.UTF8, "application/json");
        });

    private static void AddCaller(HttpRequestMessage request, string? caller)
    {
        if (caller is not null)
        {
            request.Headers.Add(JellyfinAuth.CallerHeader, caller);
        }
    }

    /// <summary>Named like Jellyfin's <c>UserController.UpdateUserConfiguration</c>.</summary>
    [ApiController]
    [Authorize]
    public sealed class UserController : ControllerBase
    {
        private readonly ConcurrentQueue<string> _log;

        public UserController(ConcurrentQueue<string> log)
        {
            _log = log;
        }

        [HttpPost("Users/Configuration")]
        public ActionResult UpdateUserConfiguration([FromQuery, Required] Guid? userId, [FromBody, Required] FakeUserConfiguration configuration)
        {
            _log.Enqueue("action");
            return NoContent();
        }
    }

    /// <summary>Named like Jellyfin's <c>TrailersController</c>.</summary>
    [ApiController]
    [Authorize]
    public sealed class TrailersController : ControllerBase
    {
        [HttpGet("Trailers/Admin")]
        [Authorize(Policy = Policies.RequiresElevation)]
        public ActionResult<string> Admin() => "admin";

        [HttpGet("Trailers/IsElevated")]
        public async Task<ContentResult> IsElevated()
            => Content((await CurrentUser.IsElevatedAsync(HttpContext)) ? "true" : "false", "application/json");
    }

    public sealed class FakeUserConfiguration
    {
        public string? AudioLanguagePreference { get; set; }

        [Range(0, 10)]
        public int MaxRating { get; set; }
    }

    private sealed class RecordingFilter : IAsyncActionFilter, IOrderedFilter
    {
        private readonly string _name;
        private readonly ConcurrentQueue<string> _log;

        public RecordingFilter(string name, int order, ConcurrentQueue<string> log)
        {
            _name = name;
            Order = order;
            _log = log;
        }

        public int Order { get; }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            _log.Enqueue(_name + ":before");
            await next();
            _log.Enqueue(_name + ":after");
        }
    }
}
