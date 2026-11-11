using System;
using System.Collections.Generic;
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
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Api;

/// <summary>
/// The read-only store end to end in process: the plugin's own registrations (store, gate, clip worker as a hosted service, manager,
/// diagnostics) behind Jellyfin's web pipeline, on a <c>bookmarks.json</c> of a newer schema and on a 1.0 file whose upgrade
/// fails. Reads work, every write answers 503 with ProblemDetails, Capabilities says why, Diagnostics shows the
/// status and the kept copy, the hosted worker stays idle, and the file never changes. Since Bookmarks 1.2 the way out of
/// missing or unreadable data on the settings page's routes: an admin restores a snapshot, or starts empty, and the data,
/// the gate and the hosted clip worker come back at once.
/// </summary>
public sealed class ReadOnlyStoreApiTests : IAsyncLifetime
{
    private static readonly Guid AdminId = new("9fa5c677-8cf7-4a94-bff0-c82a60581ccf");
    private static readonly Guid QueuedClip = new("5f7ef9bc-14bc-4e9a-a9c6-07c0b46a9210");
    private static readonly Guid ViewerId = new("2c3b7d1e-6a4f-4b8e-9d0c-1f2e3a4b5c6d");

    private readonly TempDirectory _data = new();
    private readonly ListLogger _logger = new();
    private readonly CountingRunner _runner = new();
    private readonly User _admin = TestData.CreateUser("admin");
    private readonly User _viewer = TestData.CreateUser("viewer");

    public ReadOnlyStoreApiTests()
    {
        _admin.Id = AdminId;
        _viewer.Id = ViewerId;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StoreFile => Path.Combine(_data.Path, "bookmarks", "bookmarks.json");

    public static TheoryData<string, string, int> Fixtures => new()
    {
        { StoreFixtures.Schema3, "NewerVersion", 8 },
        { StoreFixtures.Bookmarks100DuplicateKey, "MigrationFailed", 0 },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task ReadOnlyData_ReadsWork_WritesAre503_TheWorkerIsIdle_AndTheFileNeverChanges(string fixture, string reason, int listed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
        var original = StoreFixtures.Read(fixture);
        await File.WriteAllBytesAsync(StoreFile, original, Ct);
        var message = reason == "NewerVersion" ? StoreMessages.NewerVersion : StoreMessages.MigrationFailed;

        await using var host = await StartAsync();

        // Capabilities: read-only, and why (for the page's two banners).
        var capabilities = await GetJsonAsync(host, "/Bookmarks/Capabilities");
        Assert.True(capabilities.GetProperty("StoreReadOnly").GetBoolean());
        Assert.Equal(reason, capabilities.GetProperty("StoreReadOnlyReason").GetString());

        // Reads: a newer version's bookmarks as far as this version reads them; nothing after a failed upgrade.
        var mine = await GetJsonAsync(host, "/Bookmarks");
        Assert.Equal(listed, mine.GetArrayLength());

        // Every write: 503 with the store's message as ProblemDetails. Also for a bookmark the file holds after a failed
        // upgrade, which the store's empty stand-in doesn't list (a page loaded before the upgrade; it was once 404
        // "Bookmark not found."), and for an id the file doesn't hold (the same answer, so ids can't be probed).
        var create = """{"ItemId":"ba7ef8f5-0757-f5f6-0cae-61b9b7fd19f5","StartTicks":10000000,"EndTicks":20000000,"Note":"x"}""";
        await AssertUnavailableAsync(host, HttpMethod.Post, "/Bookmarks", message, create);
        foreach (var id in new[] { QueuedClip, Guid.NewGuid() })
        {
            await AssertUnavailableAsync(host, HttpMethod.Post, $"/Bookmarks/{id}", message, """{"StartTicks":0}""");
            await AssertUnavailableAsync(host, HttpMethod.Post, $"/Bookmarks/{id}", message, """{"StartTicks":0,"Name":"Vault","IfUpdatedAt":"2026-09-29T10:05:27.1198706Z"}""");
            await AssertUnavailableAsync(host, HttpMethod.Post, $"/Bookmarks/{id}/Name", message, """{"Name":"Vault"}""");
            await AssertUnavailableAsync(host, HttpMethod.Post, $"/Bookmarks/{id}/Clip", message, """{"Mode":"Fast"}""");
            await AssertUnavailableAsync(host, HttpMethod.Post, $"/Bookmarks/{id}/Clip/Cancel", message);
            await AssertUnavailableAsync(host, HttpMethod.Delete, $"/Bookmarks/{id}", message);
            await AssertUnavailableAsync(host, HttpMethod.Delete, $"/Bookmarks/{id}?deleteClip=true", message);
            await AssertUnavailableAsync(host, HttpMethod.Delete, $"/Bookmarks/{id}/Clip", message);
        }

        // The bulk delete (with and without the clips, a bookmark the file holds among the ids) and the preferences
        // refuse too; the My Bookmarks page's lists and the preferences still read.
        foreach (var deleteClips in new[] { "false", "true" })
        {
            var ids = "[\"" + QueuedClip + "\",\"" + Guid.NewGuid() + "\"]";
            await AssertUnavailableAsync(host, HttpMethod.Post, "/Bookmarks/BulkDelete", message, "{\"Ids\":" + ids + ",\"DeleteClips\":" + deleteClips + "}");
        }

        await AssertUnavailableAsync(host, HttpMethod.Post, "/Bookmarks/Preferences", message, """{"AskForName":true}""");
        Assert.Equal(listed, (await GetJsonAsync(host, "/Bookmarks/Query?limit=200")).GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(listed > 0, (await GetJsonAsync(host, "/Bookmarks/Groups")).GetProperty("TotalRecordCount").GetInt32() > 0);
        Assert.False((await GetJsonAsync(host, "/Bookmarks/Preferences")).GetProperty("PauseWhileNaming").GetBoolean());

        // The clips' bulk delete refuses (a clip the file holds among the ids); the My Clips page's list, its numbers
        // and the clips' states still read (while the store is read-only, the list, preview and download work).
        await AssertUnavailableAsync(host, HttpMethod.Post, "/Bookmarks/Clips/BulkDelete", message, "{\"Ids\":[\"" + QueuedClip + "\",\"" + Guid.NewGuid() + "\"]}");
        var clips = await GetJsonAsync(host, "/Bookmarks/Clips?limit=200");
        Assert.Equal(clips.GetProperty("TotalRecordCount").GetInt32(), (await GetJsonAsync(host, "/Bookmarks/Clips/Summary")).GetProperty("Count").GetInt32());
        Assert.Equal(listed > 0, clips.GetProperty("Items").EnumerateArray().Any(c => Guid.Parse(c.GetProperty("Id").GetString()!) == QueuedClip));
        Assert.Equal(listed > 0 ? 1 : 0, (await GetJsonAsync(host, $"/Bookmarks/Clips/Status?ids={QueuedClip:N}")).GetArrayLength());

        // Reading it stays what the store holds: the newer file's bookmark, nothing after a failed upgrade.
        using (var read = await SendAsync(host, HttpMethod.Get, $"/Bookmarks/{QueuedClip}"))
        {
            Assert.Equal(listed > 0 ? HttpStatusCode.OK : HttpStatusCode.NotFound, read.StatusCode);
        }

        // Diagnostics: the status and the store's sentence; after a failed upgrade, the copy to go back with.
        var store = (await GetJsonAsync(host, "/Bookmarks/Admin/Diagnostics")).GetProperty("Store");
        Assert.Equal("ReadOnly" + (reason == "NewerVersion" ? "NewerVersion" : "MigrationFailed"), store.GetProperty("Status").GetString());
        Assert.True(store.GetProperty("ReadOnly").GetBoolean());
        Assert.Equal(listed > 0 ? 0 : 1, store.GetProperty("KeptFileCount").GetInt32());

        // The admin actions on records of users and videos that are gone refuse too: 409, nothing changed; a dry run
        // of Remap by name still answers.
        foreach (var (path, json) in new (string, string?)[]
        {
            ("/Bookmarks/Admin/Users/Remap?by=Name&dryRun=false", null),
            ("/Bookmarks/Admin/Users/ApplyPolicy", "{\"UserIds\":[\"" + Guid.NewGuid().ToString("N") + "\"]}"),
            ("/Bookmarks/Admin/Detached/Purge?includeArchive=true", null),
        })
        {
            using var refused = await SendAsync(host, HttpMethod.Post, path, json);
            Assert.True(refused.StatusCode == HttpStatusCode.Conflict, $"{path}: {refused.StatusCode} {await refused.Content.ReadAsStringAsync(Ct)}");
        }

        using (var dryRun = await SendAsync(host, HttpMethod.Post, "/Bookmarks/Admin/Users/Remap"))
        {
            Assert.Equal(HttpStatusCode.OK, dryRun.StatusCode);
        }

        // The Jellyfin Enhanced import: the import is the store's 503, nothing written; its dry run still reports.
        var jeFolder = Path.Combine(_data.Path, "config", "Jellyfin.Plugin.JellyfinEnhanced", AdminId.ToString("N"));
        Directory.CreateDirectory(jeFolder);
        await File.WriteAllTextAsync(
            Path.Combine(jeFolder, "bookmark.json"),
            "{\"Bookmarks\":{\"Bm_1\":{\"ItemId\":\"" + Guid.NewGuid().ToString("N") + "\",\"TmdbId\":\"\",\"MediaType\":\"movie\",\"Name\":\"A film\",\"Timestamp\":60,\"Label\":\"x\"}}}",
            Ct);
        await AssertUnavailableAsync(host, HttpMethod.Post, "/Bookmarks/Admin/Import/JellyfinEnhanced?dryRun=false", message, "{}");
        using (var jeDryRun = await SendAsync(host, HttpMethod.Post, "/Bookmarks/Admin/Import/JellyfinEnhanced?dryRun=true", "{}"))
        {
            var body = await jeDryRun.Content.ReadAsStringAsync(Ct);
            Assert.True(jeDryRun.StatusCode == HttpStatusCode.OK, body);
            using var report = JsonDocument.Parse(body);
            Assert.True(report.RootElement.GetProperty("StoreReadOnly").GetBoolean());
            Assert.Equal(1, report.RootElement.GetProperty("Users")[0].GetProperty("Import").GetInt32());
        }

        // The settings page's tools: an import that writes and a restore are refused (409, before any snapshot), a
        // dry run of the import still reports (with StoreReadOnly), the snapshots are listed; the export is the newer file
        // as it is, or 503 after a failed upgrade, whose untouched file and copy are the backup.
        var envelope = "{\"Format\":\"jellyfin-plugin-export/1\",\"Plugin\":\"Bookmarks\",\"PluginId\":\"" + Plugin.PluginId + "\",\"PluginVersion\":\"1.1.0.0\","
            + "\"SchemaVersion\":2,\"ExportedUtc\":\"2026-10-07T12:00:00Z\",\"Users\":[],\"Data\":{\"Version\":2,\"Bookmarks\":[]},\"Archive\":[]}";
        foreach (var (path, json) in new (string, string?)[]
        {
            ("/Bookmarks/Admin/Import?dryRun=false&mode=Replace", envelope),
            ("/Bookmarks/Admin/Snapshots/Restore?name=bookmarks-20261001-031000.json", null),
        })
        {
            using var refused = await SendAsync(host, HttpMethod.Post, path, json);
            Assert.True(refused.StatusCode == HttpStatusCode.Conflict, $"{path}: {refused.StatusCode} {await refused.Content.ReadAsStringAsync(Ct)}");
        }

        using (var importDryRun = await SendAsync(host, HttpMethod.Post, "/Bookmarks/Admin/Import", envelope))
        {
            var body = await importDryRun.Content.ReadAsStringAsync(Ct);
            Assert.True(importDryRun.StatusCode == HttpStatusCode.OK, body);
            using var report = JsonDocument.Parse(body);
            Assert.True(report.RootElement.GetProperty("StoreReadOnly").GetBoolean());
        }

        Assert.Empty((await GetJsonAsync(host, "/Bookmarks/Admin/Snapshots")).GetProperty("Snapshots").EnumerateArray());
        using (var export = await SendAsync(host, HttpMethod.Get, "/Bookmarks/Admin/Export"))
        {
            Assert.Equal(listed > 0 ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, export.StatusCode);
        }

        // The identity service is idle, and says so once.
        await WaitUntilAsync(() => _logger.Entries.Any(e => e.Message.Contains("so IdentityService is idle", StringComparison.Ordinal)));
        Assert.Single(_logger.Entries, e => e.Message.Contains("so IdentityService is idle", StringComparison.Ordinal));

        // The hosted clip worker never ran: the queued and running jobs of the newer file stay as they are, and the gate
        // said so once.
        await WaitUntilAsync(() => _logger.Entries.Any(e => e.Message.Contains("so ClipWorker is idle", StringComparison.Ordinal)));
        await Task.Delay(300, Ct);
        Assert.Equal(0, _runner.Runs);
        Assert.Single(_logger.Entries, e => e.Message.Contains("so ClipWorker is idle", StringComparison.Ordinal));
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.Contains($"read-only ({reason})", StringComparison.Ordinal));

        await host.DisposeAsync();
        Assert.Equal(original, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StoreFile)!, "*.corrupt-*"));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(StoreFile)!, "snapshots")));
    }

    [Fact]
    public async Task MissingData_AnAdminRestoresASnapshotPutBack_TheDataIsWritable_AndTheClipWorkerStarts()
    {
        // Found in testing: only a newer Bookmarks' snapshot is there, so the data is read-only
        // (DataMissing) and the hosted clip worker idle. A readable snapshot put back while the server runs is restored from
        // the settings page: no restart, every snapshot kept, the clip job it held queued ended, and the worker starts.
        var folder = Path.GetDirectoryName(StoreFile)!;
        var snapshots = Path.Combine(folder, "snapshots");
        Directory.CreateDirectory(snapshots);
        var newer = Path.Combine(snapshots, "bookmarks-20261002-031000.json");
        await File.WriteAllBytesAsync(newer, StoreFixtures.Read(StoreFixtures.Schema3), Ct);
        await using var host = await StartAsync();

        Assert.Equal("DataMissing", (await GetJsonAsync(host, "/Bookmarks/Capabilities")).GetProperty("StoreReadOnlyReason").GetString());
        var listed = await GetJsonAsync(host, "/Bookmarks/Admin/Snapshots");
        Assert.True(listed.GetProperty("Recoverable").GetBoolean());
        Assert.Equal("DataMissing", listed.GetProperty("StoreReadOnlyReason").GetString());
        await WaitUntilAsync(() => _logger.Entries.Any(e => e.Message.Contains("so ClipWorker is idle", StringComparison.Ordinal)));
        using (var refused = await SendAsync(host, HttpMethod.Post, "/Bookmarks/Admin/Snapshots/Restore?name=bookmarks-20261002-031000.json"))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }

        // Synthetic data with a clip job queued, its paths in the test's folder.
        var queued = TestData.CreateBookmark(AdminId, 60, 90) with
        {
            Clip = new ClipInfo { Status = BookmarkClipStatus.Queued, Mode = BookmarkClipMode.Fast, StartTicks = 60 * TestData.OneSecond, EndTicks = 90 * TestData.OneSecond, RequestedAt = new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc) }
        };
        var good = BookmarkStore.WithBookmarks(BookmarkStoreDocument.CreateEmpty(), [queued, TestData.CreateBookmark(AdminId, 200, 230)]);
        await File.WriteAllBytesAsync(Path.Combine(snapshots, "bookmarks-20261001-031000.json"), JsonSerializer.SerializeToUtf8Bytes(good, JsonStoreFormat.Options), Ct);

        using var restore = await SendAsync(host, HttpMethod.Post, "/Bookmarks/Admin/Snapshots/Restore?name=bookmarks-20261001-031000.json");
        var body = await restore.Content.ReadAsStringAsync(Ct);
        Assert.True(restore.StatusCode == HttpStatusCode.OK, body);
        using (var result = JsonDocument.Parse(body))
        {
            var r = result.RootElement;
            Assert.Equal("bookmarks-20261001-031000.json", r.GetProperty("RestoredFrom").GetString());
            Assert.False(r.GetProperty("StartedEmpty").GetBoolean());
            Assert.Empty(r.GetProperty("MovedAside").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, r.GetProperty("SafetySnapshot").ValueKind);
            Assert.Equal(2, r.GetProperty("Bookmarks").GetInt32());
            Assert.Equal(1, r.GetProperty("ClipJobsEnded").GetInt32());
        }

        var capabilities = await GetJsonAsync(host, "/Bookmarks/Capabilities");
        Assert.False(capabilities.GetProperty("StoreReadOnly").GetBoolean());
        var store = (await GetJsonAsync(host, "/Bookmarks/Admin/Diagnostics")).GetProperty("Store");
        Assert.Equal("RestoredFromSnapshot", store.GetProperty("Status").GetString());
        Assert.Contains("; an admin restored the data from the snapshot bookmarks-20261001-031000.json", store.GetProperty("Message").GetString(), StringComparison.Ordinal);
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.StartsWith("An admin (" + AdminId.ToString("D"), StringComparison.Ordinal) && e.Message.Contains("bookmarks-20261001-031000.json", StringComparison.Ordinal));
        Assert.True(File.Exists(newer));
        var after = await GetJsonAsync(host, "/Bookmarks/Admin/Snapshots");
        Assert.False(after.GetProperty("Recoverable").GetBoolean());

        // Why the snapshots are kept, for the page's label: the data was restored.
        Assert.Equal("RestoredFromSnapshot", after.GetProperty("KeptBecause").GetString());
        Assert.All(after.GetProperty("Snapshots").EnumerateArray(), s => Assert.True(s.GetProperty("Kept").GetBoolean()));

        // The clip worker, idle since it started, runs: a clip asked for now is made, and the job the snapshot held stays
        // ended (never resumed).
        await WaitUntilAsync(() => _logger.Entries.Any(e => e.Message.Contains("so the clip worker starts", StringComparison.Ordinal)));
        var created = await SendAsync(host, HttpMethod.Post, "/Bookmarks", """{"ItemId":"ba7ef8f5-0757-f5f6-0cae-61b9b7fd19f5","StartTicks":10000000,"EndTicks":20000000}""");
        var createdBody = await created.Content.ReadAsStringAsync(Ct);
        Assert.True(created.IsSuccessStatusCode, createdBody);
        var id = JsonDocument.Parse(createdBody).RootElement.GetProperty("Id").GetString();
        using (var clip = await SendAsync(host, HttpMethod.Post, $"/Bookmarks/{id}/Clip", """{"Mode":"Fast"}"""))
        {
            Assert.True(clip.IsSuccessStatusCode, await clip.Content.ReadAsStringAsync(Ct));
        }

        await WaitUntilAsync(() => _runner.Runs > 0);
        await Task.Delay(300, Ct);
        Assert.Equal(1, _runner.Runs);
        using (var ended = await SendAsync(host, HttpMethod.Get, $"/Bookmarks/{queued.Id}"))
        {
            using var dto = JsonDocument.Parse(await ended.Content.ReadAsStringAsync(Ct));
            Assert.Equal("Failed", dto.RootElement.GetProperty("Clip").GetProperty("Status").GetString());
        }
    }

    [Fact]
    public async Task UnreadableData_AnAdminStartsEmpty_TheFileIsKept_AndOnlyThen()
    {
        // bookmarks.json holds something unreadable and nothing can take its place: read-only (DataUnreadable), the file
        // left as it is. Start empty is refused while the data is there, and only an admin's choice: the file kept as
        // bookmarks.json.corrupt-<time>, the data empty and writable at once.
        Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
        await File.WriteAllTextAsync(StoreFile, "{ \"Version\": 2, not json", Ct);
        await using var host = await StartAsync();

        Assert.Equal("DataUnreadable", (await GetJsonAsync(host, "/Bookmarks/Capabilities")).GetProperty("StoreReadOnlyReason").GetString());
        await AssertUnavailableAsync(host, HttpMethod.Post, "/Bookmarks/Preferences", StoreMessages.DataUnreadable, """{"AskForName":true}""");
        using (var export = await SendAsync(host, HttpMethod.Get, "/Bookmarks/Admin/Export"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, export.StatusCode);
        }

        Assert.Equal("{ \"Version\": 2, not json", await File.ReadAllTextAsync(StoreFile, Ct));

        var started = await SendAsync(host, HttpMethod.Post, "/Bookmarks/Admin/Data/StartEmpty");
        var body = await started.Content.ReadAsStringAsync(Ct);
        Assert.True(started.StatusCode == HttpStatusCode.OK, body);
        var moved = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StoreFile)!, "bookmarks.json.corrupt-*"));
        using (var result = JsonDocument.Parse(body))
        {
            Assert.True(result.RootElement.GetProperty("StartedEmpty").GetBoolean());
            Assert.Equal([Path.GetFileName(moved)], result.RootElement.GetProperty("MovedAside").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(0, result.RootElement.GetProperty("Bookmarks").GetInt32());
        }

        Assert.Equal("{ \"Version\": 2, not json", await File.ReadAllTextAsync(moved, Ct));
        Assert.False((await GetJsonAsync(host, "/Bookmarks/Capabilities")).GetProperty("StoreReadOnly").GetBoolean());
        Assert.Equal("RecoveredEmpty", (await GetJsonAsync(host, "/Bookmarks/Admin/Diagnostics")).GetProperty("Store").GetProperty("Status").GetString());
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.StartsWith("An admin (" + AdminId.ToString("D") + ") started the bookmark data empty", StringComparison.Ordinal));
        using (var preferences = await SendAsync(host, HttpMethod.Post, "/Bookmarks/Preferences", """{"AskForName":true}"""))
        {
            Assert.True(preferences.IsSuccessStatusCode);
        }

        // Once the data is there, start empty is refused: 409, nothing changed.
        using (var again = await SendAsync(host, HttpMethod.Post, "/Bookmarks/Admin/Data/StartEmpty"))
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            using var problem = JsonDocument.Parse(await again.Content.ReadAsStringAsync(Ct));
            Assert.Equal(StoreMessages.NothingToRecover, problem.RootElement.GetProperty("detail").GetString());
        }

        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StoreFile)!, "bookmarks.json.corrupt-*"));
    }

    [Fact]
    public async Task UnreadableData_AReadableFilePutBackWhileTheServerRuns_IsNeverMovedAside_AndAUserIsToldToAskTheAdmin()
    {
        // Found in testing: the recovery went by the status of the load, so a good bookmarks.json
        // an admin copied back while the server runs was moved aside as unreadable by Start empty (or Restore), and the data
        // started empty. Finding 9: the 503 told every user that the plugin's settings page says what to do.
        var folder = Path.GetDirectoryName(StoreFile)!;
        var snapshots = Path.Combine(folder, "snapshots");
        Directory.CreateDirectory(snapshots);
        await File.WriteAllTextAsync(StoreFile, "{ \"Version\": 2, not json", Ct);
        var empty = JsonSerializer.SerializeToUtf8Bytes(BookmarkStoreDocument.CreateEmpty(), JsonStoreFormat.Options);
        await File.WriteAllBytesAsync(Path.Combine(snapshots, "bookmarks-20261001-031000.json"), empty, Ct);
        await using var host = await StartAsync();
        Assert.Equal("DataUnreadable", (await GetJsonAsync(host, "/Bookmarks/Capabilities")).GetProperty("StoreReadOnlyReason").GetString());

        await AssertUnavailableAsync(host, HttpMethod.Post, "/Bookmarks/Preferences", StoreMessages.DataUnreadable, """{"AskForName":true}""");
        using (var user = await host.SendAsync(HttpMethod.Post, "/Bookmarks/Preferences", request =>
        {
            request.Headers.Add(JellyfinAuth.CallerHeader, "user");
            request.Headers.Add(JellyfinAuth.UserIdHeader, ViewerId.ToString("N"));
            request.Content = new StringContent("""{"AskForName":true}""", Encoding.UTF8, "application/json");
        }))
        {
            var text = await user.Content.ReadAsStringAsync(Ct);
            Assert.True(user.StatusCode == HttpStatusCode.ServiceUnavailable, text);
            using var problem = JsonDocument.Parse(text);
            Assert.Equal(StoreMessages.ReadOnlyForUsers(StoreStatus.ReadOnlyDataUnreadable), problem.RootElement.GetProperty("detail").GetString());
            Assert.DoesNotContain("settings page", text, StringComparison.Ordinal);
        }

        // The admin copies a readable bookmarks.json back, then chooses Start empty, or a snapshot: refused, nothing moved.
        var good = JsonSerializer.SerializeToUtf8Bytes(BookmarkStore.WithBookmarks(BookmarkStoreDocument.CreateEmpty(), [TestData.CreateBookmark(AdminId, 60, 90)]), JsonStoreFormat.Options);
        await File.WriteAllBytesAsync(StoreFile, good, Ct);
        foreach (var path in new[] { "/Bookmarks/Admin/Data/StartEmpty", "/Bookmarks/Admin/Snapshots/Restore?name=bookmarks-20261001-031000.json" })
        {
            using var refused = await SendAsync(host, HttpMethod.Post, path);
            var text = await refused.Content.ReadAsStringAsync(Ct);
            Assert.True(refused.StatusCode == HttpStatusCode.Conflict, path + ": " + text);
            using var problem = JsonDocument.Parse(text);
            Assert.Equal(StoreMessages.ReadableFileInPlace("bookmarks.json"), problem.RootElement.GetProperty("detail").GetString());
            Assert.Equal(good, await File.ReadAllBytesAsync(StoreFile, Ct));
            Assert.Empty(Directory.GetFiles(folder, "bookmarks.json.corrupt-*"));
        }

        Assert.True((await GetJsonAsync(host, "/Bookmarks/Admin/Snapshots")).GetProperty("Recoverable").GetBoolean());
    }

    [Fact]
    public async Task UnreadableUserMarks_DiagnosticsSayIt_RemapAndImportByNameAre409_AndAnAdminStartsTheListEmpty()
    {
        // Found in testing: an unreadable kit-users.json (the list of users deleted on this server)
        // had no way out but stopping the server, Diagnostics' Users entry said nothing, and while it was read-only the list
        // was empty, so Remap by name could give a deleted user's bookmarks to a new user of the same name.
        var folder = Path.GetDirectoryName(StoreFile)!;
        Directory.CreateDirectory(folder);
        var marksFile = Path.Combine(folder, KitUsersDocument.FileName);
        await File.WriteAllTextAsync(marksFile, "{ not json", Ct);
        await using var host = await StartAsync();

        var marks = (await GetJsonAsync(host, "/Bookmarks/Admin/Diagnostics")).GetProperty("Users").GetProperty("Marks");
        Assert.Equal("ReadOnlyDataUnreadable", marks.GetProperty("Status").GetString());
        Assert.True(marks.GetProperty("Recoverable").GetBoolean());
        Assert.Contains("To go on, start empty on the plugin's settings page", marks.GetProperty("Message").GetString(), StringComparison.Ordinal);

        using (var remap = await SendAsync(host, HttpMethod.Post, "/Bookmarks/Admin/Users/Remap?by=Name&dryRun=true"))
        {
            var text = await remap.Content.ReadAsStringAsync(Ct);
            Assert.True(remap.StatusCode == HttpStatusCode.Conflict, text);
            using var problem = JsonDocument.Parse(text);
            Assert.StartsWith("Remap by name is refused: the list of users deleted on this server (kit-users.json) can't be read", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }

        using var export = await SendAsync(host, HttpMethod.Get, "/Bookmarks/Admin/Export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var envelope = await export.Content.ReadAsStringAsync(Ct);
        using (var byName = await SendAsync(host, HttpMethod.Post, "/Bookmarks/Admin/Import?mapUsersBy=Name&dryRun=true", envelope))
        {
            var text = await byName.Content.ReadAsStringAsync(Ct);
            Assert.True(byName.StatusCode == HttpStatusCode.Conflict, text);
            using var problem = JsonDocument.Parse(text);
            Assert.StartsWith("An import that maps users by name is refused", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }

        using (var byId = await SendAsync(host, HttpMethod.Post, "/Bookmarks/Admin/Import?mapUsersBy=Id&dryRun=true", envelope))
        {
            Assert.True(byId.StatusCode == HttpStatusCode.OK, await byId.Content.ReadAsStringAsync(Ct));
        }

        using var started = await SendAsync(host, HttpMethod.Post, "/Bookmarks/Admin/Users/Marks/StartEmpty");
        var body = await started.Content.ReadAsStringAsync(Ct);
        Assert.True(started.StatusCode == HttpStatusCode.OK, body);
        var moved = Assert.Single(Directory.GetFiles(folder, KitUsersDocument.FileName + ".corrupt-*"));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(moved, Ct));
        using (var result = JsonDocument.Parse(body))
        {
            Assert.True(result.RootElement.GetProperty("StartedEmpty").GetBoolean());
            Assert.Equal([Path.GetFileName(moved)], result.RootElement.GetProperty("MovedAside").EnumerateArray().Select(e => e.GetString()));
        }

        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.StartsWith("An admin (" + AdminId.ToString("D") + ") started the list of users deleted on this server (kit-users.json) empty", StringComparison.Ordinal));
        Assert.False((await GetJsonAsync(host, "/Bookmarks/Admin/Diagnostics")).GetProperty("Users").GetProperty("Marks").GetProperty("Recoverable").GetBoolean());
        using (var again = await SendAsync(host, HttpMethod.Post, "/Bookmarks/Admin/Users/Remap?by=Name&dryRun=true"))
        {
            Assert.True(again.StatusCode == HttpStatusCode.OK, await again.Content.ReadAsStringAsync(Ct));
        }
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _data.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task<JsonElement> GetJsonAsync(WebHostHarness host, string path)
    {
        using var response = await SendAsync(host, HttpMethod.Get, path);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task AssertUnavailableAsync(WebHostHarness host, HttpMethod method, string path, string message, string? json = null)
    {
        using var response = await SendAsync(host, method, path, json);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable, $"{method} {path}: {response.StatusCode} {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(503, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(message, problem.RootElement.GetProperty("detail").GetString());
    }

    private static Task<HttpResponseMessage> SendAsync(WebHostHarness host, HttpMethod method, string path, string? json = null)
        => host.SendAsync(method, path, request =>
        {
            request.Headers.Add(JellyfinAuth.CallerHeader, "admin");
            request.Headers.Add(JellyfinAuth.UserIdHeader, AdminId.ToString("N"));
            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
        });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(20, Ct);
        }
    }

    private Task<WebHostHarness> StartAsync()
    {
        // The plugin's own registrations for everything this touches; Jellyfin's services are mocks.
        var applicationHost = new Mock<IServerApplicationHost>();
        applicationHost.SetupGet(h => h.ApplicationVersion).Returns(new Version(10, 11, 5));
        applicationHost.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, applicationHost.Object);
        Type[] own =
        [
            typeof(KitOptions), typeof(KitAbiStatus), typeof(KitPaths), typeof(KitPageHosts), typeof(ClientHealthRegistry), typeof(KitDiagnostics),
            typeof(IKitDiagnostics), typeof(IKitDiagnosticsSource), typeof(BookmarkPaths), typeof(IBookmarkStore), typeof(BackgroundWriteGate),
            typeof(ClipExtractionService), typeof(IClipExtractionService), typeof(ClipFileOps), typeof(PartialFileJanitor), typeof(IBookmarkManager), typeof(IBookmarkQuery), typeof(IBookmarkPreferences),
            typeof(ClipStorageGuard), typeof(IDriveSpaceProbe),

            // The admin actions on records of users and videos that are gone, over the kit's reconciler and its owners.
            typeof(IBookmarkRecordsAdmin), typeof(UserReconciler), typeof(KitUserMarks), typeof(KitComponent<IUserDataOwner>),
            typeof(BookmarkUserDataOwner), typeof(UserDataDemoOwner),

            // The Jellyfin Enhanced import, over the kit's reader.
            typeof(Jellyfin.Plugin.Bookmarks.Import.JellyfinEnhancedBookmarkImporter), typeof(Jellyfin.Plugin.Bookmarks.Import.IBookmarkJeImport),
            typeof(Jellyfin.Plugin.Kit.Interop.JellyfinEnhancedReader),

            // The settings page's tools: export, import, snapshots and restore, over the kit's.
            typeof(IBookmarkAdminTools), typeof(Jellyfin.Plugin.Kit.Interop.JellyfinEnhancedDiagnostics), typeof(Jellyfin.Plugin.Kit.Interop.ShortcutPolicy),

            // The My Clips page's service, its list, the two caches and the kit's LibraryAccess.
            typeof(IBookmarkClips), typeof(ClipQueryService), typeof(ClipStateCache), typeof(ClipLibraryLocations), typeof(LibraryAccess),

            // The kit's library-change monitor and verify sweep, hosted services like the worker, with the demo consumers and
            // records, and the identity service.
            .. TestData.LibraryServiceTypes
        ];

        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_data.Path);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.Combine(_data.Path, "config"));
        var loggers = new Mock<ILoggerFactory>();
        loggers.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(_logger);
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUserById(AdminId)).Returns(_admin);
        users.Setup(m => m.GetUserById(ViewerId)).Returns(_viewer);
        users.SetupGet(m => m.Users).Returns([_admin]);
        var library = new Mock<ILibraryManager>();
        library.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>())).Returns([]);
        library.Setup(m => m.GetItemById<Video>(It.IsAny<Guid>(), It.IsAny<User?>()))
            .Returns((Guid id, User? _) => new MediaBrowser.Controller.Entities.Movies.Movie { Id = id, Name = "Long Test Movie", ProductionYear = 2021, RunTimeTicks = 3000 * TestData.OneSecond, Path = @"C:\media\Long Test Movie (2021).mkv" });

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
                services.AddSingleton<IFfmpegRunner>(_runner);
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
            .WithMvc(typeof(BookmarksController), typeof(BookmarksAdminController), typeof(BookmarksClipsController))
            .StartAsync();
    }

    /// <summary>ffmpeg that only counts: the worker must never get this far.</summary>
    private sealed class CountingRunner : IFfmpegRunner
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public Task RunAsync(string encoderPath, IReadOnlyList<string> arguments, TimeSpan duration, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            return Task.CompletedTask;
        }
    }
}
