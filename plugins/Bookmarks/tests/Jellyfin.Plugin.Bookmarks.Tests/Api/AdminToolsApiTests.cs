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
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Api;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tasks;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Interop;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.Kit.Web.Pages;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Api;

/// <summary>
/// The settings page's admin tools behind Jellyfin's web pipeline with
/// the registrator's own services: <c>GET /Bookmarks/Admin/Export</c> and <c>POST /Bookmarks/Admin/Import</c> (a dry run by
/// default; Merge and Replace; the archive; refused while clips are being made), <c>GET /Bookmarks/Admin/Snapshots</c> and
/// <c>POST /Bookmarks/Admin/Snapshots/Restore</c> (the clip folders recorded since kept), <c>POST
/// /Bookmarks/Admin/Maintenance/Run</c> (202) and <c>GET /Bookmarks/Admin/Shortcuts/Check</c> (the kit's shortcut policy), and
/// Diagnostics' <c>BookmarkCounts</c>; admins only.
/// </summary>
public sealed class AdminToolsApiTests : IAsyncLifetime
{
    private static readonly DateTime Created = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly TempDirectory _data = new();
    private readonly List<User> _server = [];
    private readonly User _admin = User("admin");
    private readonly User _alex = User("alex");
    private Mock<ITaskManager>? _tasks = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ClipRoot => Path.Combine(_data.Path, "bookmarks", "clips");

    public ValueTask InitializeAsync()
    {
        _server.AddRange([_admin, _alex]);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _data.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Export_IsTheKitsEnvelope_WithEveryUsersBookmarks_TheirNames_AndTheSettings_AsAnAttachmentNotCached()
    {
        await using var host = await StartAsync();
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        await store.AddAsync(Bookmark(_admin.Id, "Bank job"), Ct, _admin);
        await store.AddAsync(Bookmark(_alex.Id, "Tears in rain"), Ct, _alex);
        await store.SetPreferencesAsync(_alex.Id, new BookmarkUserPreferences { AskForName = true }, Ct);

        using var response = await SendAsync(host, HttpMethod.Get, "/Bookmarks/Admin/Export");
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.StartsWith("bookmarks-export-", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'), StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var envelope = JsonDocument.Parse(body);
        var root = envelope.RootElement;
        Assert.Equal("jellyfin-plugin-export/1", root.GetProperty("Format").GetString());
        Assert.Equal("Bookmarks", root.GetProperty("Plugin").GetString());
        Assert.Equal(JellyfinLine.VersionOf(typeof(Plugin)), root.GetProperty("PluginVersion").GetString());
        Assert.Equal(2, root.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(new[] { "admin", "alex" }, root.GetProperty("Users").EnumerateArray().Select(u => u.GetProperty("Name").GetString()).Order());
        Assert.Equal(2, root.GetProperty("Data").GetProperty("Bookmarks").GetArrayLength());
        Assert.True(root.GetProperty("Data").GetProperty("UserPreferences").TryGetProperty(_alex.Id.ToString("N"), out _));
        Assert.Equal(2048, root.GetProperty("Settings").GetProperty("MinFreeSpaceMegabytes").GetInt32());
        Assert.Equal(0, root.GetProperty("Archive").GetArrayLength());
    }

    [Fact]
    public async Task AnExport_ImportedIntoAnotherServer_IsADryRunFirst_ThenAddsTheBookmarks_AfterASnapshot_ByUserName()
    {
        string export;
        await using (var source = await StartAsync())
        {
            var store = source.Services.GetRequiredService<IBookmarkStore>();
            await store.AddAsync(Bookmark(_admin.Id, "Bank job"), Ct, _admin);
            await store.AddAsync(Bookmark(_alex.Id, "Tears in rain"), Ct, _alex);
            await store.AddAsync(Bookmark(_alex.Id, "Hoth"), Ct, _alex);
            using var response = await SendAsync(source, HttpMethod.Get, "/Bookmarks/Admin/Export");
            export = await response.Content.ReadAsStringAsync(Ct);
        }

        // Another server: other data, the same names under other ids.
        using var other = new TempDirectory();
        var admin2 = User("admin");
        var alex2 = User("Alex");
        _server.Clear();
        _server.AddRange([admin2, alex2]);
        await using var host = await StartAsync(other.Path);
        var target = host.Services.GetRequiredService<IBookmarkStore>();

        var byId = await PostJsonAsync(host, "/Bookmarks/Admin/Import", export);
        Assert.True(byId.GetProperty("DryRun").GetBoolean());
        Assert.Equal(0, byId.GetProperty("Records").GetProperty("Added").GetInt32());
        Assert.Equal(3, byId.GetProperty("Records").GetProperty("SkippedUnknownUser").GetInt32());

        var dryRun = await PostJsonAsync(host, "/Bookmarks/Admin/Import?mapUsersBy=Name", export);
        Assert.True(dryRun.GetProperty("DryRun").GetBoolean());
        Assert.Equal(3, dryRun.GetProperty("Records").GetProperty("Added").GetInt32());
        Assert.Equal(0, dryRun.GetProperty("ClipsBeingMade").GetInt32());
        Assert.Empty(target.GetAll());

        var applied = await PostJsonAsync(host, "/Bookmarks/Admin/Import?mapUsersBy=Name&dryRun=false&mode=Merge", export);
        Assert.False(applied.GetProperty("DryRun").GetBoolean());
        Assert.Equal(3, applied.GetProperty("Records").GetProperty("Added").GetInt32());
        Assert.StartsWith("bookmarks-import-", applied.GetProperty("SafetySnapshot").GetString(), StringComparison.Ordinal);
        Assert.Equal(new[] { admin2.Id, alex2.Id, alex2.Id }.Order(), target.GetAll().Select(b => b.UserId).Order());
        Assert.Equal("Alex", target.KnownUsers.NameOf(alex2.Id));

        // Again: nothing new.
        var again = await PostJsonAsync(host, "/Bookmarks/Admin/Import?mapUsersBy=Name&dryRun=false", export);
        Assert.Equal(0, again.GetProperty("Records").GetProperty("Added").GetInt32());
        Assert.Equal(3, again.GetProperty("Records").GetProperty("Unchanged").GetInt32());
        Assert.Equal(3, target.GetAll().Count);
    }

    [Fact]
    public async Task Replace_MakesTheBookmarksTheFiles_AndTheBadFilesAndOptionsAreProblemDetails()
    {
        await using var host = await StartAsync();
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var kept = Bookmark(_admin.Id, "Kept");
        await store.AddAsync(kept, Ct, _admin);
        using (var response = await SendAsync(host, HttpMethod.Get, "/Bookmarks/Admin/Export"))
        {
            var export = await response.Content.ReadAsStringAsync(Ct);
            var added = Bookmark(_alex.Id, "Added later");
            await store.AddAsync(added, Ct, _alex);

            var replaced = await PostJsonAsync(host, "/Bookmarks/Admin/Import?mode=Replace&dryRun=false", export);
            Assert.Equal(1, replaced.GetProperty("Records").GetProperty("Removed").GetInt32());
            Assert.Equal([kept.Id], store.GetAll().Select(b => b.Id));
            Assert.Single(Directory.GetFiles(Path.Combine(_data.Path, "bookmarks", "snapshots", "pre-op"), "bookmarks-import-*"));
        }

        foreach (var (path, json, status) in new (string, string, HttpStatusCode)[]
        {
            ("/Bookmarks/Admin/Import", "{\"not\":\"an export\"}", HttpStatusCode.BadRequest),
            ("/Bookmarks/Admin/Import?mode=Sideways", Envelope([]), HttpStatusCode.BadRequest),
            ("/Bookmarks/Admin/Import", Envelope([]).Replace("\"SchemaVersion\": 2", "\"SchemaVersion\": 3", StringComparison.Ordinal), HttpStatusCode.Conflict),
        })
        {
            using var refused = await SendAsync(host, HttpMethod.Post, path, json);
            var body = await refused.Content.ReadAsStringAsync(Ct);
            Assert.True(refused.StatusCode == status, $"{path}: {refused.StatusCode} {body}");
            Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task AnImportThatWrites_IsRefusedWhileAClipIsBeingMade_AndItsDryRunSaysSo()
    {
        await using var host = await StartAsync();
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var queued = Bookmark(_admin.Id, "Queued") with { EndTicks = 1_200_000_000, Clip = new ClipInfo { Status = BookmarkClipStatus.Queued, Mode = BookmarkClipMode.Fast, StartTicks = 600_000_000, EndTicks = 1_200_000_000, RequestedAt = Created } };
        await store.AddAsync(queued, Ct, _admin);
        var envelope = Envelope([Bookmark(_alex.Id, "From the file")]);

        var dryRun = await PostJsonAsync(host, "/Bookmarks/Admin/Import", envelope);
        Assert.Equal(1, dryRun.GetProperty("ClipsBeingMade").GetInt32());
        Assert.Equal(1, dryRun.GetProperty("Records").GetProperty("Added").GetInt32());

        using var refused = await SendAsync(host, HttpMethod.Post, "/Bookmarks/Admin/Import?dryRun=false", envelope);
        var body = await refused.Content.ReadAsStringAsync(Ct);
        Assert.True(refused.StatusCode == HttpStatusCode.Conflict, body);
        Assert.Contains("Clips are being made right now.", body, StringComparison.Ordinal);
        Assert.Equal([queued.Id], store.GetAll().Select(b => b.Id));

        // Refused before the import's snapshot: none taken, so none holding the job, none rotated out.
        Assert.False(Directory.Exists(Path.Combine(_data.Path, "bookmarks", "snapshots", "pre-op")));

        // Done: the import goes through.
        await store.UpdateAsync(queued.Id, b => b with { Clip = b.Clip! with { Status = BookmarkClipStatus.Cancelled } }, Ct);
        var applied = await PostJsonAsync(host, "/Bookmarks/Admin/Import?dryRun=false", envelope);
        Assert.Equal(1, applied.GetProperty("Records").GetProperty("Added").GetInt32());
        Assert.Equal(2, store.GetAll().Count);
    }

    [Fact]
    public async Task TheArchive_IsExported_AndAnImportAddsItsBookmarks_ToThisServersArchive_Once()
    {
        await using var host = await StartAsync();
        var detached = host.Services.GetRequiredService<DetachedRecords>();
        var owner = host.Services.GetRequiredService<BookmarkIdentityService>();
        var archived = Bookmark(_alex.Id, "Gone for good") with { DetachedUtc = Created.AddDays(1) };
        var withClip = Bookmark(_alex.Id, "Has a clip") with { DetachedUtc = Created.AddDays(1), Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = Path.Combine(ClipRoot, "x.mkv"), RequestedAt = Created } };
        var envelope = Envelope([], [archived, withClip]);

        var dryRun = await PostJsonAsync(host, "/Bookmarks/Admin/Import", envelope);
        Assert.Equal(2, dryRun.GetProperty("Archive").GetProperty("Records").GetInt32());
        Assert.Equal(1, dryRun.GetProperty("Archive").GetProperty("Imported").GetInt32());
        Assert.Contains("1 aren't archived bookmarks this version can take.", dryRun.GetProperty("Archive").GetProperty("Note").GetString(), StringComparison.Ordinal);
        Assert.Empty(detached.ArchiveOf(owner).Read().Records);

        var applied = await PostJsonAsync(host, "/Bookmarks/Admin/Import?dryRun=false", envelope);
        Assert.Equal(1, applied.GetProperty("Archive").GetProperty("Imported").GetInt32());
        var record = Assert.Single(detached.ArchiveOf(owner).Read().Records);
        Assert.Equal(archived.Id.ToString("D"), record.Id);
        Assert.Equal(_alex.Id, record.UserId);

        // In the next export, and not twice after a second import.
        using (var response = await SendAsync(host, HttpMethod.Get, "/Bookmarks/Admin/Export"))
        {
            using var export = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(archived.Id.ToString("D"), Assert.Single(export.RootElement.GetProperty("Archive").EnumerateArray()).GetProperty("Id").GetString());
            Assert.Contains("alex", export.RootElement.GetProperty("Users").EnumerateArray().Select(u => u.GetProperty("Name").GetString()));
        }

        var again = await PostJsonAsync(host, "/Bookmarks/Admin/Import?dryRun=false", envelope);
        Assert.Equal(0, again.GetProperty("Archive").GetProperty("Imported").GetInt32());
        Assert.Single(detached.ArchiveOf(owner).Read().Records);
    }

    [Fact]
    public async Task AnExportsArchivedMissingClip_CarriesItsReasonAndItsTime_AndAnotherServersImportKeepsBoth()
    {
        // Found in testing: the export wrote the record alone, and an import took ClipMissing back only because a deleted bookmark
        // with a "missing since" looks like one. The kit's export carries the reason and when it began now. Here the archive
        // line's time isn't the record's mark, so the time the other server keeps can only be the export's.
        var since = Created.AddDays(11);
        var record = Bookmark(_alex.Id, "Bank job") with
        {
            Note = "the kept clip's note",
            DeletedUtc = Created.AddDays(2),
            Clip = new ClipInfo
            {
                Status = BookmarkClipStatus.Completed, Mode = BookmarkClipMode.Fast, RequestedAt = Created, CompletedAt = Created,
                FilePath = Path.Combine(ClipRoot, "alex", "Heat (1995) - Bank job [00.01.00-00.01.30].mkv"), FileSize = 4, MissingSinceUtc = Created.AddDays(10)
            }
        };
        string exported;
        await using (var host = await StartAsync())
        {
            var detached = host.Services.GetRequiredService<DetachedRecords>();
            detached.ArchiveOf(host.Services.GetRequiredService<BookmarkIdentityService>()).Append([BookmarkIdentityService.ToArchived(record, MissingClipSweep.ArchiveReason, since)]);
            using var response = await SendAsync(host, HttpMethod.Get, "/Bookmarks/Admin/Export");
            exported = await response.Content.ReadAsStringAsync(Ct);
        }

        var entry = Assert.Single(JsonNode.Parse(exported)!["Archive"]!.AsArray())!.AsObject();
        Assert.Equal(("ClipMissing", since), (entry[DetachedRecords.ExportReasonProperty]!.GetValue<string>(), entry[DetachedRecords.ExportReasonSinceProperty]!.GetValue<DateTime>()));
        Assert.Equal(record.Id.ToString("D"), entry["Id"]!.GetValue<string>());

        // Another server (its own data folder, the same users) imports it.
        using var otherData = new TempDirectory();
        await using var other = await StartAsync(otherData.Path);
        await PostJsonAsync(other, "/Bookmarks/Admin/Import?dryRun=false", exported);

        var archived = Assert.Single(other.Services.GetRequiredService<DetachedRecords>().ArchiveOf(other.Services.GetRequiredService<BookmarkIdentityService>()).Read().Records);
        Assert.Equal((record.Id.ToString("D"), MissingClipSweep.ArchiveReason, since), (archived.Id, archived.Reason, archived.DetachedUtc));
        Assert.False(archived.Record.ContainsKey(DetachedRecords.ExportReasonProperty) || archived.Record.ContainsKey(DetachedRecords.ExportReasonSinceProperty));
        Assert.False(archived.Record["Clip"]!.AsObject().ContainsKey("FilePath"));
        Assert.Equal("the kept clip's note", archived.Record["Note"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConfirmingAClipFolder_IsADryRunByDefault_ThenMarksItsGoneClips_AfterASnapshot_AndAnUnknownIdIs404()
    {
        // The Diagnostics row's "This is the clip folder; its clips are gone", by the folder's RootId.
        // Here a folder whose clips were deleted by hand with its marker (as a 1.1 folder emptied by hand has none): the dry
        // run counts the clip that will be marked missing; the confirmation gives the folder its marker and marks it.
        Directory.CreateDirectory(ClipRoot);
        await using var host = await StartAsync();
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        await store.AddClipRootAsync(ClipRoot, Ct);
        var record = Bookmark(_alex.Id, "Bank job") with
        {
            Clip = new ClipInfo
            {
                Status = BookmarkClipStatus.Completed, Mode = BookmarkClipMode.Fast, RequestedAt = Created, CompletedAt = Created,
                FilePath = Path.Combine(ClipRoot, "alex", "Heat (1995) - Bank job [00.01.00-00.01.30].mkv"), FileSize = 4
            }
        };
        await store.AddAsync(record, Ct);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ClipRoot));
        var rootId = ClipRootPaths.RootId(root);

        var dry = await PostJsonAsync(host, $"/Bookmarks/Admin/ClipFolders/{rootId}/Confirm");

        Assert.Equal(
            (true, root, true, 1, 0, 1),
            (dry.GetProperty("DryRun").GetBoolean(), dry.GetProperty("Folder").GetString(), dry.GetProperty("Existed").GetBoolean(), dry.GetProperty("Clips").GetInt32(), dry.GetProperty("Present").GetInt32(), dry.GetProperty("Marked").GetInt32()));
        Assert.Null(store.Get(record.Id)!.Clip!.MissingSinceUtc);
        Assert.False(File.Exists(ClipRootMarker.PathIn(root)));

        var done = await PostJsonAsync(host, $"/Bookmarks/Admin/ClipFolders/{rootId}/Confirm?dryRun=false");

        Assert.Equal((false, "Written", 1), (done.GetProperty("DryRun").GetBoolean(), done.GetProperty("Marker").GetString(), done.GetProperty("Marked").GetInt32()));
        Assert.NotNull(store.Get(record.Id)!.Clip!.MissingSinceUtc);
        Assert.Equal(store.GetClipRootToken(root), ClipFileSupport.TokenOf(root));
        var preOp = Path.Combine(_data.Path, "bookmarks", "snapshots", SnapshotOperations.Folder);
        var snapshots = Directory.GetFiles(preOp).Where(f => Path.GetFileName(f).StartsWith("bookmarks-" + SnapshotOperations.Other + "-", StringComparison.Ordinal)).ToList();
        Assert.Single(snapshots);

        // An id no recorded folder has: 404 before any snapshot is taken.
        using var unknown = await SendAsync(host, HttpMethod.Post, "/Bookmarks/Admin/ClipFolders/0123456789ab/Confirm?dryRun=false");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(snapshots, Directory.GetFiles(preOp).Where(f => Path.GetFileName(f).StartsWith("bookmarks-" + SnapshotOperations.Other + "-", StringComparison.Ordinal)).ToList());
    }

    [Fact]
    public async Task AnArchivedMissingClip_ComesIntoThisServersArchive_WithItsReason_WithoutItsFilesPath_AndIsExportedAgain()
    {
        // The missing-clip sweep archives a deleted bookmark whose clip file has been gone longer than the retention
        // (ClipMissing); an export carries it in its Archive like every archived record, and an import takes it back with its
        // reason and its name and note, without the file's path (an export's clip paths are never trusted). A deleted
        // bookmark that isn't one (no missing mark) is still left out. This envelope is an export made by 1.2.0 before the fix,
        // whose entries carry no reason: a deleted bookmark with a "missing since" is taken as one archived for its missing
        // clip (the fallback); this server's export carries the reason again.
        await using var host = await StartAsync();
        var detached = host.Services.GetRequiredService<DetachedRecords>();
        var owner = host.Services.GetRequiredService<BookmarkIdentityService>();
        var missingSince = Created.AddDays(10);
        var missing = Bookmark(_alex.Id, "Bank job") with
        {
            Note = "the kept clip's note",
            DeletedUtc = Created.AddDays(2),
            Clip = new ClipInfo
            {
                Status = BookmarkClipStatus.Completed, Mode = BookmarkClipMode.Fast, RequestedAt = Created, CompletedAt = Created,
                FilePath = @"D:\elsewhere\alex\Heat (1995) - Bank job [00.01.00-00.01.30].mkv", FileSize = 4, FileStartTicks = 600_000_000, FileEndTicks = 900_000_000,
                MissingSinceUtc = missingSince
            }
        };
        var tombstoneWithFile = Bookmark(_alex.Id, "Kept, not missing") with
        {
            DeletedUtc = Created.AddDays(2),
            Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = Path.Combine(ClipRoot, "alex", "kept.mkv"), RequestedAt = Created }
        };
        var envelope = Envelope([], [missing, tombstoneWithFile]);

        var dryRun = await PostJsonAsync(host, "/Bookmarks/Admin/Import", envelope);
        Assert.Equal((2, 1), (dryRun.GetProperty("Archive").GetProperty("Records").GetInt32(), dryRun.GetProperty("Archive").GetProperty("Imported").GetInt32()));
        Assert.Contains("1 aren't archived bookmarks this version can take.", dryRun.GetProperty("Archive").GetProperty("Note").GetString(), StringComparison.Ordinal);
        Assert.Empty(detached.ArchiveOf(owner).Read().Records);

        await PostJsonAsync(host, "/Bookmarks/Admin/Import?dryRun=false", envelope);
        var archived = Assert.Single(detached.ArchiveOf(owner).Read().Records);
        Assert.Equal((missing.Id.ToString("D"), MissingClipSweep.ArchiveReason, missingSince), (archived.Id, archived.Reason, archived.DetachedUtc));
        Assert.Equal(("Bank job", "the kept clip's note"), (archived.Record["Name"]!.GetValue<string>(), archived.Record["Note"]!.GetValue<string>()));
        var clip = archived.Record["Clip"]!.AsObject();
        Assert.False(clip.ContainsKey("FilePath") || clip.ContainsKey("FileSize") || clip.ContainsKey("FileStartTicks") || clip.ContainsKey("FileEndTicks"));
        Assert.Equal(missingSince, clip["MissingSinceUtc"]!.GetValue<DateTime>());
        Assert.Null(host.Services.GetRequiredService<IBookmarkStore>().Get(missing.Id));

        using var response = await SendAsync(host, HttpMethod.Get, "/Bookmarks/Admin/Export");
        using var export = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var entry = Assert.Single(export.RootElement.GetProperty("Archive").EnumerateArray());
        Assert.Equal(missing.Id.ToString("D"), entry.GetProperty("Id").GetString());
        Assert.Equal(("ClipMissing", missingSince), (entry.GetProperty(DetachedRecords.ExportReasonProperty).GetString(), entry.GetProperty(DetachedRecords.ExportReasonSinceProperty).GetDateTime()));

        // A reason this version doesn't know is left out of an import.
        var unknown = JsonNode.Parse(Envelope([], [missing with { Id = Guid.NewGuid() }]))!;
        unknown["Archive"]![0]![DetachedRecords.ExportReasonProperty] = "SomethingNew";
        var dryUnknown = await PostJsonAsync(host, "/Bookmarks/Admin/Import", unknown.ToJsonString());
        Assert.Equal((1, 0), (dryUnknown.GetProperty("Archive").GetProperty("Records").GetInt32(), dryUnknown.GetProperty("Archive").GetProperty("Imported").GetInt32()));
    }

    [Fact]
    public async Task Snapshots_AreListed_AndRestore_TakesTheSnapshotsBookmarks_AfterOneOfItsOwn_KeepingTheClipFoldersRecordedSince()
    {
        await using var host = await StartAsync();
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var before = Bookmark(_admin.Id, "Before");
        await store.AddAsync(before, Ct, _admin);
        var snapshotFolder = Path.Combine(_data.Path, "bookmarks", "snapshots");
        var daily = Path.GetFileName(await store.Documents.SnapshotAsync(snapshotFolder, 7, Ct))!;
        await store.AddAsync(Bookmark(_admin.Id, "After"), Ct, _admin);
        var laterRoot = Path.Combine(_data.Path, "later-clips");
        Directory.CreateDirectory(laterRoot);
        Assert.True(await store.AddClipRootAsync(laterRoot, Ct));

        var listed = await GetJsonAsync(host, "/Bookmarks/Admin/Snapshots");
        var entry = Assert.Single(listed.GetProperty("Snapshots").EnumerateArray());
        Assert.Equal(daily, entry.GetProperty("Name").GetString());
        Assert.Equal("Daily", entry.GetProperty("Kind").GetString());
        Assert.Equal(2, entry.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(7, listed.GetProperty("SnapshotsToKeep").GetInt32());

        var restored = await PostJsonAsync(host, "/Bookmarks/Admin/Snapshots/Restore?name=" + Uri.EscapeDataString(daily));
        Assert.Equal(daily, restored.GetProperty("RestoredFrom").GetString());
        Assert.StartsWith("bookmarks-restore-", restored.GetProperty("SafetySnapshot").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, restored.GetProperty("Bookmarks").GetInt32());
        Assert.Equal([before.Id], store.GetAll().Select(b => b.Id));
        Assert.Contains(laterRoot, store.ClipRoots, ClipRootPaths.Comparer);

        // The list now has the restore's own snapshot too.
        Assert.Equal(2, (await GetJsonAsync(host, "/Bookmarks/Admin/Snapshots")).GetProperty("Snapshots").GetArrayLength());

        foreach (var (path, status) in new (string, HttpStatusCode)[]
        {
            ("/Bookmarks/Admin/Snapshots/Restore", HttpStatusCode.BadRequest),
            ("/Bookmarks/Admin/Snapshots/Restore?name=bookmarks-19990101-000000.json", HttpStatusCode.NotFound),
        })
        {
            using var refused = await SendAsync(host, HttpMethod.Post, path);
            Assert.Equal(status, refused.StatusCode);
        }
    }

    [Fact]
    public async Task ARestoreFromBeforeAClipsTwoRenames_NamesTheRenamedFile_SoNoFileIsLeftWithoutItsRecord()
    {
        // The snapshot names the clip's first path; the file has been renamed twice since.
        await using var host = await StartAsync();
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var files = host.Services.GetRequiredService<ClipFileOps>();
        var (record, first) = await AddClipAsync(store, "First");
        var daily = Path.GetFileName(await store.Documents.SnapshotAsync(Path.Combine(_data.Path, "bookmarks", "snapshots"), 7, Ct))!;
        await RenameClipAsync(store, files, record.Id, "Second");
        var third = await RenameClipAsync(store, files, record.Id, "Third");

        var restored = await PostJsonAsync(host, "/Bookmarks/Admin/Snapshots/Restore?name=" + Uri.EscapeDataString(daily));

        Assert.Equal(1, restored.GetProperty("ClipsRelinked").GetInt32());
        var stored = store.Get(record.Id)!;
        Assert.Equal(("First", third), (stored.Name, stored.Clip!.FilePath));
        Assert.False(File.Exists(first));
        Assert.Equal([third], ClipFileSupport.FilesBesidesMarker(ClipRoot, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AnImportFromBeforeAClipsRenames_ReplaceOrMerge_NamesTheRenamedFile()
    {
        // An export made before the renames names the clip's first path.
        await using var host = await StartAsync();
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var files = host.Services.GetRequiredService<ClipFileOps>();
        var (record, first) = await AddClipAsync(store, "First");
        var exported = store.Get(record.Id)!;
        await RenameClipAsync(store, files, record.Id, "Second");
        var third = await RenameClipAsync(store, files, record.Id, "Third");

        var replaced = await PostJsonAsync(host, "/Bookmarks/Admin/Import?mode=Replace&dryRun=false", Envelope([exported]));
        Assert.Contains(replaced.GetProperty("Notes").EnumerateArray(), n => n.GetString()!.StartsWith("1 clip(s) named a file that was renamed since the export", StringComparison.Ordinal));
        Assert.Equal(("First", third), (store.Get(record.Id)!.Name, store.Get(record.Id)!.Clip!.FilePath));

        // Merge: the export's copy is newer (an edit made there), so it replaces the stored one, which names the file.
        var newer = exported with { Note = "Edited on the other side", UpdatedAt = DateTime.UtcNow.AddMinutes(5) };
        await PostJsonAsync(host, "/Bookmarks/Admin/Import?mode=Merge&dryRun=false", Envelope([newer]));
        var merged = store.Get(record.Id)!;
        Assert.Equal(("Edited on the other side", third), (merged.Note, merged.Clip!.FilePath));
        Assert.False(File.Exists(first));
        Assert.Equal([third], ClipFileSupport.FilesBesidesMarker(ClipRoot, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Restore_IsRefusedWhileAClipIsBeingMade_AndChangesNothing()
    {
        await using var host = await StartAsync();
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var snapshotFolder = Path.Combine(_data.Path, "bookmarks", "snapshots");
        await store.AddAsync(Bookmark(_admin.Id, "First"), Ct, _admin);
        var daily = Path.GetFileName(await store.Documents.SnapshotAsync(snapshotFolder, 7, Ct))!;
        var running = Bookmark(_admin.Id, "Running") with { EndTicks = 1_200_000_000, Clip = new ClipInfo { Status = BookmarkClipStatus.Running, Mode = BookmarkClipMode.Fast, RequestedAt = Created } };
        await store.AddAsync(running, Ct, _admin);

        using var refused = await SendAsync(host, HttpMethod.Post, "/Bookmarks/Admin/Snapshots/Restore?name=" + daily);
        var body = await refused.Content.ReadAsStringAsync(Ct);
        Assert.True(refused.StatusCode == HttpStatusCode.Conflict, body);
        Assert.Contains("Clips are being made right now.", body, StringComparison.Ordinal);
        Assert.Equal(2, store.GetAll().Count);
        Assert.False(Directory.Exists(Path.Combine(snapshotFolder, "pre-op")));
        Assert.Equal(1, (await GetJsonAsync(host, "/Bookmarks/Admin/Snapshots")).GetProperty("ClipsBeingMade").GetInt32());
    }

    [Fact]
    public async Task ASnapshotTakenWhileClipsWereMade_IsRestoredWithTheirJobsEnded_SoNothingCountsAsBeingMade_AndItsOwnSnapshotRestoresToo()
    {
        // A daily snapshot taken while a clip was being made holds the job Running (or Queued); no clip worker
        // runs it before the next start, so restored as it was it would block every later restore and import (409), and
        // ignore its owner's Cancel and new requests. It is ended as it is restored: Failed, with the earlier file kept.
        await using var host = await StartAsync();
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var snapshotFolder = Path.Combine(_data.Path, "bookmarks", "snapshots");
        var previous = Path.Combine(ClipRoot, "admin", "Heat (1995) - Running [00.01.00-00.02.00].mp4");
        var running = Bookmark(_admin.Id, "Running") with
        {
            EndTicks = 1_200_000_000,
            Clip = new ClipInfo
            {
                Status = BookmarkClipStatus.Running, Mode = BookmarkClipMode.Accurate, StartTicks = 600_000_000, EndTicks = 1_200_000_000, RequestedAt = Created, EstimatedBytes = 1000,
                FilePath = previous, FileSize = 4, FileStartTicks = 600_000_000, FileEndTicks = 1_100_000_000
            }
        };
        var queued = Bookmark(_alex.Id, "Queued") with { EndTicks = 1_200_000_000, Clip = new ClipInfo { Status = BookmarkClipStatus.Queued, Mode = BookmarkClipMode.Fast, StartTicks = 600_000_000, EndTicks = 1_200_000_000, RequestedAt = Created } };
        var tombstone = Bookmark(_alex.Id, "Deleted, clip kept") with
        {
            DeletedUtc = Created.AddDays(1),
            Clip = new ClipInfo { Status = BookmarkClipStatus.Running, Mode = BookmarkClipMode.Accurate, RequestedAt = Created, FilePath = Path.Combine(ClipRoot, "alex", "kept.mkv"), FileSize = 4 }
        };
        var plain = Bookmark(_admin.Id, "No clip");
        await store.UpdateDocumentAsync(d => BookmarkStore.WithBookmarks(d, [running, queued, tombstone, plain]), Ct);
        var daily = Path.GetFileName(await store.Documents.SnapshotAsync(snapshotFolder, 7, Ct))!;
        Assert.Equal(2, (await GetJsonAsync(host, "/Bookmarks/Admin/Snapshots")).GetProperty("ClipsBeingMade").GetInt32());

        // The jobs end as the worker ends them (the owners cancelled); then the snapshot is restored.
        foreach (var id in new[] { running.Id, queued.Id })
        {
            await store.UpdateAsync(id, b => b with { Clip = b.Clip! with { Status = BookmarkClipStatus.Cancelled, CompletedAt = Created.AddHours(1) } }, Ct);
        }

        var restored = await PostJsonAsync(host, "/Bookmarks/Admin/Snapshots/Restore?name=" + Uri.EscapeDataString(daily));
        Assert.Equal(2, restored.GetProperty("ClipJobsEnded").GetInt32());
        Assert.Equal(3, restored.GetProperty("Bookmarks").GetInt32());
        var ended = store.Get(running.Id)!.Clip!;
        Assert.Equal(BookmarkClipStatus.Failed, ended.Status);
        Assert.Equal("The bookmark data was restored from a snapshot taken while this clip was being made. Make it again.", ended.Error);
        Assert.NotNull(ended.CompletedAt);
        Assert.Equal(
            (previous, 4L, 600_000_000L, 1_100_000_000L, BookmarkClipMode.Accurate, Created, 1000L),
            (ended.FilePath, ended.FileSize, ended.FileStartTicks, ended.FileEndTicks, ended.Mode, ended.RequestedAt, ended.EstimatedBytes));
        Assert.Equal(running.UpdatedAt, store.Get(running.Id)!.UpdatedAt);
        Assert.Equal(BookmarkClipStatus.Failed, store.Get(queued.Id)!.Clip!.Status);
        var kept = store.GetAll().Single(b => b.Id == tombstone.Id).Clip!;
        Assert.Equal(BookmarkClipStatus.Completed, kept.Status);
        Assert.Equal(tombstone.Clip!.FilePath, kept.FilePath);
        Assert.Null(store.Get(plain.Id)!.Clip);
        Assert.Equal(0, BookmarkImportAdapter.CountClipsBeingMade(store.GetAll()));
        Assert.Equal(0, (await GetJsonAsync(host, "/Bookmarks/Admin/Snapshots")).GetProperty("ClipsBeingMade").GetInt32());

        // Undoing it through its own snapshot isn't refused; that one held no job.
        var undone = await PostJsonAsync(host, "/Bookmarks/Admin/Snapshots/Restore?name=" + Uri.EscapeDataString(restored.GetProperty("SafetySnapshot").GetString()!));
        Assert.Equal(0, undone.GetProperty("ClipJobsEnded").GetInt32());
        Assert.Equal(BookmarkClipStatus.Cancelled, store.Get(running.Id)!.Clip!.Status);
    }

    [Fact]
    public async Task AnExportsClipJobs_AreImportedEnded_AtTheExportsTime_WithANote_AndTheSameExportImportedAgainChangesNothing()
    {
        // An export made while clips were being made holds their jobs; imported as they were, nothing would
        // run them before the next start. They are ended at the export's time, so a second import finds them unchanged.
        await using var host = await StartAsync();
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var running = Bookmark(_admin.Id, "Running") with { EndTicks = 1_200_000_000, Clip = new ClipInfo { Status = BookmarkClipStatus.Running, Mode = BookmarkClipMode.Accurate, StartTicks = 600_000_000, EndTicks = 1_200_000_000, RequestedAt = Created } };
        var queued = Bookmark(_alex.Id, "Queued") with { EndTicks = 1_200_000_000, Clip = new ClipInfo { Status = BookmarkClipStatus.Queued, Mode = BookmarkClipMode.Fast, StartTicks = 600_000_000, EndTicks = 1_200_000_000, RequestedAt = Created } };
        var envelope = Envelope([running, queued]);

        var dryRun = await PostJsonAsync(host, "/Bookmarks/Admin/Import", envelope);
        Assert.Equal(0, dryRun.GetProperty("ClipsBeingMade").GetInt32());
        var note = Assert.Single(dryRun.GetProperty("Notes").EnumerateArray().Select(n => n.GetString()!), n => n.StartsWith("2 clip(s) were being made when the export was made;", StringComparison.Ordinal));
        Assert.Contains(running.Id.ToString("D"), note, StringComparison.Ordinal);
        Assert.Contains(queued.Id.ToString("D"), note, StringComparison.Ordinal);

        var applied = await PostJsonAsync(host, "/Bookmarks/Admin/Import?dryRun=false", envelope);
        Assert.Equal(2, applied.GetProperty("Records").GetProperty("Added").GetInt32());
        foreach (var id in new[] { running.Id, queued.Id })
        {
            var clip = store.Get(id)!.Clip!;
            Assert.Equal(BookmarkClipStatus.Failed, clip.Status);
            Assert.Equal(BookmarkImportAdapter.ImportedClipJobMessage, clip.Error);
            Assert.Equal(new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc), clip.CompletedAt);
        }

        Assert.Equal(0, (await GetJsonAsync(host, "/Bookmarks/Admin/Snapshots")).GetProperty("ClipsBeingMade").GetInt32());

        // Imported again: not refused, nothing changed, no note.
        var again = await PostJsonAsync(host, "/Bookmarks/Admin/Import?dryRun=false", envelope);
        Assert.Equal(2, again.GetProperty("Records").GetProperty("Unchanged").GetInt32());
        Assert.DoesNotContain(again.GetProperty("Notes").EnumerateArray().Select(n => n.GetString()!), n => n.Contains("being made", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MaintenanceRun_QueuesTheDailyTask_Answering202_And503WithoutTheTaskManager()
    {
        await using (var host = await StartAsync())
        {
            using var response = await SendAsync(host, HttpMethod.Post, "/Bookmarks/Admin/Maintenance/Run");
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Accepted, body);
            Assert.Contains("\"Queued\":true", body, StringComparison.Ordinal);
            _tasks!.Verify(t => t.QueueScheduledTask<BookmarksMaintenanceTask>(), Times.Once);
        }

        _tasks = null;
        await using var without = await StartAsync();
        using var unavailable = await SendAsync(without, HttpMethod.Post, "/Bookmarks/Admin/Maintenance/Run");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
    }

    [Fact]
    public async Task ShortcutCheck_ChecksATypedValue_WithTheKitsPolicy()
    {
        await using var host = await StartAsync();

        var free = await GetJsonAsync(host, "/Bookmarks/Admin/Shortcuts/Check?combo=" + Uri.EscapeDataString("shift+b"));
        Assert.Equal("BookmarkNow", free.GetProperty("Name").GetString());
        Assert.Equal("Shift+B", free.GetProperty("Combo").GetString());
        Assert.True(free.GetProperty("Valid").GetBoolean());
        Assert.False(free.GetProperty("Reserved").GetBoolean());

        var player = await GetJsonAsync(host, "/Bookmarks/Admin/Shortcuts/Check?combo=k");
        Assert.True(player.GetProperty("Reserved").GetBoolean());
        Assert.Equal("K is taken by jellyfin-web's video player, so Bookmark this moment won't work with it. Choose another key.", player.GetProperty("Warning").GetString());

        // Without Jellyfin Enhanced its default keys still count, so B would stop working once JE is installed.
        var jeDefault = await GetJsonAsync(host, "/Bookmarks/Admin/Shortcuts/Check?combo=B");
        Assert.True(jeDefault.GetProperty("Reserved").GetBoolean());
        Assert.EndsWith("would stop working if Jellyfin Enhanced is installed.", jeDefault.GetProperty("Warning").GetString(), StringComparison.Ordinal);

        var invalid = await GetJsonAsync(host, "/Bookmarks/Admin/Shortcuts/Check?combo=" + Uri.EscapeDataString("Shift+"));
        Assert.False(invalid.GetProperty("Valid").GetBoolean());
        var off = await GetJsonAsync(host, "/Bookmarks/Admin/Shortcuts/Check");
        Assert.Equal(string.Empty, off.GetProperty("Combo").GetString());
    }

    [Fact]
    public async Task Diagnostics_CountsEachUsersBookmarks_WithoutTheirText()
    {
        await using var host = await StartAsync();
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        await store.AddAsync(Bookmark(_admin.Id, "Secret name") with { Note = "Secret note" }, Ct, _admin);
        await store.AddAsync(Bookmark(_alex.Id, null) with { EndTicks = 1_200_000_000 }, Ct, _alex);

        var diagnostics = await GetJsonAsync(host, "/Bookmarks/Admin/Diagnostics");
        var counts = diagnostics.GetProperty("BookmarkCounts");
        Assert.Equal(2, counts.GetProperty("Total").GetProperty("Users").GetInt32());
        Assert.Equal(2, counts.GetProperty("Total").GetProperty("Bookmarks").GetInt32());
        var rows = counts.GetProperty("Users").EnumerateArray().ToDictionary(r => r.GetProperty("UserName").GetString()!);
        Assert.Equal(1, rows["admin"].GetProperty("Named").GetInt32());
        Assert.Equal(1, rows["alex"].GetProperty("Ranges").GetInt32());
        Assert.True(counts.GetProperty("FileBytes").GetInt64() > 0);
        Assert.Equal(JsonValueKind.Null, counts.GetProperty("Warning").ValueKind);
        Assert.DoesNotContain("Secret", counts.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Heat", counts.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTools_AreRefusedToUsers()
    {
        await using var host = await StartAsync();

        foreach (var (method, path) in new (HttpMethod, string)[]
        {
            (HttpMethod.Get, "/Bookmarks/Admin/Export"), (HttpMethod.Post, "/Bookmarks/Admin/Import"), (HttpMethod.Get, "/Bookmarks/Admin/Snapshots"),
            (HttpMethod.Post, "/Bookmarks/Admin/Snapshots/Restore?name=x"), (HttpMethod.Post, "/Bookmarks/Admin/Maintenance/Run"),
            (HttpMethod.Get, "/Bookmarks/Admin/Shortcuts/Check?combo=B"), (HttpMethod.Post, "/Bookmarks/Admin/ClipFolders/0123456789ab/Confirm?dryRun=false"),
            (HttpMethod.Post, "/Bookmarks/Admin/Data/StartEmpty"), (HttpMethod.Post, "/Bookmarks/Admin/Users/Marks/StartEmpty"),
        })
        {
            using var refused = await host.SendAsync(method, path, r =>
            {
                r.Headers.Add(JellyfinAuth.CallerHeader, "user");
                r.Headers.Add(JellyfinAuth.UserIdHeader, _alex.Id.ToString("N"));
                if (method == HttpMethod.Post)
                {
                    r.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                }
            });
            Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{path}: {refused.StatusCode}");
            using var anonymous = await host.SendAsync(method, path);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        _tasks!.Verify(t => t.QueueScheduledTask<BookmarksMaintenanceTask>(), Times.Never);
    }

    // The admin's bookmark with a finished Fast clip whose file is in the default clip folder, marked and recorded.
    private async Task<(Bookmark Record, string File)> AddClipAsync(IBookmarkStore store, string name)
    {
        Directory.CreateDirectory(Path.Combine(ClipRoot, "admin"));
        await store.AddClipRootAsync(ClipRoot, Ct);
        await ClipFileSupport.MarkClipFolderAsync(store, ClipRoot);
        var file = Path.Combine(ClipRoot, "admin", ClipFileNaming.BuildFileName("Heat (1995)", name, 60 * TestData.OneSecond, 90 * TestData.OneSecond, BookmarkClipMode.Fast));
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4], Ct);
        var record = Bookmark(_admin.Id, name) with
        {
            Clip = new ClipInfo
            {
                Status = BookmarkClipStatus.Completed,
                Mode = BookmarkClipMode.Fast,
                StartTicks = 60 * TestData.OneSecond,
                EndTicks = 90 * TestData.OneSecond,
                RequestedAt = Created,
                CompletedAt = Created,
                FilePath = file,
                FileSize = 4,
                FileStartTicks = 60 * TestData.OneSecond,
                FileEndTicks = 90 * TestData.OneSecond
            }
        };
        await store.AddAsync(record, Ct, _admin);
        return (store.Get(record.Id)!, file);
    }

    // The real rename of the clip's name and file: the file's new path.
    private async Task<string> RenameClipAsync(IBookmarkStore store, ClipFileOps files, Guid id, string name)
    {
        var renamed = await new ClipFileRenamer(store, files, NullLogger.Instance).RenameAsync(_admin, id, changeName: true, name, renameFile: true, ifUpdatedAt: null, Ct);
        Assert.True(File.Exists(renamed.Clip!.FilePath));
        return renamed.Clip.FilePath!;
    }

    private static User User(string name)
    {
        var user = TestData.CreateUser(name);
        user.Id = Guid.NewGuid();
        return user;
    }

    private static Bookmark Bookmark(Guid userId, string? name)
    {
        var item = Guid.NewGuid();
        return new Bookmark
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ItemId = item,
            StartTicks = 600_000_000,
            Name = name,
            ItemName = "Heat (1995)",
            Identity = new IdentitySnapshot { ItemId = item, Kind = "Movie", Name = "Heat", ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "949" } },
            AttachedUtc = Created,
            CreatedAt = Created,
            UpdatedAt = Created
        };
    }

    // An export envelope of this server's users holding the given bookmarks and archived ones.
    private string Envelope(IReadOnlyList<Bookmark> bookmarks, IReadOnlyList<Bookmark>? archive = null)
    {
        var document = BookmarkStore.WithBookmarks(BookmarkStoreDocument.CreateEmpty(), bookmarks);
        var data = (JsonObject)JsonSerializer.SerializeToNode(document, JsonStoreFormat.Options)!;
        var users = _server.Select(u => new JsonObject { ["Id"] = u.Id.ToString("N"), ["Name"] = u.Username });
        var envelope = new JsonObject
        {
            ["Format"] = "jellyfin-plugin-export/1",
            ["Plugin"] = "Bookmarks",
            ["PluginId"] = Plugin.PluginId,
            ["PluginVersion"] = "1.1.0.0",
            ["SchemaVersion"] = 2,
            ["ExportedUtc"] = "2026-10-07T12:00:00Z",
            ["Users"] = new JsonArray(users.ToArray<JsonNode>()),
            ["Data"] = data,
            ["Archive"] = new JsonArray((archive ?? []).Select(b => (JsonNode)JsonSerializer.SerializeToNode(b, JsonStoreFormat.Options)!).ToArray())
        };
        return envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static async Task<JsonElement> GetJsonAsync(WebHostHarness host, string path)
    {
        using var response = await SendAsync(host, HttpMethod.Get, path);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> PostJsonAsync(WebHostHarness host, string path, string? json = null)
    {
        using var response = await SendAsync(host, HttpMethod.Post, path, json);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static Task<HttpResponseMessage> SendAsync(WebHostHarness host, HttpMethod method, string path, string? json = null)
        => host.SendAsync(method, path, r =>
        {
            r.Headers.Add(JellyfinAuth.CallerHeader, "admin");
            if (json is not null)
            {
                r.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
        });

    private Task<WebHostHarness> StartAsync(string? dataPath = null)
    {
        var applicationHost = new Mock<IServerApplicationHost>();
        applicationHost.SetupGet(h => h.ApplicationVersion).Returns(JellyfinLine.Server);
        applicationHost.SetupGet(h => h.SystemId).Returns("0123456789abcdef0123456789abcdef");
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, applicationHost.Object);
        Type[] own =
        [
            typeof(KitOptions), typeof(KitAbiStatus), typeof(KitPaths), typeof(KitPageHosts), typeof(ClientHealthRegistry), typeof(KitDiagnostics),
            typeof(IKitDiagnostics), typeof(IKitDiagnosticsSource), typeof(BookmarkPaths), typeof(IBookmarkStore), typeof(BackgroundWriteGate), typeof(ClipFileOps), typeof(ClipStateCache),
            typeof(IBookmarkRecordsAdmin), typeof(UserReconciler), typeof(KitUserMarks), typeof(BookmarkUserDataOwner), typeof(DetachedRecords),
            typeof(IdentityResolver), typeof(MoveJournalReader), typeof(BookmarkIdentityService), typeof(IBookmarkAdminTools), typeof(MissingClipSweep),
            typeof(JellyfinEnhancedReader), typeof(JellyfinEnhancedDiagnostics), typeof(ShortcutPolicy)
        ];

        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(dataPath ?? _data.Path);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.Combine(dataPath ?? _data.Path, "config"));
        var users = new Mock<IUserManager>();
        users.SetupUsers(() => _server.ToList());
        users.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => _server.Find(u => u.Id == id));
        var tasks = _tasks;

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
                services.AddSingleton(TestData.ConfigurationProvider(new PluginConfiguration()));
                if (tasks is not null)
                {
                    services.AddSingleton(tasks.Object);
                }
            })
            .WithMvc(typeof(BookmarksAdminController))
            .StartAsync();
    }
}
