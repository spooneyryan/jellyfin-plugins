using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Tests.Fixtures;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Api;

/// <summary>
/// PluginExceptionFilter and KitProblemDetails through a real MVC pipeline.
/// </summary>
public sealed class PluginExceptionFilterTests : IAsyncLifetime
{
    private readonly RecordingLoggerProvider _logs = new();
    private MvcTestHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await MvcTestHost.StartAsync(
            [typeof(FxErrorsController)],
            services =>
            {
                services.AddLogging(logging => logging.AddProvider(_logs).SetMinimumLevel(LogLevel.Debug));
                services.AddKit(TestKit.Options);
            });
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    public static TheoryData<string> KindNames => new(Enum.GetNames<PluginErrorKind>());

    [Theory]
    [MemberData(nameof(KindNames))]
    public async Task ThrownException_BecomesProblemDetails(string kind)
    {
        var (status, contentType, body) = await GetAsync("/Fx/Errors/Throw/" + kind);
        var expected = KitProblemDetails.For(Enum.Parse<PluginErrorKind>(kind), "Detail for " + kind + ".");

        Assert.Equal(expected.Status, status);
        Assert.Equal("application/problem+json", contentType);
        Assert.Equal(expected.Title, body.GetProperty("title").GetString());
        Assert.Equal(expected.Status, body.GetProperty("status").GetInt32());
        Assert.Equal(expected.Detail, body.GetProperty("detail").GetString());
        Assert.Equal(expected.Type, body.TryGetProperty("type", out var type) ? type.GetString() : null);
    }

    [Theory]
    [MemberData(nameof(KindNames))]
    public async Task ThrownAndReturned_SendTheSameBody(string kind)
    {
        var thrown = await GetRawAsync("/Fx/Errors/Throw/" + kind);
        var returned = await GetRawAsync("/Fx/Errors/Result/" + kind);

        Assert.Equal(thrown, returned);
    }

    [Theory]
    [InlineData("Invalid", """{"title":"Bad Request","status":400,"detail":"Detail for Invalid."}""")]
    [InlineData("Forbidden", """{"title":"Forbidden","status":403,"detail":"Detail for Forbidden."}""")]
    [InlineData("NotFound", """{"title":"Not Found","status":404,"detail":"Detail for NotFound."}""")]
    [InlineData("FeatureDisabled", """{"type":"urn:jellyfin-plugin-kit:feature-disabled","title":"Forbidden","status":403,"detail":"Detail for FeatureDisabled."}""")]
    [InlineData("Locked", """{"type":"urn:jellyfin-plugin-kit:locked","title":"Forbidden","status":403,"detail":"Detail for Locked."}""")]
    public async Task Body_IsExactlyTheBookmarks10Shape(string kind, string expected)
    {
        // Bookmarks 1.0 sent {title, status, detail} as application/problem+json (sandbox baseline); the kit adds a
        // type only to the two 403 kinds the client words differently.
        Assert.Equal(expected, (await GetRawAsync("/Fx/Errors/Throw/" + kind)).Body);
    }

    [Theory]
    [MemberData(nameof(KindNames))]
    public async Task NoErrorEverCarriesTheParentalControlCode(string kind)
    {
        // jellyfin-web signs the user out on a 403 whose error code is ParentalControl.
        foreach (var path in new[] { "/Fx/Errors/Throw/", "/Fx/Errors/Result/" })
        {
            using var response = await _host.Client.SendAsync(_host.Request(HttpMethod.Get, path + kind, "user"), TestContext.Current.CancellationToken);
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.DoesNotContain("ParentalControl", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                response.Headers.Concat(response.Content.Headers),
                header => header.Value.Any(v => v.Contains("ParentalControl", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public async Task AnErrorsCode_IsTheCodeExtension_AndOnlyThere()
    {
        // A stable code a page tells two 409s apart by; an error without one has no code at all.
        Assert.Equal("""{"title":"Conflict","status":409,"detail":"Saved since.","code":"FxChanged"}""", (await GetRawAsync("/Fx/Errors/Coded")).Body);
        Assert.DoesNotContain("code", (await GetRawAsync("/Fx/Errors/Throw/Conflict")).Body, StringComparison.Ordinal);
        Assert.Equal(
            """{"title":"Conflict","status":409,"detail":"Saved since.","code":"FxChanged"}""",
            System.Text.Json.JsonSerializer.Serialize(KitProblemDetails.For(PluginErrorKind.Conflict, "Saved since.", "FxChanged")));
    }

    [Fact]
    public async Task AnErrorsExtensions_FollowItsCode()
    {
        Assert.Equal(
            """{"title":"Bad Request","status":400,"detail":"Too broad.","code":"FxBroad","candidateCount":12,"kinds":["Movie"]}""",
            (await GetRawAsync("/Fx/Errors/Extended")).Body);
    }

    [Fact]
    public async Task DerivedException_IsHandledWithItsKind()
    {
        var (status, _, body) = await GetAsync("/Fx/Errors/Derived");

        Assert.Equal(409, status);
        Assert.Equal("A thin subclass.", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ExpectedErrors_AreLoggedAtDebugWithThePluginCategory()
    {
        await GetAsync("/Fx/Errors/Throw/NotFound");

        var entry = Assert.Single(_logs.Entries, e => e.Category == "Jellyfin.Plugin.KitTest.Kit.Api");
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("NotFound", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Success_IsUntouched()
    {
        using var response = await _host.Client.SendAsync(_host.Request(HttpMethod.Get, "/Fx/Errors/Ok", "user"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ABodyOverTheRequestSizeLimit_Is413ProblemDetails_NotJellyfins500()
    {
        // Abuse limits: Kestrel stops reading a body over the action's [RequestSizeLimit] with a 413
        // BadHttpRequestException, thrown while MVC binds the body. Without the filter it reaches Jellyfin's
        // ExceptionMiddleware, which answers 500 and logs an Error (v10.11.5 Jellyfin.Api/Middleware/ExceptionMiddleware.cs).
        // The test server enforces no size limit, so the action throws what Kestrel throws; bookmarks-api-probe.mjs sends a
        // body over Bookmarks' 64 KB to a sandbox's Kestrel.
        var (status, contentType, body) = await GetAsync("/Fx/Errors/TooLarge");

        Assert.Equal(413, status);
        Assert.Equal("application/problem+json", contentType);
        Assert.Equal("""{"title":"Payload Too Large","status":413,"detail":"The request is too large."}""", body.GetRawText());
        var entry = Assert.Single(_logs.Entries, e => e.Category == "Jellyfin.Plugin.KitTest.Kit.Api");
        Assert.Equal(LogLevel.Debug, entry.Level);
    }

    [Fact]
    public async Task ABodyUnderTheLimit_IsBound()
    {
        using var request = _host.Request(HttpMethod.Post, "/Fx/Errors/Body", "user");
        request.Content = new StringContent("""{"Text":"small"}""", System.Text.Encoding.UTF8, "application/json");
        using var response = await _host.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"small\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OtherBadRequestErrorsOfKestrel_PassThrough()
    {
        // Only the size limit's 413 is the filter's; Kestrel's other bad-request errors stay Jellyfin's to answer (the test
        // server hands them to the client).
        await Assert.ThrowsAsync<BadHttpRequestException>(
            () => _host.Client.SendAsync(_host.Request(HttpMethod.Get, "/Fx/Errors/BadRequest", "user"), TestContext.Current.CancellationToken));
        Assert.DoesNotContain(_logs.Entries, e => e.Category == "Jellyfin.Plugin.KitTest.Kit.Api");
    }

    [Fact]
    public void OtherExceptions_PassThrough()
    {
        foreach (var exception in new Exception[] { new InvalidOperationException("A bug."), new BadHttpRequestException("Bad chunk.", StatusCodes.Status400BadRequest) })
        {
            var context = new ExceptionContext(
                new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
                [])
            {
                Exception = exception
            };

            new PluginExceptionFilterAttribute().OnException(context);

            Assert.False(context.ExceptionHandled);
            Assert.Null(context.Result);
        }
    }

    [Fact]
    public void AMessageForUsers_IsWhatACallerWhoIsntAnAdminGets_AndAnAdminOrAnApiKeyTheOther()
    {
        // The JSON store hardening: the store's read-only 503 sends an admin to the plugin's settings page,
        // which a user can't open; a user is told to ask the administrator. Jellyfin's role claim says which (it sets
        // Administrator for admins and API keys); without one, a user's sentence.
        var exception = new PluginException(PluginErrorKind.Unavailable, StoreMessages.DataUnreadable) { NonAdminMessage = StoreMessages.ReadOnlyForUsers(StoreStatus.ReadOnlyDataUnreadable) };
        foreach (var (role, expected) in new[] { ("Administrator", StoreMessages.DataUnreadable), ("User", StoreMessages.ReadOnlyForUsers(StoreStatus.ReadOnlyDataUnreadable)), (null, StoreMessages.ReadOnlyForUsers(StoreStatus.ReadOnlyDataUnreadable)) })
        {
            var http = new DefaultHttpContext();
            if (role is not null)
            {
                http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role)], "Test"));
            }

            var context = new ExceptionContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), []) { Exception = exception };
            new PluginExceptionFilterAttribute().OnException(context);

            var details = Assert.IsType<ProblemDetails>(Assert.IsType<KitProblemResult>(context.Result).Value);
            Assert.Equal(503, details.Status);
            Assert.Equal(expected, details.Detail);
        }

        Assert.EndsWith("Ask your server's administrator.", StoreMessages.ReadOnlyForUsers(StoreStatus.ReadOnlyDataUnreadable), StringComparison.Ordinal);
        Assert.Equal("Administrator", CurrentUser.AdministratorRole);
    }

    [Fact]
    public void WithoutServices_StillMapsTheException()
    {
        var context = new ExceptionContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
            [])
        {
            Exception = PluginException.Locked("Locked by your admin.")
        };

        new PluginExceptionFilterAttribute().OnException(context);

        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<KitProblemResult>(context.Result);
        Assert.Equal(KitProblemDetails.LockedType, Assert.IsType<ProblemDetails>(result.Value).Type);
    }

    private async Task<(int Status, string? ContentType, JsonElement Body)> GetAsync(string path)
    {
        var raw = await GetRawAsync(path);
        return (raw.Status, raw.ContentType, JsonDocument.Parse(raw.Body).RootElement.Clone());
    }

    private async Task<(int Status, string? ContentType, string Body)> GetRawAsync(string path)
    {
        using var response = await _host.Client.SendAsync(_host.Request(HttpMethod.Get, path, "user"), TestContext.Current.CancellationToken);
        return ((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(RecordingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner.Entries.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }
}
