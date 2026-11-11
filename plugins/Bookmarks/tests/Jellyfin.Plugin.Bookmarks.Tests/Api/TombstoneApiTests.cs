using System;
using System.Collections.Generic;
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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Api;

/// <summary>
/// Clip-only tombstones on the wire: the plugin's own registrations behind Jellyfin's web pipeline, with the clip worker
/// hosted. Every route of <see cref="BookmarksController"/> that takes a bookmark id answers a clip-only tombstone as
/// <see cref="TombstoneRoutes"/> says (it must list every such route, so a new one is decided here first, as 1.1.0's
/// rename was); a bookmark deleted without its clip becomes one; <c>DELETE /Bookmarks/{id}/Clip</c> answers 200 with the
/// bookmark or 204 when a tombstone went; another user's tombstone is 404 everywhere.
/// </summary>
public sealed class TombstoneApiTests : IAsyncLifetime
{
    private static readonly Guid AliceId = new("9fa5c677-8cf7-4a94-bff0-c82a60581ccf");
    private static readonly Guid BobId = new("ff71b896-f205-45ff-936a-c2393f6091f6");
    private static readonly Guid MovieId = new("ba7ef8f5-0757-f5f6-0cae-61b9b7fd19f5");

    private readonly TempDirectory _data = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly User _bob = TestData.CreateUser("bob");

    public TombstoneApiTests()
    {
        _alice.Id = AliceId;
        _bob.Id = BobId;
    }

    /// <summary>
    /// Gets every route of <see cref="BookmarksController"/> that takes a bookmark id, with its status for the caller's own
    /// clip-only tombstone: bookmark operations are 404 (rows 1, 2 and 5); the clip file is served and the clip
    /// deleted with the record (rows 3 and 4).
    /// </summary>
    public static TheoryData<string, string, int> TombstoneRoutes => new()
    {
        { "GET", "{bookmarkId}", 404 },
        { "POST", "{bookmarkId}", 404 },
        { "POST", "{bookmarkId}/Name", 404 },
        { "DELETE", "{bookmarkId}", 404 },
        { "POST", "{bookmarkId}/Clip", 404 },
        { "POST", "{bookmarkId}/Clip/Cancel", 404 },
        { "DELETE", "{bookmarkId}/Clip", 204 },
        { "GET", "{bookmarkId}/Clip/File", 200 },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StoreFile => Path.Combine(_data.Path, "bookmarks", "bookmarks.json");

    [Fact]
    public void TombstoneRoutes_ListEveryRouteThatTakesABookmarkId()
    {
        var listed = TombstoneRoutes.Select(row => $"{row.Data.Item1} {row.Data.Item2}").Order(StringComparer.Ordinal);
        var actual = typeof(BookmarksController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>())
            .Where(a => a.Template?.Contains("{bookmarkId", StringComparison.Ordinal) == true)
            .SelectMany(a => a.HttpMethods.Select(verb => $"{verb} {a.Template!.Replace(":guid", string.Empty, StringComparison.Ordinal)}"))
            .Order(StringComparer.Ordinal);

        Assert.Equal(actual, listed);
    }

    [Theory]
    [MemberData(nameof(TombstoneRoutes))]
    public async Task EveryBookmarkRoute_AnswersATombstone_AsSection64Says(string method, string route, int expected)
    {
        await using var host = await StartAsync();
        var (tombstone, file) = await AddTombstoneAsync(host);
        var storeBefore = await File.ReadAllBytesAsync(StoreFile, Ct);

        using var response = await SendAsync(host, method, "/Bookmarks/" + route.Replace("{bookmarkId}", tombstone.Id.ToString(), StringComparison.Ordinal), AliceId, BodyFor(method, route));
        var body = await response.Content.ReadAsByteArrayAsync(Ct);

        Assert.True(expected == (int)response.StatusCode, $"{method} {route}: {response.StatusCode} {Encoding.UTF8.GetString(body)}");
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        switch (expected)
        {
            case 404:
                // Not found, as ProblemDetails, and nothing changed: the record, the store file and the clip file.
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                Assert.Equal(tombstone, store.Get(tombstone.Id));
                Assert.Equal(storeBefore, await File.ReadAllBytesAsync(StoreFile, Ct));
                Assert.True(File.Exists(file));
                break;
            case 200:
                Assert.Equal(await File.ReadAllBytesAsync(file, Ct), body);
                Assert.Equal(tombstone, store.Get(tombstone.Id));
                break;
            default:
                Assert.Empty(body);
                Assert.Null(store.Get(tombstone.Id));
                Assert.False(File.Exists(file));
                break;
        }
    }

    [Fact]
    public async Task DeletingATombstone_WithItsClip_IsAlso404_AndKeepsTheFile()
    {
        await using var host = await StartAsync();
        var (tombstone, file) = await AddTombstoneAsync(host);

        using var response = await SendAsync(host, "DELETE", $"/Bookmarks/{tombstone.Id}?deleteClip=true", AliceId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(File.Exists(file));
        Assert.NotNull(host.Services.GetRequiredService<IBookmarkStore>().Get(tombstone.Id));
    }

    [Fact]
    public async Task ABookmarkDeletedWithoutItsClip_IsGoneFromTheApi_ButItsClipFileStaysReachable()
    {
        await using var host = await StartAsync();
        var created = await PostJsonAsync(host, "/Bookmarks", $$"""{"ItemId":"{{MovieId}}","StartTicks":600000000,"EndTicks":900000000,"Note":"Bank job"}""");
        var id = Guid.Parse(created.GetProperty("Id").GetString()!);
        var file = await GiveItAClipAsync(host, id, "Heat (1995) - Bank job [00.01.00-00.01.30].mkv");

        using (var deleted = await SendAsync(host, "DELETE", $"/Bookmarks/{id}?deleteClip=false", AliceId))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        // Rows 1 and 2: gone from the lists and by id.
        Assert.Equal(0, (await GetJsonAsync(host, "/Bookmarks")).GetArrayLength());
        Assert.Equal(0, (await GetJsonAsync(host, $"/Bookmarks?itemId={MovieId}")).GetArrayLength());
        using (var read = await SendAsync(host, "GET", $"/Bookmarks/{id}", AliceId))
        {
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        }

        // Row 3: its clip file still downloads, as an attachment and streamed.
        using (var download = await SendAsync(host, "GET", $"/Bookmarks/{id}/Clip/File", AliceId))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("clip", await download.Content.ReadAsStringAsync(Ct));
        }

        using (var stream = await SendAsync(host, "GET", $"/Bookmarks/{id}/Clip/File?download=false", AliceId))
        {
            Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
            Assert.Null(stream.Content.Headers.ContentDisposition);
        }

        // The stored tombstone: the clip reduced to its file.
        var stored = host.Services.GetRequiredService<IBookmarkStore>().Get(id)!;
        Assert.NotNull(stored.DeletedUtc);
        Assert.Equal(BookmarkClipStatus.Completed, stored.Clip?.Status);
        Assert.Equal(BookmarkClipMode.Fast, stored.Clip?.Mode);
        Assert.Equal("Bank job", stored.Note);

        // Row 4: deleting its clip removes file and record (204), after which it is gone for good.
        using (var deleteClip = await SendAsync(host, "DELETE", $"/Bookmarks/{id}/Clip", AliceId))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleteClip.StatusCode);
        }

        Assert.False(File.Exists(file));
        Assert.Null(host.Services.GetRequiredService<IBookmarkStore>().Get(id));
        using var again = await SendAsync(host, "DELETE", $"/Bookmarks/{id}/Clip", AliceId);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        using var gone = await SendAsync(host, "GET", $"/Bookmarks/{id}/Clip/File", AliceId);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task DeletingTheClipOfALiveBookmark_Is200_WithTheBookmarkLeft()
    {
        await using var host = await StartAsync();
        var created = await PostJsonAsync(host, "/Bookmarks", $$"""{"ItemId":"{{MovieId}}","StartTicks":600000000,"EndTicks":900000000}""");
        var id = Guid.Parse(created.GetProperty("Id").GetString()!);
        var file = await GiveItAClipAsync(host, id, "Heat (1995) [00.01.00-00.01.30].mp4");

        using var response = await SendAsync(host, "DELETE", $"/Bookmarks/{id}/Clip", AliceId);
        var json = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var bookmark = JsonDocument.Parse(json);
        Assert.Equal(id, Guid.Parse(bookmark.RootElement.GetProperty("Id").GetString()!));
        Assert.False(bookmark.RootElement.TryGetProperty("Clip", out _));
        Assert.False(File.Exists(file));
        Assert.Equal(1, (await GetJsonAsync(host, "/Bookmarks")).GetArrayLength());
    }

    [Fact]
    public async Task AnotherUsersTombstone_Is404_ForItsClipToo()
    {
        await using var host = await StartAsync();
        var (tombstone, file) = await AddTombstoneAsync(host);

        foreach (var (method, path) in new[] { ("GET", $"/Bookmarks/{tombstone.Id}/Clip/File"), ("DELETE", $"/Bookmarks/{tombstone.Id}/Clip"), ("GET", $"/Bookmarks/{tombstone.Id}") })
        {
            using var response = await SendAsync(host, method, path, BobId);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path}: {response.StatusCode}");
        }

        Assert.True(File.Exists(file));
        Assert.Equal(tombstone, host.Services.GetRequiredService<IBookmarkStore>().Get(tombstone.Id));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _data.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string? BodyFor(string method, string route) => (method, route) switch
    {
        ("POST", "{bookmarkId}") => """{"StartTicks":0,"EndTicks":10000000}""",
        ("POST", "{bookmarkId}/Name") => """{"Name":"Vault"}""",
        ("POST", "{bookmarkId}/Clip") => """{"Mode":"Fast"}""",
        _ => null
    };

    private static Task<HttpResponseMessage> SendAsync(WebHostHarness host, string method, string path, Guid userId, string? json = null)
        => host.SendAsync(new HttpMethod(method), path, request =>
        {
            request.Headers.Add(JellyfinAuth.CallerHeader, "user");
            request.Headers.Add(JellyfinAuth.UserIdHeader, userId.ToString("N"));
            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
        });

    private static async Task<JsonElement> GetJsonAsync(WebHostHarness host, string path)
    {
        using var response = await SendAsync(host, "GET", path, AliceId);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> PostJsonAsync(WebHostHarness host, string path, string json)
    {
        using var response = await SendAsync(host, "POST", path, AliceId, json);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    // A finished clip on the bookmark, as the worker records one, with its file in the clip folder, which the worker records
    // in ClipRoots before it writes there.
    private async Task<string> GiveItAClipAsync(WebHostHarness host, Guid id, string fileName)
    {
        var folder = Path.Combine(_data.Path, "bookmarks", "clips", "alice");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, fileName);
        await File.WriteAllTextAsync(file, "clip", Ct);
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        await store.AddClipRootAsync(Path.Combine(_data.Path, "bookmarks", "clips"), Ct);
        await store.UpdateAsync(
            id,
            b => b with
            {
                Clip = new ClipInfo
                {
                    Status = BookmarkClipStatus.Completed,
                    Mode = ClipFileNaming.GetMode(fileName),
                    StartTicks = b.StartTicks,
                    EndTicks = b.EndTicks ?? b.StartTicks,
                    RequestedAt = DateTime.UtcNow,
                    CompletedAt = DateTime.UtcNow,
                    FilePath = file,
                    FileSize = 4,
                    FileStartTicks = b.StartTicks,
                    FileEndTicks = b.EndTicks
                }
            },
            Ct);
        return file;
    }

    private async Task<(Bookmark Tombstone, string File)> AddTombstoneAsync(WebHostHarness host)
    {
        var bookmark = TestData.CreateBookmark(AliceId, 60, 90, MovieId) with { Name = "Kept clip" };
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        await store.AddAsync(bookmark, Ct);
        var file = await GiveItAClipAsync(host, bookmark.Id, "Heat (1995) - Kept clip [00.01.00-00.01.30].mkv");
        var tombstone = await store.UpdateAsync(bookmark.Id, b => BookmarkRecordRules.ToTombstone(b, DateTime.UtcNow)!, Ct);
        return (tombstone!, file);
    }

    private Task<WebHostHarness> StartAsync()
    {
        // The plugin's own registrations for everything this touches; Jellyfin's services are mocks, ffmpeg is fake.
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

                // Jellyfin's download policy (the clip file route's) needs Jellyfin.Api; any signed-in user stands in here.
                services.AddAuthorizationCore(options => options.AddPolicy(
                    Policies.Download,
                    policy => policy.AddAuthenticationSchemes(JellyfinAuth.Scheme).RequireAuthenticatedUser()));
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
