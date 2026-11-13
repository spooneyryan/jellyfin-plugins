using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
/// A clip file in use on the real file system, on the wire: the plugin's own registrations behind
/// Jellyfin's web pipeline, with a clip file held open by <c>new FileStream(path, Open, Read, FileShare.ReadWrite)</c> as a
/// download or a preview holds it. <c>DELETE /Bookmarks/{id}/Clip</c> and <c>DELETE /Bookmarks/{id}?deleteClip=true</c> answer
/// 409 with ProblemDetails, and the record and the file are unchanged (the store file byte for byte); once the stream is
/// closed, both succeed; a bulk delete with the clips reports it <c>InUse</c> and deletes the others. A record whose
/// clip file lies outside the clip folders is 404 for its file and its delete, and nothing is touched. Windows only: other systems delete an open file.
/// </summary>
public sealed class ClipFileInUseApiTests : IAsyncLifetime
{
    private static readonly Guid AliceId = new("9fa5c677-8cf7-4a94-bff0-c82a60581ccf");
    private static readonly Guid MovieId = new("ba7ef8f5-0757-f5f6-0cae-61b9b7fd19f5");

    private readonly TempDirectory _data = new();
    private readonly User _alice = TestData.CreateUser("alice");

    public ClipFileInUseApiTests()
    {
        _alice.Id = AliceId;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StoreFile => Path.Combine(_data.Path, "bookmarks", "bookmarks.json");

    private string ClipFolder => Path.Combine(_data.Path, "bookmarks", "clips");

    public static TheoryData<string, string, int> DeleteRoutes => new()
    {
        { "live", "{id}/Clip", 200 },
        { "live", "{id}?deleteClip=true", 204 },
        { "tombstone", "{id}/Clip", 204 },
    };

    [Theory]
    [MemberData(nameof(DeleteRoutes))]
    public async Task ADeleteOfAClipFileHeldOpen_Is409_WithNothingChanged_AndSucceedsOnceItIsClosed(string record, string route, int afterwards)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows refuses to delete a file open without delete sharing.");
        await using var host = await StartAsync();
        var (bookmark, file) = await AddAsync(host, record == "tombstone");
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var path = "/Bookmarks/" + route.Replace("{id}", bookmark.Id.ToString(), StringComparison.Ordinal);
        var storeBefore = await File.ReadAllBytesAsync(StoreFile, Ct);

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            using var inUse = await SendAsync(host, HttpMethod.Delete, path);
            var body = await inUse.Content.ReadAsStringAsync(Ct);

            Assert.True(inUse.StatusCode == HttpStatusCode.Conflict, $"{path}: {inUse.StatusCode} {body}");
            Assert.Equal("application/problem+json", inUse.Content.Headers.ContentType?.MediaType);
            using var problem = JsonDocument.Parse(body);
            Assert.Equal(409, problem.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(ClipFileOps.InUseMessage, problem.RootElement.GetProperty("detail").GetString());
            Assert.Equal(bookmark, store.Get(bookmark.Id));
            Assert.Equal(storeBefore, await File.ReadAllBytesAsync(StoreFile, Ct));
            Assert.True(File.Exists(file));

            // The file can still be downloaded meanwhile (another reader).
            using var download = await SendAsync(host, HttpMethod.Get, $"/Bookmarks/{bookmark.Id}/Clip/File");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        }

        using var released = await SendAsync(host, HttpMethod.Delete, path);

        Assert.Equal(afterwards, (int)released.StatusCode);
        Assert.False(File.Exists(file));
        if (afterwards == 204)
        {
            Assert.Null(store.Get(bookmark.Id));
        }
        else
        {
            Assert.Null(store.Get(bookmark.Id)?.Clip);
        }
    }

    [Fact]
    public async Task ABulkDeleteWithTheClips_LeavesTheBookmarkWhoseFileIsHeldOpen_InUse_AndDeletesTheOthers()
    {
        // A bulk delete counts it in InUse, with a real file held open; nothing of it changes until it is closed.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows refuses to delete a file open without delete sharing.");
        await using var host = await StartAsync();
        var (held, heldFile) = await AddAsync(host, tombstone: false, fileName: "Heat (1995) - Held [00.01.00-00.01.30].mp4");
        var (other, otherFile) = await AddAsync(host, tombstone: false, fileName: "Heat (1995) - Other [00.01.00-00.01.30].mp4");
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var body = "{\"Ids\":[\"" + held.Id + "\",\"" + other.Id + "\"],\"DeleteClips\":true}";

        using (new FileStream(heldFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            using var response = await SendAsync(host, HttpMethod.Post, "/Bookmarks/BulkDelete", body);
            var text = await response.Content.ReadAsStringAsync(Ct);

            Assert.True(response.StatusCode == HttpStatusCode.OK, text);
            using var result = JsonDocument.Parse(text);
            Assert.Equal([other.Id], result.RootElement.GetProperty("Deleted").EnumerateArray().Select(e => Guid.Parse(e.GetString()!)));
            Assert.Equal([held.Id], result.RootElement.GetProperty("InUse").EnumerateArray().Select(e => Guid.Parse(e.GetString()!)));
            Assert.Equal(held, store.Get(held.Id));
            Assert.True(File.Exists(heldFile));
            Assert.Null(store.Get(other.Id));
            Assert.False(File.Exists(otherFile));
        }

        using var released = await SendAsync(host, HttpMethod.Post, "/Bookmarks/BulkDelete", body);
        using var after = JsonDocument.Parse(await released.Content.ReadAsStringAsync(Ct));
        Assert.Equal([held.Id], after.RootElement.GetProperty("Deleted").EnumerateArray().Select(e => Guid.Parse(e.GetString()!)));
        Assert.Equal([other.Id], after.RootElement.GetProperty("NotFound").EnumerateArray().Select(e => Guid.Parse(e.GetString()!)));
        Assert.False(File.Exists(heldFile));
        Assert.Null(store.Get(held.Id));
    }

    [Fact]
    public async Task AClipFileOutsideTheClipFolders_Is404_ForItsFileAndItsDelete_AndIsNeverTouched()
    {
        await using var host = await StartAsync();
        var outside = Path.Combine(_data.Path, "library", "Heat (1995) - Bank job [00.01.00-00.01.30].mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        await File.WriteAllTextAsync(outside, "not a clip of ours", Ct);
        var (bookmark, _) = await AddAsync(host, tombstone: false, filePath: outside);
        var storeBefore = await File.ReadAllBytesAsync(StoreFile, Ct);

        using var file = await SendAsync(host, HttpMethod.Get, $"/Bookmarks/{bookmark.Id}/Clip/File");
        using var deleteClip = await SendAsync(host, HttpMethod.Delete, $"/Bookmarks/{bookmark.Id}/Clip");
        using var deleteWithClip = await SendAsync(host, HttpMethod.Delete, $"/Bookmarks/{bookmark.Id}?deleteClip=true");

        Assert.Equal(HttpStatusCode.NotFound, file.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deleteClip.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deleteWithClip.StatusCode);
        using var problem = JsonDocument.Parse(await deleteClip.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ClipFileOps.NotInClipFoldersMessage, problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal("not a clip of ours", await File.ReadAllTextAsync(outside, Ct));
        Assert.Equal(storeBefore, await File.ReadAllBytesAsync(StoreFile, Ct));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _data.Dispose();
        return ValueTask.CompletedTask;
    }

    private static Task<HttpResponseMessage> SendAsync(WebHostHarness host, HttpMethod method, string path, string? json = null)
        => host.SendAsync(method, path, request =>
        {
            request.Headers.Add(JellyfinAuth.CallerHeader, "user");
            request.Headers.Add(JellyfinAuth.UserIdHeader, AliceId.ToString("N"));
            if (json is not null)
            {
                request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            }
        });

    // Alice's bookmark with a finished clip, as the worker records one: its file in the clip folder, which the worker records
    // in ClipRoots before it writes there, or at the given path.
    private async Task<(Bookmark Bookmark, string File)> AddAsync(WebHostHarness host, bool tombstone, string? filePath = null, string fileName = "Heat (1995) - Bank job [00.01.00-00.01.30].mp4")
    {
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        await store.AddClipRootAsync(ClipFolder, Ct);
        var file = filePath ?? Path.Combine(ClipFolder, "alice", fileName);
        if (filePath is null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "clip", Ct);
        }

        var bookmark = TestData.CreateBookmark(AliceId, 60, 90, MovieId) with
        {
            Name = "Bank job",
            Clip = new ClipInfo
            {
                Status = BookmarkClipStatus.Completed,
                Mode = BookmarkClipMode.Accurate,
                StartTicks = 60 * TestData.OneSecond,
                EndTicks = 90 * TestData.OneSecond,
                RequestedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                FilePath = file,
                FileSize = 4,
                FileStartTicks = 60 * TestData.OneSecond,
                FileEndTicks = 90 * TestData.OneSecond
            }
        };
        await store.AddAsync(bookmark, Ct);
        if (tombstone)
        {
            await store.UpdateAsync(bookmark.Id, b => BookmarkRecordRules.ToTombstone(b, DateTime.UtcNow)!, Ct);
        }

        return (store.Get(bookmark.Id)!, file);
    }

    private Task<WebHostHarness> StartAsync()
    {
        // The plugin's own registrations for everything this touches (ClipFileOps with the real file system and the real
        // 200 ms between attempts); Jellyfin's services are mocks, ffmpeg is fake.
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
