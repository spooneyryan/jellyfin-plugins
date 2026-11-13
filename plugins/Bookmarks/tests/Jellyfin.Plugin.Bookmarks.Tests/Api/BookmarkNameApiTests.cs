using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Bookmarks.Api;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Api;

/// <summary>
/// Names on the wire: the plugin's own registrations behind Jellyfin's web pipeline, with Jellyfin's JSON
/// options. Create, update and <c>POST /Bookmarks/{id}/Name</c> with the name rules; the <c>UpdatedAt</c> the API sends,
/// sent back as <c>IfUpdatedAt</c>, is the token (also a JavaScript <c>Date</c>'s millisecond form of it); a stale one is
/// a 409 ProblemDetails with the bookmark and <c>bookmarks.json</c> unchanged; a name over 100 characters a 400; every POST
/// reads at most 64 KB.
/// </summary>
public sealed class BookmarkNameApiTests : IAsyncLifetime
{
    private static readonly Guid AliceId = new("9fa5c677-8cf7-4a94-bff0-c82a60581ccf");
    private static readonly Guid BobId = new("ff71b896-f205-45ff-936a-c2393f6091f6");
    private static readonly Guid MovieId = new("ba7ef8f5-0757-f5f6-0cae-61b9b7fd19f5");

    private readonly TempDirectory _data = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly User _bob = TestData.CreateUser("bob");

    public BookmarkNameApiTests()
    {
        _alice.Id = AliceId;
        _bob.Id = BobId;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StoreFile => Path.Combine(_data.Path, "bookmarks", "bookmarks.json");

    [Fact]
    public async Task CreateUpdateAndRename_CarryTheNormalisedName()
    {
        await using var host = await StartAsync();

        var (status, created) = await SendAsync(host, "POST", "/Bookmarks", AliceId, $$"""{"ItemId":"{{MovieId}}","StartTicks":600000000,"EndTicks":900000000,"Name":"  Bank\tjob ","Note":"the note"}""");
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("Bank job", created.GetProperty("Name").GetString());
        var id = created.GetProperty("Id").GetString();

        (status, var renamed) = await SendAsync(host, "POST", $"/Bookmarks/{id}/Name", AliceId, """{"Name":"Vault\r\n2"}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Vault 2", renamed.GetProperty("Name").GetString());
        Assert.Equal("the note", renamed.GetProperty("Note").GetString());
        Assert.Equal(600000000, renamed.GetProperty("StartTicks").GetInt64());

        // Bookmarks 1.0's body (neither Name nor IfUpdatedAt) keeps the name; everything else is replaced.
        (status, var from10) = await SendAsync(host, "POST", $"/Bookmarks/{id}", AliceId, """{"StartTicks":610000000,"EndTicks":null,"Note":null}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Vault 2", from10.GetProperty("Name").GetString());
        Assert.False(from10.TryGetProperty("Note", out _));
        Assert.False(from10.TryGetProperty("EndTicks", out _));
        Assert.Equal(610000000, from10.GetProperty("StartTicks").GetInt64());

        // Replace semantics otherwise: an explicit null, or a body with the token and without Name, clears it, and the JSON
        // leaves the null out (as every null).
        (status, var updated) = await SendAsync(host, "POST", $"/Bookmarks/{id}", AliceId, """{"StartTicks":600000000,"EndTicks":900000000,"Name":null,"Note":"the note"}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(updated.TryGetProperty("Name", out _));
        Assert.Equal("the note", updated.GetProperty("Note").GetString());
        (status, var renamedAgain) = await SendAsync(host, "POST", $"/Bookmarks/{id}/Name", AliceId, """{"Name":"Vault"}""");
        Assert.Equal(HttpStatusCode.OK, status);
        (status, var withToken) = await SendAsync(host, "POST", $"/Bookmarks/{id}", AliceId, $$"""{"StartTicks":600000000,"Note":"the note","IfUpdatedAt":"{{renamedAgain.GetProperty("UpdatedAt").GetString()}}"}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(withToken.TryGetProperty("Name", out _));

        (status, var unnamed) = await SendAsync(host, "POST", $"/Bookmarks/{id}/Name", AliceId, """{"Name":"  "}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(unnamed.TryGetProperty("Name", out _));
        Assert.Null(host.Services.GetRequiredService<IBookmarkStore>().Get(Guid.Parse(id!))!.Name);
    }

    [Fact]
    public async Task TheUpdatedAtTheApiSent_IsTheToken_AndAStaleOneIs409ProblemDetails_WithNothingChanged()
    {
        await using var host = await StartAsync();
        var (_, created) = await SendAsync(host, "POST", "/Bookmarks", AliceId, $$"""{"ItemId":"{{MovieId}}","StartTicks":600000000,"Name":"Bank job"}""");
        var id = created.GetProperty("Id").GetString();
        var token = created.GetProperty("UpdatedAt").GetString()!;

        // The text the API sent, as it sent it.
        var (status, first) = await SendAsync(host, "POST", $"/Bookmarks/{id}/Name", AliceId, $$"""{"Name":"Tab one","IfUpdatedAt":"{{token}}"}""");
        Assert.Equal(HttpStatusCode.OK, status);
        var next = first.GetProperty("UpdatedAt").GetString()!;
        Assert.NotEqual(token, next);

        // The other tab still holds the first token: 409, ProblemDetails with the client's sentence, nothing written.
        var before = await File.ReadAllBytesAsync(StoreFile, Ct);
        using (var conflict = await SendRawAsync(host, "POST", $"/Bookmarks/{id}/Name", AliceId, $$"""{"Name":"Tab two","IfUpdatedAt":"{{token}}"}"""))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("application/problem+json", conflict.Content.Headers.ContentType?.MediaType);
            Assert.Equal(
                """{"title":"Conflict","status":409,"detail":"Changed elsewhere. Reload to see the latest."}""",
                await conflict.Content.ReadAsStringAsync(Ct));
        }

        using (var conflict = await SendRawAsync(host, "POST", $"/Bookmarks/{id}", AliceId, $$"""{"StartTicks":0,"Name":"Tab two","IfUpdatedAt":"{{token}}"}"""))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.Equal("Tab one", host.Services.GetRequiredService<IBookmarkStore>().Get(Guid.Parse(id!))!.Name);

        // The current token in a JavaScript Date's form (milliseconds only) is accepted.
        var javaScript = DateTime.Parse(next, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        (status, var third) = await SendAsync(host, "POST", $"/Bookmarks/{id}", AliceId, $$"""{"StartTicks":0,"Name":"Tab one","Note":"later","IfUpdatedAt":"{{javaScript}}"}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("later", third.GetProperty("Note").GetString());
    }

    [Fact]
    public async Task ANameOver100Characters_Is400ProblemDetails_WithTheRule()
    {
        await using var host = await StartAsync();
        var (_, created) = await SendAsync(host, "POST", "/Bookmarks", AliceId, $$"""{"ItemId":"{{MovieId}}","StartTicks":600000000}""");
        var id = created.GetProperty("Id").GetString();
        var tooLong = new string('x', 101);

        foreach (var (path, body) in new[]
        {
            ("/Bookmarks", $$"""{"ItemId":"{{MovieId}}","StartTicks":0,"Name":"{{tooLong}}"}"""),
            ($"/Bookmarks/{id}", $$"""{"StartTicks":0,"Name":"{{tooLong}}"}"""),
            ($"/Bookmarks/{id}/Name", $$"""{"Name":"{{tooLong}}"}""")
        })
        {
            using var response = await SendRawAsync(host, "POST", path, AliceId, body);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, path);
            Assert.Equal(
                """{"title":"Bad Request","status":400,"detail":"Names can be at most 100 characters long."}""",
                await response.Content.ReadAsStringAsync(Ct));
        }

        Assert.Single(host.Services.GetRequiredService<IBookmarkStore>().GetAll());
    }

    [Fact]
    public async Task AnotherUsersBookmark_Is404_ForTheRenameToo()
    {
        await using var host = await StartAsync();
        var (_, created) = await SendAsync(host, "POST", "/Bookmarks", AliceId, $$"""{"ItemId":"{{MovieId}}","StartTicks":600000000,"Name":"Bank job"}""");
        var id = created.GetProperty("Id").GetString();

        using var response = await SendRawAsync(host, "POST", $"/Bookmarks/{id}/Name", BobId, """{"Name":"Mine"}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Bank job", host.Services.GetRequiredService<IBookmarkStore>().Get(Guid.Parse(id!))!.Name);
    }

    [Fact]
    public void EveryPost_ReadsAtMost64KB()
    {
        // The kit's abuse limits: [RequestSizeLimit] on every POST; the kit's filter turns Kestrel's 413 into
        // ProblemDetails (Kit.Tests' PluginExceptionFilterTests; the test server enforces no limit, so
        // bookmarks-api-probe.mjs sends a larger body to a sandbox).
        var posts = typeof(BookmarksController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpPostAttribute>().Any())
            .ToList();

        Assert.Equal(
            ["CancelClip", "CreateBookmark", "DeleteBookmarks", "RenameBookmark", "StartClip", "UpdateBookmark", "UpdateBookmarkPreferences"],
            posts.Select(m => m.Name).Order(StringComparer.Ordinal));
        Assert.All(posts, m => Assert.Equal(64 * 1024, ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata?)m.GetCustomAttribute<RequestSizeLimitAttribute>())?.MaxRequestBodySize));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _data.Dispose();
        return ValueTask.CompletedTask;
    }

    private static Task<HttpResponseMessage> SendRawAsync(WebHostHarness host, string method, string path, Guid userId, string? json = null)
        => host.SendAsync(new HttpMethod(method), path, request =>
        {
            request.Headers.Add(JellyfinAuth.CallerHeader, "user");
            request.Headers.Add(JellyfinAuth.UserIdHeader, userId.ToString("N"));
            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
        });

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(WebHostHarness host, string method, string path, Guid userId, string? json = null)
    {
        using var response = await SendRawAsync(host, method, path, userId, json);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return (response.StatusCode, document.RootElement.Clone());
    }

    private Task<WebHostHarness> StartAsync()
    {
        // The plugin's own registrations for everything this touches; Jellyfin's services are mocks, ffmpeg is fake.
        var applicationHost = new Mock<IServerApplicationHost>();
        applicationHost.SetupGet(h => h.ApplicationVersion).Returns(JellyfinLine.Server);
        applicationHost.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, applicationHost.Object);
        Type[] own =
        [
            typeof(KitOptions), typeof(KitAbiStatus), typeof(KitPaths), typeof(KitPageHosts), typeof(ClientHealthRegistry), typeof(KitDiagnostics),
            typeof(IKitDiagnostics), typeof(IKitDiagnosticsSource), typeof(BookmarkPaths), typeof(IBookmarkStore), typeof(BackgroundWriteGate),
            typeof(ClipExtractionService), typeof(IClipExtractionService), typeof(ClipFileOps), typeof(ClipStateCache), typeof(PartialFileJanitor), typeof(IBookmarkManager), typeof(IBookmarkQuery), typeof(IBookmarkPreferences),
            typeof(ClipStorageGuard), typeof(IDriveSpaceProbe),

            // The kit's library-change monitor and verify sweep, hosted services like the worker, with the demo consumers and
            // records.
            .. TestData.LibraryServiceTypes
        ];

        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_data.Path);
        var loggers = new Mock<ILoggerFactory>();
        loggers.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(NullLogger.Instance);
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUserById(AliceId)).Returns(_alice);
        users.Setup(m => m.GetUserById(BobId)).Returns(_bob);
        var library = new Mock<ILibraryManager>();
        library.Setup(m => m.GetItemById<Video>(MovieId, It.IsAny<User?>()))
            .Returns(new MediaBrowser.Controller.Entities.Movies.Movie { Id = MovieId, Name = "Heat", ProductionYear = 1995, RunTimeTicks = 3000 * TestData.OneSecond, Path = @"C:\media\Heat (1995).mkv" });

        return new WebHostHarness()
            .WithServices(services =>
            {
                foreach (var descriptor in registered.Where(d => own.Contains(d.ServiceType)
                    || (d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null)))
                {
                    services.Add(descriptor);
                }

                services.AddSingleton(applicationHost.Object);
                services.AddSingleton(paths.Object);
                services.AddSingleton(loggers.Object);
                services.AddSingleton(users.Object);
                services.AddSingleton(library.Object);
                services.AddSingleton(TestData.ConfigurationProvider(new PluginConfiguration()));
                services.AddSingleton<IClipPlanner>(new FakeClipPlanner(_data));
                services.AddSingleton<IFfmpegRunner>(new FakeFfmpegRunner());
                services.AddSingleton(Mock.Of<IEmbeddedScript>());
                services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o =>
                {
                    var jellyfin = JsonDefaults.PascalCaseOptions;
                    o.JsonSerializerOptions.DefaultIgnoreCondition = jellyfin.DefaultIgnoreCondition;
                    o.JsonSerializerOptions.PropertyNamingPolicy = jellyfin.PropertyNamingPolicy;
                    o.JsonSerializerOptions.Converters.Clear();
                    foreach (var converter in jellyfin.Converters)
                    {
                        o.JsonSerializerOptions.Converters.Add(converter);
                    }
                });
            })
            .WithMvc(typeof(BookmarksController))
            .StartAsync();
    }
}
