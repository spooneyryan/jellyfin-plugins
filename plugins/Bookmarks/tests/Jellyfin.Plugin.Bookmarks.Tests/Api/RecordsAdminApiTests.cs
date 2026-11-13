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
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.Kit.Web.Pages;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Api;

/// <summary>
/// The kit's admin actions on Bookmarks' records of users and videos that are gone, behind Jellyfin's web
/// pipeline with the registrator's own services: <c>POST /Bookmarks/Admin/Users/Remap</c> (a dry run by default),
/// <c>POST /Bookmarks/Admin/Users/ApplyPolicy</c> and <c>POST /Bookmarks/Admin/Detached/Purge</c>, admins only, each after
/// a snapshot.
/// </summary>
public sealed class RecordsAdminApiTests : IAsyncLifetime
{
    private static readonly DateTime Created = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly TempDirectory _data = new();
    private readonly List<User> _server = [];
    private readonly User _admin = User("admin");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _data.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void TheController_IsForAdminsOnly()
        => Assert.Equal(Policies.RequiresElevation, typeof(BookmarksAdminController).GetCustomAttribute<AuthorizeAttribute>()!.Policy);

    [Fact]
    public async Task RemapByName_IsADryRunFirst_ThenMovesTheBookmarks_AndRefusesAnotherWayToMatch()
    {
        // A data folder restored onto a rebuilt server: the stored ids are unknown, their names are the users'.
        var oldAlex = User("alex");
        var newAlex = User("alex");
        _server.AddRange([_admin, newAlex]);
        await using var host = await StartAsync();
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        await store.AddAsync(Bookmark(oldAlex.Id), Ct, oldAlex);
        await store.AddAsync(Bookmark(oldAlex.Id), Ct, oldAlex);

        var dryRun = await PostJsonAsync(host, "/Bookmarks/Admin/Users/Remap");
        Assert.True(dryRun.GetProperty("DryRun").GetBoolean());
        Assert.Equal(1, dryRun.GetProperty("Mapped").GetArrayLength());
        Assert.All(store.GetAll(), b => Assert.Equal(oldAlex.Id, b.UserId));

        var applied = await PostJsonAsync(host, "/Bookmarks/Admin/Users/Remap?by=Name&dryRun=false");
        Assert.False(applied.GetProperty("DryRun").GetBoolean());
        Assert.All(store.GetAll(), b => Assert.Equal(newAlex.Id, b.UserId));

        using var byId = await SendAsync(host, "/Bookmarks/Admin/Users/Remap?by=Id");
        Assert.Equal(HttpStatusCode.BadRequest, byId.StatusCode);
        Assert.Equal("application/problem+json", byId.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ApplyPolicy_GivesUnknownUsersBookmarksTheDeletedUserPolicy_AndRefusesACurrentUser()
    {
        var gone = User("gone");
        _server.Add(_admin);
        await using var host = await StartAsync();
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var plain = Bookmark(gone.Id);
        await store.AddAsync(plain, Ct, gone);
        var clipFile = Path.Combine(_data.Path, "bookmarks", "clips", "gone.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(clipFile)!);
        await File.WriteAllBytesAsync(clipFile, [1, 2, 3], Ct);
        var clipped = Bookmark(gone.Id) with { Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, Mode = BookmarkClipMode.Fast, FilePath = clipFile, FileSize = 3, RequestedAt = Created, CompletedAt = Created } };
        await store.AddAsync(clipped, Ct);

        using (var current = await SendAsync(host, "/Bookmarks/Admin/Users/ApplyPolicy", "{\"UserIds\":[\"" + _admin.Id.ToString("N") + "\"]}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, current.StatusCode);
        }

        using (var empty = await SendAsync(host, "/Bookmarks/Admin/Users/ApplyPolicy", "{}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        }

        var report = await PostJsonAsync(host, "/Bookmarks/Admin/Users/ApplyPolicy", "{\"UserIds\":[\"" + gone.Id.ToString("D") + "\"]}");

        Assert.Equal("KeepAsOrphan", report.GetProperty("Policy").GetString());
        Assert.Equal(2, report.GetProperty("Affected").GetInt32());
        Assert.Null(store.Get(plain.Id));
        Assert.True(BookmarkRecordRules.IsTombstone(store.Get(clipped.Id)!));
        Assert.True(File.Exists(clipFile));
        Assert.Single(Directory.GetFiles(Path.Combine(_data.Path, "bookmarks", "snapshots", "pre-op"), "bookmarks-apply-policy-*"));
        Assert.True(host.Services.GetRequiredService<UserReconciler>().IsOrphan(gone.Id));
    }

    [Theory]
    [InlineData("KeepAsOrphan")]
    [InlineData("Anonymize")]
    [InlineData("Delete")]
    public async Task ADeletedUsersNamesAndNotes_LeaveNoTrace_InTheStoreItsBackupTheArchiveTheRenamesOrALaterSnapshot(string policy)
    {
        // Bookmarks 1.2.1: the deleted-user policy covered only the live store; the archive (and its earlier lines), the
        // tombstones' names and notes, the tombstones' renames and the store's .bak kept them. Every place the user's text
        // could be, each with its own marker, and the other user's, which stays.
        var gone = User("gone");
        var other = User("other");
        _server.AddRange([_admin, other]);
        await using var host = await StartAsync(new PluginConfiguration { DeletedUserPolicy = policy });
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var detached = host.Services.GetRequiredService<DetachedRecords>();
        var archive = detached.ArchiveOf(host.Services.GetRequiredService<BookmarkIdentityService>());
        var files = host.Services.GetRequiredService<ClipFileOps>();
        var clips = Path.Combine(_data.Path, "bookmarks", "clips");
        Directory.CreateDirectory(clips);

        ClipInfo ClipOf(string name)
        {
            var path = Path.Combine(clips, name);
            File.WriteAllBytes(path, [1, 2, 3]);
            return new ClipInfo { Status = BookmarkClipStatus.Completed, Mode = BookmarkClipMode.Fast, FilePath = path, FileSize = 3, RequestedAt = Created, CompletedAt = Created };
        }

        var plain = Bookmark(gone.Id) with { Name = "Residue-live-name", Note = "Residue-live-note" };
        var clipped = Bookmark(gone.Id) with { Name = "Residue-clip-name", Note = "Residue-clip-note", Clip = ClipOf("clipped.mkv") };
        var tombstone = BookmarkRecordRules.ToTombstone(Bookmark(gone.Id) with { Name = "Residue-tomb-name", Note = "Residue-tomb-note", Clip = ClipOf("kept.mkv") }, Created)!;
        var others = Bookmark(other.Id) with { Name = "Others-name", Note = "Others-note" };
        foreach (var bookmark in new[] { plain, clipped, tombstone, others })
        {
            await store.AddAsync(bookmark, Ct);
        }

        // The archive: a record that stays archived, one superseded since (an earlier line only), and the other user's.
        var archived = Bookmark(gone.Id) with { Name = "Residue-archived-name", Note = "Residue-archived-note", DetachedUtc = Created.AddDays(1) };
        var superseded = Bookmark(gone.Id) with { Name = "Residue-history-name", Note = "Residue-history-note", DetachedUtc = Created.AddDays(1) };
        var othersArchived = Bookmark(other.Id) with { Name = "Others-archived-name", DetachedUtc = Created.AddDays(1) };
        archive.Append([BookmarkIdentityService.ToArchived(archived), BookmarkIdentityService.ToArchived(superseded), BookmarkIdentityService.ToArchived(othersArchived)]);
        archive.MarkSuperseded([superseded.Id.ToString("D")]);

        // The renames' history: the tombstone's clip and the clip-bearing bookmark's, renamed from names with the user's words.
        foreach (var bookmark in new[] { clipped, tombstone })
        {
            files.History!.Add(new ClipRenameEntry { BookmarkId = bookmark.Id, From = Path.Combine(clips, "Residue-rename-" + bookmark.Id.ToString("N") + ".mkv"), To = bookmark.Clip!.FilePath! }, Created);
        }

        // A write since, so that the .bak holds the user's records too.
        await store.AddAsync(Bookmark(other.Id), Ct);
        var storeFile = Path.Combine(_data.Path, "bookmarks", "bookmarks.json");
        Assert.Contains("Residue-live-note", await File.ReadAllTextAsync(storeFile + ".bak", Ct), StringComparison.Ordinal);
        Assert.Contains("Residue-history-name", await File.ReadAllTextAsync(archive.FilePath, Ct), StringComparison.Ordinal);
        Assert.Contains("Residue-rename-", await File.ReadAllTextAsync(files.History!.FilePath, Ct), StringComparison.Ordinal);

        var report = await PostJsonAsync(host, "/Bookmarks/Admin/Users/ApplyPolicy", "{\"UserIds\":[\"" + gone.Id.ToString("D") + "\"]}");
        Assert.Equal(policy, report.GetProperty("Policy").GetString());

        // A daily snapshot afterwards.
        var snapshots = Path.Combine(_data.Path, "bookmarks", "snapshots");
        var daily = await store.Documents.SnapshotAsync(snapshots, 7, Ct);
        Assert.NotNull(daily);

        var after = new Dictionary<string, string>
        {
            ["store"] = await File.ReadAllTextAsync(storeFile, Ct),
            ["backup"] = File.Exists(storeFile + ".bak") ? await File.ReadAllTextAsync(storeFile + ".bak", Ct) : string.Empty,
            ["archive"] = await File.ReadAllTextAsync(archive.FilePath, Ct),
            ["renames"] = File.Exists(files.History!.FilePath) ? await File.ReadAllTextAsync(files.History!.FilePath, Ct) : string.Empty,
            ["daily snapshot"] = await File.ReadAllTextAsync(daily!, Ct)
        };
        foreach (var leftover in Directory.GetFiles(Path.Combine(_data.Path, "bookmarks"), "*.tmp", SearchOption.AllDirectories))
        {
            after["temporary file " + Path.GetFileName(leftover)] = await File.ReadAllTextAsync(leftover, Ct);
        }

        foreach (var (place, text) in after)
        {
            Assert.False(text.Contains("Residue-", StringComparison.Ordinal), $"The {place} still holds the deleted user's text: {text}");
        }

        // The other user's records stay, live and archived; the tombstones keep their clip files (all but Delete).
        Assert.Contains("Others-note", after["store"], StringComparison.Ordinal);
        Assert.Contains("Others-archived-name", after["archive"], StringComparison.Ordinal);
        Assert.Equal([othersArchived.Id.ToString("D")], archive.Read().Records.Select(r => r.Id));
        if (policy == "Delete")
        {
            Assert.DoesNotContain(store.GetAll(), b => b.UserId == gone.Id);
        }
        else
        {
            Assert.Equal(new[] { clipped.Id, tombstone.Id }.Order(), store.GetAll().Where(b => b.UserId == gone.Id).Select(b => b.Id).Order());
            Assert.All(store.GetAll().Where(b => b.UserId == gone.Id), b => Assert.True(BookmarkRecordRules.IsTombstone(b) && b.Name is null && b.Note is null && File.Exists(b.Clip!.FilePath)));
        }

        // The way back: the pre-operation snapshots taken just before hold them.
        var preOp = Path.Combine(snapshots, "pre-op");
        Assert.Contains("Residue-live-note", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(preOp, "bookmarks-apply-policy-*.json")), Ct), StringComparison.Ordinal);
        Assert.Contains("Residue-history-name", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(preOp, "bookmarks-archived-user-deleted-*.jsonl")), Ct), StringComparison.Ordinal);

        // Applied again: nothing left to change.
        var again = await PostJsonAsync(host, "/Bookmarks/Admin/Users/ApplyPolicy", "{\"UserIds\":[\"" + gone.Id.ToString("D") + "\"]}");
        Assert.Equal(0, again.GetProperty("Affected").GetInt32());
    }

    [Fact]
    public async Task ADeletedUserWhoseOnlyBookmarksAreArchived_IsCounted_AndAnUnreadableArchiveRefusesThePolicy_ChangingNothing()
    {
        var gone = User("gone");
        _server.Add(_admin);
        await using var host = await StartAsync();
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var archive = host.Services.GetRequiredService<DetachedRecords>().ArchiveOf(host.Services.GetRequiredService<BookmarkIdentityService>());
        var owner = host.Services.GetRequiredService<BookmarkUserDataOwner>();
        var plain = Bookmark(gone.Id) with { Note = "Residue-live-note" };
        await store.AddAsync(plain, Ct);
        archive.Append([BookmarkIdentityService.ToArchived(Bookmark(gone.Id) with { DetachedUtc = Created.AddDays(1) })]);
        Assert.Equal(2, owner.CountRecordsByUser()[gone.Id]);

        // An archive that can't be read: the counts throw (the kit skips the owner), and the policy changes nothing.
        var text = await File.ReadAllTextAsync(archive.FilePath, Ct);
        using (var held = new FileStream(archive.FilePath, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            // Changed (so not read from the cache) and held open without sharing: it can't be read.
            held.WriteByte((byte)0x0A);
            await held.FlushAsync(Ct);
            Assert.Throws<Jellyfin.Plugin.Kit.Api.PluginException>(() => owner.CountRecordsByUser());
            await Assert.ThrowsAsync<Jellyfin.Plugin.Kit.Api.PluginException>(() => owner.HandleUserDeletedAsync(gone.Id, DeletedUserPolicy.Delete, Ct));
            Assert.NotNull(store.Get(plain.Id));
        }

        await File.WriteAllTextAsync(archive.FilePath, text, Ct);

        // Readable again: an archive-only user is handled too.
        await store.RemoveAsync(plain.Id, Ct);
        Assert.Equal(1, owner.CountRecordsByUser()[gone.Id]);
        Assert.Equal(1, await owner.HandleUserDeletedAsync(gone.Id, DeletedUserPolicy.KeepAsOrphan, Ct));
        Assert.Empty(archive.Read().Records);
        Assert.False(owner.CountRecordsByUser().ContainsKey(gone.Id));
    }

    [Fact]
    public async Task RemapByName_GivesTheArchivedBookmarksToTheNewIdToo()
    {
        var oldAlex = User("alex");
        var newAlex = User("alex");
        _server.AddRange([_admin, newAlex]);
        await using var host = await StartAsync();
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var archive = host.Services.GetRequiredService<DetachedRecords>().ArchiveOf(host.Services.GetRequiredService<BookmarkIdentityService>());
        await store.AddAsync(Bookmark(oldAlex.Id), Ct, oldAlex);
        var archived = Bookmark(oldAlex.Id) with { DetachedUtc = Created.AddDays(1) };
        archive.Append([BookmarkIdentityService.ToArchived(archived)]);

        await PostJsonAsync(host, "/Bookmarks/Admin/Users/Remap?by=Name&dryRun=false");

        var record = Assert.Single(archive.Read().Records);
        Assert.Equal((archived.Id.ToString("D"), newAlex.Id), (record.Id, BookmarkUserDataOwner.ArchivedUserIdOf(record)));
        Assert.Equal(newAlex.Id, record.UserId);
    }

    [Fact]
    public async Task Purge_DeletesDetachedBookmarks_ButNotOnesWithAClipFile_AfterASnapshot()
    {
        _server.Add(_admin);
        await using var host = await StartAsync();
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var old = Bookmark(_admin.Id) with { DetachedUtc = DateTime.UtcNow.AddDays(-40) };
        var recent = Bookmark(_admin.Id) with { DetachedUtc = DateTime.UtcNow.AddDays(-2) };
        var clipped = Bookmark(_admin.Id) with { DetachedUtc = DateTime.UtcNow.AddDays(-40), Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = @"C:\clips\x.mkv", RequestedAt = Created } };
        var attached = Bookmark(_admin.Id);
        foreach (var bookmark in new[] { old, recent, clipped, attached })
        {
            await store.AddAsync(bookmark, Ct);
        }

        using (var negative = await SendAsync(host, "/Bookmarks/Admin/Detached/Purge?olderThanDays=-1"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        }

        var purged = await PostJsonAsync(host, "/Bookmarks/Admin/Detached/Purge?olderThanDays=30");

        Assert.Equal(1, purged.GetProperty("Purged").GetInt32());
        Assert.Equal(new[] { recent.Id, clipped.Id, attached.Id }.Order(), store.GetAll().Select(b => b.Id).Order());
        Assert.Single(Directory.GetFiles(Path.Combine(_data.Path, "bookmarks", "snapshots", "pre-op"), "bookmarks-purge-*"));

        var all = await PostJsonAsync(host, "/Bookmarks/Admin/Detached/Purge?includeArchive=true");
        Assert.Equal(1, all.GetProperty("Purged").GetInt32());
        Assert.Equal(2, store.GetAll().Count);
    }

    [Fact]
    public async Task TheRecordActions_AreRefusedToUsers()
    {
        // Jellyfin's RequiresElevation: admins (and API keys, which Jellyfin treats as admins), never a signed-in user.
        _server.Add(_admin);
        await using var host = await StartAsync();

        foreach (var path in new[] { "/Bookmarks/Admin/Users/Remap", "/Bookmarks/Admin/Users/ApplyPolicy", "/Bookmarks/Admin/Detached/Purge" })
        {
            using var refused = await host.SendAsync(HttpMethod.Post, path, r =>
            {
                r.Headers.Add(JellyfinAuth.CallerHeader, "user");
                r.Headers.Add(JellyfinAuth.UserIdHeader, Guid.NewGuid().ToString("N"));
                r.Content = new StringContent("{\"UserIds\":[]}", Encoding.UTF8, "application/json");
            });
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            using var anonymous = await host.SendAsync(HttpMethod.Post, path);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
    }

    private static User User(string name)
    {
        var user = TestData.CreateUser(name);
        user.Id = Guid.NewGuid();
        return user;
    }

    private static Bookmark Bookmark(Guid userId)
    {
        var item = Guid.NewGuid();
        return new Bookmark
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ItemId = item,
            StartTicks = 600_000_000,
            ItemName = "Heat (1995)",
            Identity = new IdentitySnapshot { ItemId = item, Kind = "Movie", Name = "Heat" },
            AttachedUtc = Created,
            CreatedAt = Created,
            UpdatedAt = Created
        };
    }

    private static async Task<JsonElement> PostJsonAsync(WebHostHarness host, string path, string? json = null)
    {
        using var response = await SendAsync(host, path, json);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static Task<HttpResponseMessage> SendAsync(WebHostHarness host, string path, string? json = null)
        => host.SendAsync(HttpMethod.Post, path, r =>
        {
            r.Headers.Add(JellyfinAuth.CallerHeader, "admin");
            if (json is not null)
            {
                r.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
        });

    private Task<WebHostHarness> StartAsync(PluginConfiguration? configuration = null)
    {
        var applicationHost = new Mock<IServerApplicationHost>();
        applicationHost.SetupGet(h => h.ApplicationVersion).Returns(JellyfinLine.Server);
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, applicationHost.Object);
        Type[] own =
        [
            typeof(KitOptions), typeof(KitAbiStatus), typeof(KitPaths), typeof(KitPageHosts), typeof(ClientHealthRegistry), typeof(KitDiagnostics),
            typeof(IKitDiagnostics), typeof(BookmarkPaths), typeof(IBookmarkStore), typeof(BackgroundWriteGate), typeof(ClipFileOps), typeof(ClipStateCache),
            typeof(IBookmarkRecordsAdmin), typeof(UserReconciler), typeof(KitUserMarks), typeof(BookmarkUserDataOwner), typeof(DetachedRecords),
            typeof(IdentityResolver), typeof(MoveJournalReader), typeof(BookmarkIdentityService)
        ];

        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_data.Path);
        var users = new Mock<IUserManager>();
        users.SetupUsers(() => _server.ToList());
        users.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => _server.Find(u => u.Id == id));

        return new WebHostHarness()
            .WithServices(services =>
            {
                // Of the user data owners, the bookmark store's (the kit's KitComponent); the clip worker is a mock.
                foreach (var descriptor in registered.Where(d => own.Contains(d.ServiceType)
                    || (d.ImplementationInstance is KitComponent<IUserDataOwner> owner && owner.Name == typeof(BookmarkUserDataOwner).FullName)))
                {
                    services.Add(descriptor);
                }

                services.AddSingleton(applicationHost.Object);
                services.AddSingleton(paths.Object);
                services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
                services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
                services.AddSingleton(users.Object);
                services.AddSingleton(Mock.Of<ILibraryManager>());
                services.AddSingleton(Mock.Of<IClipExtractionService>());
                services.AddSingleton(Mock.Of<Jellyfin.Plugin.Bookmarks.Import.IBookmarkJeImport>());
                services.AddSingleton(Mock.Of<Jellyfin.Plugin.Bookmarks.Services.IBookmarkAdminTools>());
                services.AddSingleton(TestData.ConfigurationProvider(configuration ?? new PluginConfiguration()));
            })
            .WithMvc(typeof(BookmarksAdminController))
            .StartAsync();
    }
}
