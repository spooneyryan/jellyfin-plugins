using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Bookmarks.Api;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
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
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Api;

/// <summary>
/// The free-space guard on the wire (503 for low space or a clip folder that is unavailable), with the plugin's own
/// registrations behind Jellyfin's web pipeline and a fake probe: <c>POST
/// /Bookmarks/{id}/Clip</c> answers 503 with ProblemDetails, the drive and the numbers for an admin (Jellyfin's
/// <c>RequiresElevation</c> policy), the user's wording otherwise, and queues nothing; with room it answers 202 and the
/// job keeps its estimate; and <c>GET /Bookmarks/Admin/Diagnostics</c> has the <c>ClipStorage</c> entry.
/// </summary>
public sealed class ClipStorageApiTests : IAsyncLifetime
{
    private static readonly Guid AliceId = new("9fa5c677-8cf7-4a94-bff0-c82a60581ccf");

    private readonly TempDirectory _data = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly MediaBrowser.Controller.Entities.Movies.Movie _movie = TestData.CreateMovie();
    private readonly FakeDriveSpaceProbe _probe = new();
    private readonly PluginConfiguration _configuration = new();

    public ClipStorageApiTests()
    {
        _alice.Id = AliceId;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StoreFile => Path.Combine(_data.Path, "bookmarks", "bookmarks.json");

    [Theory]
    [InlineData("user", "Not enough free space on the server for this clip. Ask your server admin.")]
    [InlineData("admin", "Not enough free space for this clip: Q:\\ has 0.8 GB free; this clip needs about 0.3 GB, and 2 GB must stay free (\"Keep free on the clip drive\" in the Bookmarks settings).")]
    public async Task AClipThatWouldLeaveTooLittle_Is503_InTheCallersWords_AndNothingIsQueued(string caller, string message)
    {
        await using var host = await StartAsync();
        var bookmark = await AddAsync(host);
        _probe.Available(800L * 1024 * 1024, volume: @"Q:\");
        var before = await File.ReadAllBytesAsync(StoreFile, Ct);

        using var response = await SendAsync(host, HttpMethod.Post, $"/Bookmarks/{bookmark.Id}/Clip", caller, """{"Mode":"Accurate"}""");
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable, $"{response.StatusCode} {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(503, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(message, problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal(before, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.Null(host.Services.GetRequiredService<IBookmarkStore>().Get(bookmark.Id)!.Clip);
    }

    [Fact]
    public async Task AClipFolderWhoseDriveIsntThere_Is503()
    {
        await using var host = await StartAsync();
        var bookmark = await AddAsync(host);
        _probe.Unavailable();

        using var response = await SendAsync(host, HttpMethod.Post, $"/Bookmarks/{bookmark.Id}/Clip", "user", "{}");
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("The clip folder isn't available right now. Ask your server admin.", problem.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task AClipThatFits_Is202_AndItsJobKeepsTheEstimate()
    {
        await using var host = await StartAsync();
        var bookmark = await AddAsync(host);

        using var response = await SendAsync(host, HttpMethod.Post, $"/Bookmarks/{bookmark.Id}/Clip", "user", """{"Mode":"Fast"}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(300_000_000, host.Services.GetRequiredService<IBookmarkStore>().Get(bookmark.Id)!.Clip!.EstimatedBytes);
    }

    [Fact]
    public async Task Diagnostics_HasClipStorage_ForTheCurrentClipFolder()
    {
        _probe.Available(800L * 1024 * 1024, total: 1L << 40, volume: @"Q:\");
        await using var host = await StartAsync();

        using var response = await SendAsync(host, HttpMethod.Get, "/Bookmarks/Admin/Diagnostics", "admin", null);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var storage = document.RootElement.GetProperty("ClipStorage");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Path.Combine(_data.Path, "bookmarks", "clips"), storage.GetProperty("Root").GetString());
        Assert.False(storage.GetProperty("Exists").GetBoolean());
        Assert.Equal("Ok", storage.GetProperty("FreeSpace").GetString());
        Assert.Equal(800L * 1024 * 1024, storage.GetProperty("FreeBytes").GetInt64());
        Assert.Equal(1L << 40, storage.GetProperty("TotalBytes").GetInt64());
        Assert.Equal(2048L * 1024 * 1024, storage.GetProperty("MinFreeBytes").GetInt64());
        Assert.True(storage.GetProperty("LowFreeSpace").GetBoolean());
        Assert.Equal(0, storage.GetProperty("PendingFileDeletes").GetInt32());
        var warning = Assert.Single(storage.GetProperty("Warnings").EnumerateArray());
        Assert.Equal("LowFreeSpace", warning.GetProperty("Code").GetString());
        Assert.Equal("Q:\\ has 0.8 GB free. Clips are refused when fewer than 2 GB would remain.", warning.GetProperty("Message").GetString());
        Assert.StartsWith("The folder doesn't exist yet; the first clip creates it. Free space for clips: Q:\\ has 0.8 GB free of 1024 GB.", storage.GetProperty("Message").GetString(), StringComparison.Ordinal);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _data.Dispose();
        return ValueTask.CompletedTask;
    }

    private static Task<HttpResponseMessage> SendAsync(WebHostHarness host, HttpMethod method, string path, string caller, string? json)
        => host.SendAsync(method, path, request =>
        {
            request.Headers.Add(JellyfinAuth.CallerHeader, caller);
            request.Headers.Add(JellyfinAuth.UserIdHeader, AliceId.ToString("N"));
            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
        });

    private async Task<Bookmark> AddAsync(WebHostHarness host)
    {
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var bookmark = TestData.CreateBookmark(AliceId, 60, 90, _movie.Id);
        await store.AddAsync(bookmark, Ct);
        return bookmark;
    }

    private Task<WebHostHarness> StartAsync()
    {
        // The plugin's own registrations for the controllers, the manager, the worker and the guard; Jellyfin's services are
        // mocks, the planner and ffmpeg fakes, and the probe a fake in place of the real one.
        var applicationHost = new Mock<IServerApplicationHost>();
        applicationHost.SetupGet(h => h.ApplicationVersion).Returns(new Version(10, 11, 5));
        applicationHost.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, applicationHost.Object);
        Type[] own =
        [
            typeof(KitOptions), typeof(KitAbiStatus), typeof(KitPaths), typeof(KitPageHosts), typeof(ClientHealthRegistry), typeof(KitDiagnostics),
            typeof(IKitDiagnostics), typeof(IKitDiagnosticsSource), typeof(BookmarkPaths), typeof(IBookmarkStore), typeof(BackgroundWriteGate),
            typeof(ClipExtractionService), typeof(IClipExtractionService), typeof(ClipFileOps), typeof(ClipStateCache), typeof(PartialFileJanitor), typeof(IBookmarkManager), typeof(IBookmarkQuery), typeof(IBookmarkPreferences),
            typeof(ClipStorageGuard),

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
        var library = new Mock<ILibraryManager>();
        library.Setup(m => m.GetItemById<Video>(_movie.Id, It.IsAny<User?>())).Returns(_movie);

        return new WebHostHarness()
            .WithServices(services =>
            {
                foreach (var descriptor in registered.Where(d => own.Contains(d.ServiceType)
                    || (d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null)))
                {
                    services.Add(descriptor);
                }

                services.AddAuthorizationCore(options => options.AddPolicy(
                    Policies.Download,
                    policy => policy.AddAuthenticationSchemes(JellyfinAuth.Scheme).RequireAuthenticatedUser()));
                services.AddSingleton(applicationHost.Object);
                services.AddSingleton(paths.Object);
                services.AddSingleton(loggers.Object);
                services.AddSingleton(users.Object);
                services.AddSingleton(library.Object);
                services.AddSingleton(TestData.ConfigurationProvider(_configuration));
                services.AddSingleton<IDriveSpaceProbe>(_probe);
                services.AddSingleton<IClipPlanner>(new FakeClipPlanner(_data) { EstimatedBytes = 300_000_000 });
                services.AddSingleton<IFfmpegRunner>(new FakeFfmpegRunner());
                services.AddSingleton(Mock.Of<IEmbeddedScript>());
                services.AddSingleton(Mock.Of<Jellyfin.Plugin.Bookmarks.Services.IBookmarkRecordsAdmin>());
                services.AddSingleton(Mock.Of<Jellyfin.Plugin.Bookmarks.Import.IBookmarkJeImport>());
                services.AddSingleton(Mock.Of<Jellyfin.Plugin.Bookmarks.Services.IBookmarkAdminTools>());
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
            .WithMvc(typeof(BookmarksController), typeof(BookmarksAdminController))
            .StartAsync();
    }
}
