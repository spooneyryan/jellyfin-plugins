using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.UserRatings.Api;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Tests.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Jellyfin.Plugin.UserRatings.Tests.Api;

/// <summary>
/// The member endpoints in process: <see cref="UserRatingsController"/> behind
/// Jellyfin's web pipeline (<see cref="WebHostHarness"/>: its claims, <c>RequiresElevation</c>, the nullable-enum binder and
/// MVC's model validation), with Jellyfin's PascalCase JSON options, the kit's ABI status, and the real services of
/// <see cref="ServicesFixture"/> over a real <c>userratings.db</c>. The admin controller too, with the real
/// moderation (<see cref="Moderation"/>) behind the real <see cref="UserRatingsAdminApi"/>. Callers are chosen per request:
/// <c>admin</c> (the Administrator role), <c>user</c> (no role) with a user id, <c>apikey</c>, or nobody.
/// </summary>
internal sealed class ApiFixture : IAsyncDisposable
{
    private WebHostHarness? _host;
    private RatingsLibraryConsumer? _lifecycle;

    public ApiFixture()
    {
        Services.UserManager.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => Services.Users.FirstOrDefault(u => u.Id == id));
        Images.Setup(i => i.GetImageCacheTag(It.IsAny<User>())).Returns((User user) => "tag-" + user.Username);
    }

    public ServicesFixture Services { get; } = new();

    public Mock<IImageProcessor> Images { get; } = new();

    /// <summary>Gets the Settings tab's service of the host <see cref="StartAsync"/> started.</summary>
    public RatingsSettingsService SettingsService { get; private set; } = null!;

    /// <summary>Gets the moderation of the host <see cref="StartAsync"/> started, over the store as it was then.</summary>
    public RatingsModeration Moderation { get; private set; } = null!;

    public WebHostHarness Host => _host ?? throw new InvalidOperationException("Call StartAsync first.");

    public static JsonSerializerOptions Json => JsonDefaults.PascalCaseOptions;

    /// <summary>
    /// Starts the host over a service (the fixture's own by default; <see cref="ServicesFixture.NewService"/> after a reopen).
    /// </summary>
    /// <param name="service">The ratings service.</param>
    /// <returns>The host.</returns>
    public async Task<WebHostHarness> StartAsync(RatingsService? service = null)
    {
        var api = new UserRatingsApi(service ?? Services.Service, Services.Settings, Services.Store.Store, Services.UserManager.Object, Images.Object);
        var script = new Mock<IEmbeddedScript>();
        script.Setup(s => s.ClientScript()).Returns(new ContentResult { Content = "/* script */", ContentType = "text/javascript" });
        script.Setup(s => s.AdminScript()).Returns(new ContentResult { Content = "/* module */", ContentType = "text/javascript" });
        Moderation = new RatingsModeration(Services.Store.Store, Services.ItemAccess, Services.UserManager.Object, Services.Library.Object, Services.ArchivedLines, Services.Store.Clock, Services.Store.Logger);
        var applicationHost = new Mock<IServerApplicationHost>();
        applicationHost.SetupGet(h => h.ApplicationVersion).Returns(new Version(10, 11, 5));

        // The moderation admin routes over the real admin API; its other routes aren't part of these tests.
        _lifecycle ??= new RatingsLibraryConsumer(
            applicationHost.Object,
            Services.Store.Store,
            Services.Library.Object,
            Services.UserManager.Object,
            new MoveJournalReader(Services.Store.Directory.Combine("moves.jsonl"), Services.Store.Logger),
            static () => throw new InvalidOperationException("The verifier isn't part of the API tests."),
            static () => throw new InvalidOperationException("The detached records aren't part of the API tests."),
            null,
            Services.Store.Logger,
            Services.Store.Clock);
        SettingsService?.Dispose();
        SettingsService = new RatingsSettingsService(Services.ConfigurationProvider.Object, Services.UserManager.Object, Services.Store.Store, Services.Store.Clock, Services.Store.Logger);
        var admin = new UserRatingsAdminApi(
            _lifecycle,
            static () => throw new InvalidOperationException("The reconciler isn't part of the API tests."),
            static () => throw new InvalidOperationException("The users' owner isn't part of the API tests."),
            () => Moderation,
            () => SettingsService);
        _host = await new WebHostHarness()
            .WithServices(services =>
            {
                KitAbiGuard.Register(services, applicationHost.Object, UserRatingsKit.Options);
                services.AddSingleton(UserRatingsKit.Options);
                services.AddSingleton<IUserRatingsApi>(api);
                services.AddSingleton<IUserRatingsAdminApi>(admin);
                services.AddSingleton(Mock.Of<IKitDiagnostics>());
                services.AddSingleton(Services.UserManager.Object);
                services.AddSingleton(script.Object);
                UseJellyfinJson(services);
            })
            .WithMvc(typeof(UserRatingsController), typeof(UserRatingsAdminController))
            .StartAsync()
            .ConfigureAwait(false);
        return _host;
    }

    /// <summary>
    /// Jellyfin's JSON for MVC (PascalCase, its converters), as the server sets it.
    /// </summary>
    /// <param name="services">The host's services.</param>
    public static void UseJellyfinJson(IServiceCollection services)
        => services.Configure<JsonOptions>(o =>
        {
            var jellyfin = JsonDefaults.PascalCaseOptions;
            o.JsonSerializerOptions.DefaultIgnoreCondition = jellyfin.DefaultIgnoreCondition;
            o.JsonSerializerOptions.NumberHandling = jellyfin.NumberHandling;
            o.JsonSerializerOptions.PropertyNamingPolicy = jellyfin.PropertyNamingPolicy;
            o.JsonSerializerOptions.Converters.Clear();
            foreach (var converter in jellyfin.Converters)
            {
                o.JsonSerializerOptions.Converters.Add(converter);
            }
        });

    /// <summary>
    /// Sends a request as a caller.
    /// </summary>
    /// <param name="caller">The caller: a user (as <c>admin</c> when they are an administrator, else as <c>user</c>),
    /// <see cref="ApiKey"/>, or <c>null</c> for nobody.</param>
    /// <param name="method">The method.</param>
    /// <param name="path">The path.</param>
    /// <param name="json">The JSON body, if any.</param>
    /// <returns>The response.</returns>
    public Task<HttpResponseMessage> SendAsync(object? caller, HttpMethod method, string path, string? json = null)
        => Host.SendAsync(method, path, request =>
        {
            switch (caller)
            {
                case User user:
                    request.Headers.Add(JellyfinAuth.CallerHeader, user == Services.Admin ? "admin" : "user");
                    request.Headers.Add(JellyfinAuth.UserIdHeader, user.Id.ToString("N"));
                    break;
                case AsCaller(var name, var id):
                    request.Headers.Add(JellyfinAuth.CallerHeader, name);
                    request.Headers.Add(JellyfinAuth.UserIdHeader, id.ToString("N"));
                    break;
                case null:
                    break;
                default:
                    throw new ArgumentException("Unknown caller.", nameof(caller));
            }

            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
        });

    /// <summary>
    /// Sends a request and reads the JSON answer, asserting the status.
    /// </summary>
    /// <param name="caller">The caller.</param>
    /// <param name="method">The method.</param>
    /// <param name="path">The path.</param>
    /// <param name="json">The body.</param>
    /// <param name="status">The status expected.</param>
    /// <returns>The JSON, or an empty element for no content.</returns>
    public async Task<JsonElement> JsonAsync(object? caller, HttpMethod method, string path, string? json = null, int status = 200)
    {
        using var response = await SendAsync(caller, method, path, json).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Xunit.Assert.True((int)response.StatusCode == status, $"{method} {path}: {(int)response.StatusCode} {body}");
        if (body.Length == 0)
        {
            return default;
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Sends a request and asserts a ProblemDetails answer.
    /// </summary>
    /// <param name="caller">The caller.</param>
    /// <param name="method">The method.</param>
    /// <param name="path">The path.</param>
    /// <param name="status">The status expected.</param>
    /// <param name="json">The body.</param>
    /// <returns>The problem's <c>detail</c> (null for MVC's validation problems, which have <c>errors</c>).</returns>
    public async Task<string?> ProblemAsync(object? caller, HttpMethod method, string path, int status, string? json = null)
    {
        using var response = await SendAsync(caller, method, path, json).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Xunit.Assert.True((int)response.StatusCode == status, $"{method} {path}: {(int)response.StatusCode} {body}");
        using var problem = JsonDocument.Parse(body);
        Xunit.Assert.Equal(status, problem.RootElement.GetProperty("status").GetInt32());

        // The kit's errors (always with a detail) are application/problem+json; MVC's own client errors (a validation problem
        // with "errors", a 415 for a body that isn't JSON) take the controller's [Produces] type, application/json, as on
        // Bookmarks' routes.
        var kit = problem.RootElement.TryGetProperty("detail", out _);
        Xunit.Assert.Equal(kit ? "application/problem+json" : "application/json", response.Content.Headers.ContentType?.MediaType);
        return problem.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync().ConfigureAwait(false);
        }

        _lifecycle?.Dispose();
        SettingsService?.Dispose();
        Services.Dispose();
    }

    /// <summary>
    /// A caller by name and user id: <c>("user", admin.Id)</c> is the admin's account without the Administrator role on the
    /// request.
    /// </summary>
    /// <param name="Name">The caller's kind.</param>
    /// <param name="UserId">The user id of the claim.</param>
    public sealed record AsCaller(string Name, Guid UserId);

    /// <summary>The API key caller.</summary>
    public static AsCaller ApiKey { get; } = new("apikey", Guid.Empty);
}
