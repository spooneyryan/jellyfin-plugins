using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Api;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Users;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Services;

/// <summary>
/// The kit's demo consumer: the developer user data demo behind
/// <c>EnableUserDataDemo</c>, on the kit's deleted-user policy, reconciler, <i>Remap by name</i>, export, import and
/// snapshot restore; and the same through Bookmarks' registrator and MVC.
/// </summary>
public sealed class UserDataDemoTests : IDisposable
{
    private readonly PluginConfiguration _configuration = new() { EnableUserDataDemo = true };
    private readonly TempDirectory _data = new();
    private readonly ListLogger _logger = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero));
    private readonly List<User> _users = [TestData.CreateUser("admin"), TestData.CreateUser("viewer"), TestData.CreateUser("viewer2")];
    private readonly List<IDisposable> _disposables = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StoreFile => _data.Combine("bookmarks", "userdata-demo.json");

    private string Snapshots => _data.Combine("bookmarks", "snapshots");

    public void Dispose()
    {
        _disposables.ForEach(d => d.Dispose());
        _data.Dispose();
    }

    [Fact]
    public async Task WhileOff_EveryCallIs403_AndTheOwnerHoldsNothing()
    {
        _configuration.EnableUserDataDemo = false;
        var (demo, owner, _) = Create();

        var calls = new List<Func<Task>>
        {
            () => Task.FromResult(demo.GetRecords()),
            () => demo.AddRecordAsync(Record(_users[0].Id, "x"), Ct),
            () => demo.ResetAsync(Ct),
            () => demo.ReconcileAsync(Ct),
            () => Task.FromResult(demo.GetUsers()),
            () => demo.RemapUsersAsync("Name", true, Ct),
            () => demo.ApplyPolicyAsync(JsonSerializer.SerializeToElement(new { UserIds = new[] { Guid.NewGuid() } }), Ct),
            () => Task.FromResult(demo.Export(new DefaultHttpContext())),
            () => demo.ImportAsync(new DefaultHttpContext().Request, null, null, null),
            () => Task.FromResult(demo.GetSnapshots()),
            () => demo.TakeSnapshotAsync(null, Ct),
            () => demo.RestoreSnapshotAsync("x", Ct)
        };
        foreach (var call in calls)
        {
            var ex = await Assert.ThrowsAsync<PluginException>(call);
            Assert.Equal(PluginErrorKind.FeatureDisabled, ex.Kind);
        }

        Assert.Empty(owner.CountRecordsByUser());
        Assert.Same(KnownUsers.Empty, owner.KnownUsers);
        Assert.False(owner.IsReadOnly);
        Assert.Equal(0, await owner.HandleUserDeletedAsync(_users[1].Id, DeletedUserPolicy.Delete, Ct));
        Assert.Null(await owner.SnapshotBeforeAsync(SnapshotOperations.UserDeleted, Ct));
        Assert.False(Directory.Exists(_data.Combine("bookmarks")));
    }

    [Fact]
    public async Task AddRecord_NeedsAServerUser_AndRecordsTheWritersName()
    {
        var (demo, owner, _) = Create();

        var added = await demo.AddRecordAsync(Record(_users[1].Id, "  first  ", hasFile: true), Ct);

        Assert.Equal(KnownUsers.FormatId(_users[1].Id), added["UserId"]);
        Assert.Equal("viewer", added["UserName"]);
        Assert.Equal("first", added["Text"]);
        Assert.Equal(true, added["HasFile"]);
        Assert.Equal("viewer", owner.KnownUsers.NameOf(_users[1].Id));
        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<PluginException>(() => demo.AddRecordAsync(Record(Guid.NewGuid(), "x"), Ct))).Kind);
        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<PluginException>(() => demo.AddRecordAsync(Record(_users[1].Id, new string('x', 201)), Ct))).Kind);
        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<PluginException>(() => demo.AddRecordAsync(JsonSerializer.SerializeToElement(new { Text = "no user" }), Ct))).Kind);
    }

    [Theory]
    [InlineData("KeepAsOrphan", 1, true)]
    [InlineData("Anonymize", 1, true)]
    [InlineData("Delete", 0, false)]
    [InlineData("typo", 1, true)]
    public async Task ADeletedUser_GetsTheSettingsPolicy(string policy, int kept, bool nameKept)
    {
        _configuration.DeletedUserPolicy = policy;
        var (demo, owner, reconciler) = Create();
        var viewer = _users[1];
        await demo.AddRecordAsync(Record(_users[0].Id, "admin's"), Ct);
        await demo.AddRecordAsync(Record(viewer.Id, "no file"), Ct);
        await demo.AddRecordAsync(Record(viewer.Id, "clip", hasFile: true), Ct);
        _users.Remove(viewer);

        Assert.Equal(2, await reconciler.HandleUserDeletedAsync(viewer.Id, Ct));

        var left = owner.Store.Current.Records.Where(r => r.UserId == viewer.Id).ToList();
        Assert.Equal(kept, left.Count);
        Assert.All(left, r => Assert.Equal((DateTime?)_clock.GetUtcNow().UtcDateTime, r.DeletedUtc));
        Assert.All(left, r => Assert.Equal("clip", r.Text));
        Assert.Equal(nameKept ? "viewer" : null, owner.KnownUsers.NameOf(viewer.Id));
        Assert.Single(owner.Store.Current.Records, r => r.UserId == _users[0].Id);
        Assert.Single(owner.Store.ListSnapshots(Snapshots), s => s.Operation == SnapshotOperations.UserDeleted);
    }

    [Fact]
    public async Task ARestoredDataFolder_DeletesNothing_AndRemapByNameRestoresOwnership()
    {
        var (demo, owner, _) = Create();
        foreach (var user in _users)
        {
            await demo.AddRecordAsync(Record(user.Id, user.Username + "'s record"), Ct);
        }

        // The server is rebuilt: the same names, new ids.
        var oldIds = _users.Select(u => u.Id).ToList();
        _users.Clear();
        _users.AddRange([TestData.CreateUser("admin"), TestData.CreateUser("Viewer"), TestData.CreateUser("viewer2")]);

        var run = await demo.ReconcileAsync(Ct);
        Assert.Equal(true, run["GuardTripped"]);
        Assert.Equal(3, owner.Store.Current.Records.Count);
        Assert.Equal(oldIds.Order(), owner.Store.Current.Records.Select(r => r.UserId).Order());
        var users = demo.GetUsers();
        Assert.Equal(true, users["StoredUsersMismatch"]);
        Assert.Equal(UserReconciler.MismatchBanner, users["Banner"]);

        var dryRun = await demo.RemapUsersAsync(null, null, Ct);
        Assert.Equal(true, dryRun["DryRun"]);
        Assert.Equal(3, Assert.IsAssignableFrom<IEnumerable<IDictionary<string, object?>>>(dryRun["Mapped"]).Count());
        Assert.Equal(oldIds.Order(), owner.Store.Current.Records.Select(r => r.UserId).Order());

        var applied = await demo.RemapUsersAsync("name", false, Ct);

        Assert.Equal(3, applied["MovedRecords"]);
        Assert.Equal(_users.Select(u => u.Id).Order(), owner.Store.Current.Records.Select(r => r.UserId).Order());
        Assert.Equal("Viewer", owner.KnownUsers.NameOf(_users[1].Id));
        Assert.Equal(false, demo.GetUsers()["StoredUsersMismatch"]);
        Assert.Single(owner.Store.ListSnapshots(Snapshots), s => s.Operation == SnapshotOperations.Remap);
        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<PluginException>(() => demo.RemapUsersAsync("Email", true, Ct))).Kind);
    }

    [Fact]
    public async Task ExportThenImport_RoundTrips_DryRunMergeAndReplace()
    {
        var (demo, owner, _) = Create();
        await demo.AddRecordAsync(Record(_users[0].Id, "one"), Ct);
        await demo.AddRecordAsync(Record(_users[1].Id, "Amélie, café scène ✂ 日本語", hasFile: true), Ct);
        var original = owner.Store.ExportData().Data.ToJsonString();

        var context = new DefaultHttpContext();
        var file = Assert.IsType<FileContentResult>(demo.Export(context));
        Assert.StartsWith("bookmarks-userdata-demo-export-", file.FileDownloadName, StringComparison.Ordinal);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        var envelope = Assert.IsType<JsonObject>(JsonNode.Parse(file.FileContents));
        Assert.Equal(Plugin.PluginId, (string)envelope["PluginId"]!);
        Assert.Equal("server-1", (string)envelope["ServerId"]!);
        Assert.Equal(2, envelope["Users"]!.AsArray().Count);
        Assert.True((bool)envelope["Settings"]!["EnableUserDataDemo"]!);

        await demo.ResetAsync(Ct);
        var dryRun = await demo.ImportAsync(Request(file.FileContents), null, null, null);
        Assert.Equal(true, dryRun["DryRun"]);
        Assert.Equal(2, Counts(dryRun)["Added"]);
        Assert.Empty(owner.Store.Current.Records);

        var merged = await demo.ImportAsync(Request(file.FileContents), "Merge", false, "Id");
        Assert.Equal(2, Counts(merged)["Added"]);
        Assert.Equal(original, owner.Store.ExportData().Data.ToJsonString());
        Assert.NotNull(merged["SafetySnapshot"]);

        var again = await demo.ImportAsync(Request(file.FileContents), "Merge", false, null);
        Assert.Equal(2, Counts(again)["Unchanged"]);

        await demo.AddRecordAsync(Record(_users[2].Id, "local only"), Ct);
        var replaced = await demo.ImportAsync(Request(file.FileContents), "Replace", false, null);
        Assert.Equal(1, Counts(replaced)["Removed"]);
        Assert.Equal(original, owner.Store.ExportData().Data.ToJsonString());
    }

    [Fact]
    public async Task ADeletedUsersRecords_AreFlaggedInTheExport_AndNeverImportedByNameForANewUserOfTheName()
    {
        // Viewer is deleted with KeepAsOrphan (a tombstone with a file stays), and a new user takes the name.
        var (demo, owner, reconciler) = Create();
        var viewer = _users[1];
        await demo.AddRecordAsync(Record(_users[0].Id, "admin's"), Ct);
        await demo.AddRecordAsync(Record(viewer.Id, "clip", hasFile: true), Ct);
        _users.Remove(viewer);
        await reconciler.HandleUserDeletedAsync(viewer.Id, Ct);
        var newViewer = TestData.CreateUser("viewer");
        _users.Add(newViewer);

        var file = Assert.IsType<FileContentResult>(demo.Export(new DefaultHttpContext()));
        var entry = Assert.Single(JsonNode.Parse(file.FileContents)!["Users"]!.AsArray(), u => (string)u!["Id"]! == KnownUsers.FormatId(viewer.Id))!;
        Assert.Equal("viewer", (string)entry["Name"]!);
        Assert.True((bool)entry["Deleted"]!);

        var report = await demo.ImportAsync(Request(file.FileContents), "Merge", false, "Name");

        var unknown = Assert.Single(Assert.IsAssignableFrom<IEnumerable<IDictionary<string, object?>>>(((IDictionary<string, object?>)report["Users"]!)["Unknown"]));
        Assert.Equal(KnownUsers.FormatId(viewer.Id), unknown["Id"]);
        Assert.Equal(ImportUnknownUser.DeletedOnThisServer, unknown["Reason"]);
        Assert.Equal(1, Counts(report)["SkippedUnknownUser"]);
        Assert.DoesNotContain(owner.Store.Current.Records, r => r.UserId == newViewer.Id);
    }

    [Fact]
    public void Export_WhileTheStoreIsFromANewerVersion_IsTheFileAtItsVersion()
    {
        // Export still works while the store is read-only, and the newer version can import it.
        Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
        var newer = $$"""{ "Version": 2, "KnownUsers": { "{{KnownUsers.FormatId(_users[0].Id)}}": "admin" }, "Records": [ { "Id": "{{Guid.NewGuid()}}", "UserId": "{{_users[0].Id}}", "Text": "from 1.2", "HasFile": false, "CreatedAt": "2026-10-01T18:00:00Z", "UpdatedAt": "2026-10-01T18:00:00Z", "Colour": "red" } ], "NewField": [ 1, 2 ] }""";
        File.WriteAllText(StoreFile, newer);
        var (demo, owner, _) = Create();
        Assert.True(owner.IsReadOnly);

        var file = Assert.IsType<FileContentResult>(demo.Export(new DefaultHttpContext()));

        var envelope = ExportEnvelope.Parse(file.FileContents);
        Assert.Equal(2, envelope.SchemaVersion);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(newer), envelope.Data));
        Assert.Equal("admin", Assert.Single(envelope.Users).Name);
        Assert.Equal(newer, File.ReadAllText(StoreFile));
    }

    [Fact]
    public async Task Snapshots_Rotate_AndRestoreSnapshotReplacesTheData()
    {
        var (demo, owner, _) = Create();
        await demo.AddRecordAsync(Record(_users[0].Id, "one"), Ct);
        var names = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            names.Add((string)(await demo.TakeSnapshotAsync(2, Ct))["Snapshot"]!);
            _clock.Advance(TimeSpan.FromHours(1));
            await demo.AddRecordAsync(Record(_users[0].Id, "more " + i), Ct);
        }

        var listed = Assert.IsAssignableFrom<IEnumerable<IDictionary<string, object?>>>(demo.GetSnapshots()["Snapshots"]).ToList();
        Assert.Equal([names[2], names[1]], listed.Where(s => (string)s["Kind"]! == "Daily").Select(s => (string)s["Name"]!));

        var restored = await demo.RestoreSnapshotAsync(names[1], Ct);

        Assert.Equal(names[1], restored["RestoredFrom"]);
        Assert.Equal(2, restored["Records"]);
        Assert.Equal(2, owner.Store.Current.Records.Count);
        Assert.StartsWith("userdata-demo-restore-", (string)restored["SafetySnapshot"]!, StringComparison.Ordinal);
        Assert.Equal(PluginErrorKind.NotFound, (await Assert.ThrowsAsync<PluginException>(() => demo.RestoreSnapshotAsync("userdata-demo-20200101-000000.json", Ct))).Kind);
        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<PluginException>(() => demo.RestoreSnapshotAsync(" ", Ct))).Kind);
        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<PluginException>(() => demo.TakeSnapshotAsync(0, Ct))).Kind);
    }

    [Fact]
    public async Task ThroughTheRegistratorAndMvc_TheEndpointsAndTheEventConsumerWork()
    {
        await using var host = await StartAsync();

        using (var viewer = await host.SendAsync(HttpMethod.Get, "/Bookmarks/Admin/UserDataDemo/Records", r => r.Headers.Add(JellyfinAuth.CallerHeader, "user")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
        }

        using (var added = await Send(host, HttpMethod.Post, "Records", JsonSerializer.Serialize(new { UserId = _users[1].Id, Text = "clip", HasFile = true })))
        {
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        }

        using (var export = await Send(host, HttpMethod.Get, "Export"))
        {
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
            Assert.Equal("attachment", export.Content.Headers.ContentDisposition?.DispositionType);
            Assert.True(export.Headers.CacheControl?.NoStore);
            var body = await export.Content.ReadAsStringAsync(Ct);
            using var import = await Send(host, HttpMethod.Post, "Import?dryRun=true&mode=Replace", body);
            Assert.Equal(HttpStatusCode.OK, import.StatusCode);
            Assert.Contains("\"Unchanged\":1", await import.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        using (var notAnExport = await Send(host, HttpMethod.Post, "Import", "{ \"Format\": \"other\" }"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, notAnExport.StatusCode);
            Assert.Equal("application/problem+json", notAnExport.Content.Headers.ContentType?.MediaType);
        }

        using (var tooLarge = await host.SendAsync(HttpMethod.Post, "/Bookmarks/Admin/UserDataDemo/Import", r =>
        {
            r.Headers.Add(JellyfinAuth.CallerHeader, "admin");
            r.Content = new StreamContent(new MemoryStream(new byte[16]));
            r.Content.Headers.ContentLength = ExportEnvelope.MaxImportBytes + 1;
        }))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        }

        using (var unknown = await Send(host, HttpMethod.Post, "Snapshots/Restore?name=userdata-demo-20200101-000000.json"))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        using (var current = await Send(host, HttpMethod.Post, "Users/ApplyPolicy", JsonSerializer.Serialize(new { UserIds = new[] { _users[0].Id } })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, current.StatusCode);
        }

        // Jellyfin deletes viewer: its EventManager resolves the consumers from a new scope (Events/EventManager.cs:43-63).
        var viewerUser = _users[1];
        _users.Remove(viewerUser);
        using (var scope = host.Services.CreateScope())
        {
            var consumer = Assert.Single(scope.ServiceProvider.GetServices<IEventConsumer<UserDeletedEventArgs>>());
            await consumer.OnEvent(new UserDeletedEventArgs(viewerUser));
        }

        var owner = host.Services.GetRequiredService<UserDataDemoOwner>();
        var kept = Assert.Single(owner.Store.Current.Records);
        Assert.NotNull(kept.DeletedUtc);
        Assert.True(host.Services.GetRequiredService<UserReconciler>().IsOrphan(viewerUser.Id));

        using var diagnostics = await host.SendAsync(HttpMethod.Get, "/Bookmarks/Admin/Diagnostics", r => r.Headers.Add(JellyfinAuth.CallerHeader, "admin"));
        using var document = JsonDocument.Parse(await diagnostics.Content.ReadAsStringAsync(Ct));
        var users = document.RootElement.GetProperty("Users");
        Assert.Equal("KeepAsOrphan", users.GetProperty("DeletedUserPolicy").GetString());
        Assert.Equal("viewer", users.GetProperty("OrphanUsers")[0].GetProperty("LastKnownName").GetString());
        Assert.Equal("1 records belong to 1 unknown users.", users.GetProperty("Message").GetString());
    }

    private static JsonElement Record(Guid userId, string text, bool hasFile = false)
        => JsonSerializer.SerializeToElement(new { UserId = userId.ToString("N"), Text = text, HasFile = hasFile });

    private static IDictionary<string, object?> Counts(IDictionary<string, object?> report) => Assert.IsAssignableFrom<IDictionary<string, object?>>(report["Records"]);

    private static HttpRequest Request(byte[] body)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        return context.Request;
    }

    private static Task<HttpResponseMessage> Send(WebHostHarness host, HttpMethod method, string path, string? json = null)
        => host.SendAsync(method, "/Bookmarks/Admin/UserDataDemo/" + path, r =>
        {
            r.Headers.Add(JellyfinAuth.CallerHeader, "admin");
            if (json is not null)
            {
                r.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
        });

    private Mock<IUserManager> UserManager()
    {
        var userManager = new Mock<IUserManager>();
        userManager.SetupUsers(() => _users.ToList());
        userManager.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => _users.Find(u => u.Id == id));
        return userManager;
    }

    private (BookmarkUserDataDemo Demo, UserDataDemoOwner Owner, UserReconciler Reconciler) Create()
    {
        var configuration = TestData.ConfigurationProvider(_configuration);
        var owner = new UserDataDemoOwner(configuration, StoreFile, Snapshots, _logger, _clock);
        var marks = new KitUserMarks(_data.Combine("bookmarks", KitUsersDocument.FileName), _logger, _clock);
        var userManager = UserManager();
        var reconciler = new UserReconciler(
            () => KitUsers.GetAll(userManager.Object).ToDictionary(u => u.Id, u => u.Username),
            [owner],
            marks,
            () => BookmarksKit.Options.DeletedUserPolicy(Services(configuration)),
            _logger,
            _clock);
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.SystemId).Returns("server-1");
        _disposables.Add(owner);
        _disposables.Add(marks);
        _disposables.Add(reconciler);
        return (new BookmarkUserDataDemo(configuration, owner, reconciler, userManager.Object, host.Object, _logger, _clock), owner, reconciler);
    }

    private static ServiceProvider Services(IPluginConfigurationProvider configuration)
        => new ServiceCollection().AddSingleton(configuration).BuildServiceProvider();

    private Task<WebHostHarness> StartAsync()
    {
        var applicationHost = new Mock<IServerApplicationHost>();
        applicationHost.SetupGet(h => h.ApplicationVersion).Returns(JellyfinLine.Server);
        applicationHost.SetupGet(h => h.SystemId).Returns("server-1");
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, applicationHost.Object);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_data.Path);
        var loggers = new Mock<ILoggerFactory>();
        loggers.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(_logger);
        var configuration = TestData.ConfigurationProvider(_configuration);
        var userManager = UserManager();

        return new WebHostHarness()
            .WithServices(services =>
            {
                // The registrator's own descriptors for the kit, its diagnostics, the store, the users services and the demo;
                // the server's services are mocks.
                Type[] wanted =
                [
                    typeof(Kit.Hosting.KitOptions), typeof(Kit.Hosting.KitAbiStatus), typeof(Kit.Hosting.KitPaths), typeof(Kit.Web.Pages.KitPageHosts),
                    typeof(Kit.Diagnostics.ClientHealthRegistry), typeof(Kit.Diagnostics.KitDiagnostics), typeof(Kit.Diagnostics.IKitDiagnostics),
                    typeof(Kit.Diagnostics.IKitDiagnosticsSource), typeof(Bookmarks.Storage.BookmarkPaths), typeof(Bookmarks.Storage.IBookmarkStore), typeof(UserReconciler),
                    typeof(KitUserMarks), typeof(IEventConsumer<UserDeletedEventArgs>), typeof(UserDataDemoOwner), typeof(IBookmarkUserDataDemo)
                ];

                // Of the owners (the kit's KitComponents since 1.1.0), only the demo's: the bookmark store's needs the clip worker.
                foreach (var descriptor in registered.Where(d => wanted.Contains(d.ServiceType)
                    || (d.ImplementationInstance is Kit.Hosting.KitComponent<IUserDataOwner> owner && owner.Name == typeof(UserDataDemoOwner).FullName)))
                {
                    services.Add(descriptor);
                }

                services.AddSingleton(Mock.Of<IBookmarkRecordsAdmin>());
                services.AddSingleton(Mock.Of<Jellyfin.Plugin.Bookmarks.Import.IBookmarkJeImport>());
                services.AddSingleton(Mock.Of<Jellyfin.Plugin.Bookmarks.Services.IBookmarkAdminTools>());

                services.AddSingleton(paths.Object);
                services.AddSingleton(loggers.Object);
                services.AddSingleton(configuration);
                services.AddSingleton(userManager.Object);
                services.AddSingleton(new Mock<ILibraryManager>().Object);
            })
            .WithMvc(typeof(BookmarksUserDataDemoController), typeof(BookmarksAdminController))
            .StartAsync();
    }
}
