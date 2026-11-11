using System;
using System.Collections.Generic;
using System.Globalization;
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
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Import;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.Import;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Interop;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Api;

/// <summary>
/// <c>POST /Bookmarks/Admin/Import/JellyfinEnhanced</c> behind Jellyfin's web pipeline, with the
/// registrator's own importer, the kit's reader and Diagnostics: admins only, a dry run unless <c>dryRun=false</c>, the body
/// optional, 404 ProblemDetails without JE data, 400 for a bad user list, the report as Jellyfin's JSON (enums as text), and
/// Diagnostics' <c>JeImport</c> entry, the settings page's gate for the switch-off checklist.
/// </summary>
public sealed class JeImportApiTests : IAsyncLifetime
{
    private readonly TempDirectory _data = new();
    private readonly FakeLibrary _library = new();
    private readonly User _admin = TestData.CreateUser("admin");
    private readonly Mock<IUserManager> _users = new();

    public JeImportApiTests()
    {
        _admin.Id = JeImportFixture.AdminId;
        _users.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => id == _admin.Id ? _admin : null);
        _users.SetupGet(m => m.Users).Returns([_admin]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string JeFolder => Path.Combine(_data.Path, "config", JellyfinEnhancedFiles.ConfigurationName);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _data.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TheImport_IsForAdminsOnly()
    {
        await using var host = await StartAsync();
        using var user = await host.SendAsync(HttpMethod.Post, "/Bookmarks/Admin/Import/JellyfinEnhanced", r =>
        {
            r.Headers.Add(JellyfinAuth.CallerHeader, "user");
            r.Headers.Add(JellyfinAuth.UserIdHeader, Guid.NewGuid().ToString("N"));
        });
        Assert.Equal(HttpStatusCode.Forbidden, user.StatusCode);
        using var anonymous = await host.SendAsync(HttpMethod.Post, "/Bookmarks/Admin/Import/JellyfinEnhanced");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task WithoutJeData_It404s_AsProblemDetails_AndABadUserListIs400()
    {
        await using var host = await StartAsync();

        using (var none = await SendAsync(host, "/Bookmarks/Admin/Import/JellyfinEnhanced", null))
        {
            Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
            Assert.Equal("application/problem+json", none.Content.Headers.ContentType?.MediaType);
            using var problem = JsonDocument.Parse(await none.Content.ReadAsStringAsync(Ct));
            Assert.Equal(JellyfinEnhancedBookmarkImporter.NoDataMessage, problem.RootElement.GetProperty("detail").GetString());
        }

        var ids = string.Join(",", Enumerable.Range(0, 1001).Select(_ => "\"" + Guid.NewGuid().ToString("N") + "\""));
        using var tooMany = await SendAsync(host, "/Bookmarks/Admin/Import/JellyfinEnhanced", "{\"UserIds\":[" + ids + "]}");
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [Fact]
    public async Task ADryRunIsTheDefault_TheImportAnswersTheReport_AndDiagnosticsJeImportOpensTheGate()
    {
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        var folder = Path.Combine(JeFolder, _admin.Id.ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(
            Path.Combine(folder, "bookmark.json"),
            JsonSerializer.Serialize(new { Bookmarks = new Dictionary<string, object> { ["Bm_1"] = JeImportFixture.Entry(movie.Id, 60, "Bank job"), ["Bm_2"] = JeImportFixture.Entry(Guid.NewGuid(), 30, "Gone", tmdb: "999001") } }),
            Ct);
        await using var host = await StartAsync();
        var store = host.Services.GetRequiredService<IBookmarkStore>();

        var before = (await GetJsonAsync(host, "/Bookmarks/Admin/Diagnostics")).GetProperty("JeImport");
        Assert.False(before.GetProperty("ChecklistReady").GetBoolean());
        Assert.Equal(1, before.GetProperty("UsersWithBookmarks").GetInt32());
        Assert.Equal("admin", before.GetProperty("Pending")[0].GetProperty("UserName").GetString());

        // No query and no body: a dry run.
        var dry = await PostJsonAsync(host, "/Bookmarks/Admin/Import/JellyfinEnhanced", null);
        Assert.True(dry.GetProperty("DryRun").GetBoolean());
        Assert.Equal("je-bookmark/1", dry.GetProperty("Format").GetString());
        Assert.Equal(2, dry.GetProperty("Users")[0].GetProperty("Import").GetInt32());
        Assert.Empty(store.GetAll());

        var applied = await PostJsonAsync(host, "/Bookmarks/Admin/Import/JellyfinEnhanced?dryRun=false", "{\"UserIds\":[\"" + _admin.Id.ToString("D") + "\"]}", _admin.Id);
        Assert.False(applied.GetProperty("DryRun").GetBoolean());
        var row = applied.GetProperty("Users")[0];
        Assert.Equal((1, 1), (row.GetProperty("MatchedById").GetInt32(), row.GetProperty("Detached").GetInt32()));
        Assert.Contains(applied.GetProperty("Samples").EnumerateArray(), s => s.GetProperty("Outcome").GetString() == "Detached");
        Assert.True(applied.GetProperty("ChecklistReady").GetBoolean());
        Assert.Equal(8, applied.GetProperty("Checklist").GetArrayLength());
        Assert.Equal("ShortcutUsers", applied.GetProperty("Checklist")[7].GetProperty("Key").GetString());
        Assert.Equal(2, store.GetAll().Count);

        var after = (await GetJsonAsync(host, "/Bookmarks/Admin/Diagnostics")).GetProperty("JeImport");
        Assert.True(after.GetProperty("ChecklistReady").GetBoolean());
        Assert.Equal(0, after.GetProperty("Pending").GetArrayLength());
        Assert.Equal(2, after.GetProperty("Runs")[0].GetProperty("Imported").GetInt32());

        // The run names the admin who applied it.
        Assert.Equal(_admin.Id, store.ImportRuns["je:" + _admin.Id.ToString("N", CultureInfo.InvariantCulture)].AppliedBy);
        Assert.Equal(("admin", _admin.Id.ToString("N", CultureInfo.InvariantCulture)), (after.GetProperty("Runs")[0].GetProperty("AppliedByName").GetString(), after.GetProperty("Runs")[0].GetProperty("AppliedBy").GetString()));
        Assert.Equal(2, after.GetProperty("ImportedBookmarks").GetInt32());

        // Running it again: nothing new.
        var again = await PostJsonAsync(host, "/Bookmarks/Admin/Import/JellyfinEnhanced?dryRun=false", "{}");
        Assert.Equal(2, again.GetProperty("Users")[0].GetProperty("AlreadyImported").GetInt32());
        Assert.False(again.TryGetProperty("Snapshot", out var snapshot) && snapshot.ValueKind != JsonValueKind.Null, "nothing written, no snapshot");
    }

    [Fact]
    public async Task TheCapabilities_PointAnAdminAtTheImport_WhileJeBookmarksWait_AndNeverAnyoneElse()
    {
        // "Admins also see 'Have bookmarks in Jellyfin Enhanced? Import them in the plugin settings.'".
        using var fx = new JeImportFixture();
        var movie = fx.Library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        var manager = new BookmarkManager(
            fx.Store,
            fx.Library.Object,
            Mock.Of<Jellyfin.Plugin.Bookmarks.Clips.IClipExtractionService>(),
            Mock.Of<Jellyfin.Plugin.Bookmarks.Clips.IClipPlanner>(),
            ClipFileSupport.Ops(fx.Store),
            ClipFileSupport.Guard(fx.Store),
            TestData.ConfigurationProvider(new PluginConfiguration()),
            NullLogger<BookmarkManager>.Instance,
            null,
            fx.Importer);
        Assert.False(manager.GetCapabilities(fx.Admin, isAdmin: true).JellyfinEnhancedImportPending, "no JE bookmarks");

        fx.WriteBookmarks(JeImportFixture.AdminId, ("Bm_1", JeImportFixture.Entry(movie.Id, 60, "Bank job")));
        Assert.True(manager.GetCapabilities(fx.Admin, isAdmin: true).JellyfinEnhancedImportPending);
        Assert.False(manager.GetCapabilities(fx.Viewer, isAdmin: false).JellyfinEnhancedImportPending);

        await fx.Importer.ImportAsync(false, null, JeImportFixture.AdminId, Ct);
        Assert.False(manager.GetCapabilities(fx.Admin, isAdmin: true).JellyfinEnhancedImportPending, "imported");
    }

    private static async Task<JsonElement> GetJsonAsync(WebHostHarness host, string path)
    {
        using var response = await host.SendAsync(HttpMethod.Get, path, r => r.Headers.Add(JellyfinAuth.CallerHeader, "admin"));
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> PostJsonAsync(WebHostHarness host, string path, string? json, Guid? userId = null)
    {
        using var response = await SendAsync(host, path, json, userId);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static Task<HttpResponseMessage> SendAsync(WebHostHarness host, string path, string? json, Guid? userId = null)
        => host.SendAsync(HttpMethod.Post, path, r =>
        {
            r.Headers.Add(JellyfinAuth.CallerHeader, "admin");
            if (userId is { } id)
            {
                r.Headers.Add(JellyfinAuth.UserIdHeader, id.ToString("N", CultureInfo.InvariantCulture));
            }

            if (json is not null)
            {
                r.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
        });

    private Task<WebHostHarness> StartAsync()
    {
        var applicationHost = new Mock<IServerApplicationHost>();
        applicationHost.SetupGet(h => h.ApplicationVersion).Returns(new Version(10, 11, 5));
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, applicationHost.Object);
        Type[] own =
        [
            typeof(KitOptions), typeof(KitAbiStatus), typeof(KitPaths), typeof(KitPageHosts), typeof(ClientHealthRegistry), typeof(KitDiagnostics),
            typeof(IKitDiagnostics), typeof(IKitDiagnosticsSource), typeof(BookmarkPaths), typeof(IBookmarkStore), typeof(BackgroundWriteGate),
            typeof(JellyfinEnhancedBookmarkImporter), typeof(IBookmarkJeImport), typeof(JellyfinEnhancedReader), typeof(JellyfinEnhancedDiagnostics),
            typeof(ShortcutPolicy), typeof(IdentityResolver), typeof(MoveJournalReader)
        ];

        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_data.Path);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.Combine(_data.Path, "config"));
        return new WebHostHarness()
            .WithServices(services =>
            {
                foreach (var descriptor in registered.Where(d => own.Contains(d.ServiceType)))
                {
                    services.Add(descriptor);
                }

                services.AddSingleton(applicationHost.Object);
                services.AddSingleton(paths.Object);
                services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
                services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
                services.AddSingleton(_users.Object);
                services.AddSingleton(_library.Object);
                services.AddSingleton(TestData.ConfigurationProvider(new PluginConfiguration()));
                services.AddSingleton(Mock.Of<IBookmarkRecordsAdmin>());
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
            .WithMvc(typeof(BookmarksAdminController))
            .StartAsync();
    }
}
