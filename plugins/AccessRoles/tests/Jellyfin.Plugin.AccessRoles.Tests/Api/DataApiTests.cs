using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;
using Jellyfin.Plugin.Kit.TestSupport;

namespace Jellyfin.Plugin.AccessRoles.Tests.Api;

/// <summary>
/// The Data tab's API: export, import with its dry run, Merge and Replace, the snapshots and their
/// restore with the sync's hold, and <i>Run maintenance now</i>.
/// </summary>
public sealed class DataApiTests
{
    private const string ImportPath = "/AccessRoles/Admin/Import";
    private const string RestorePath = "/AccessRoles/Admin/Snapshots/Restore";

    [Fact]
    public async Task TheExport_IsTheKitsEnvelope_WithEveryRecordTheUsersNamesAndTheSettings_AndIsntCached()
    {
        await using var api = await StartWithFamilyAsync();

        using var response = await api.SendAsync(HttpMethod.Get, "/AccessRoles/Admin/Export");
        var export = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.StartsWith("accessroles-", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'), StringComparison.Ordinal);
        Assert.Equal(ExportEnvelope.FormatV1, (string?)export["Format"]);
        Assert.Equal(AccessRolesKit.Options.PluginId, Guid.Parse((string)export["PluginId"]!));
        Assert.Equal(1, (int)export["SchemaVersion"]!);
        Assert.Equal("Family", (string?)export["Data"]!["Roles"]![0]!["Name"]);
        Assert.Equal(["kid"], export["Users"]!.AsArray().Select(u => (string?)u!["Name"]));
        Assert.True((bool)export["Settings"]!["EnforcementEnabled"]!);
        Assert.Empty(export["Archive"]!.AsArray());
    }

    [Fact]
    public async Task AnImport_IsADryRunFirst_ThenSavesWithItsToken_AfterASnapshot_Audited()
    {
        await using var api = await StartWithFamilyAsync();
        var export = await ExportAsync(api);
        await api.Store.UpdateAsync(_ => AccessRolesDocument.CreateEmpty(), TestContext.Current.CancellationToken);

        var (dryStatus, dry) = await api.JsonAsync(HttpMethod.Post, ImportPath, body: export);

        Assert.Equal(200, dryStatus);
        Assert.True((bool)dry!["DryRun"]!);
        Assert.Equal(("Merge", "Id", "Export"), ((string?)dry["Mode"], (string?)dry["MapUsersBy"], (string?)dry["Source"]));
        Assert.Equal((1, 1), ((int)dry["Records"]!["Roles"]!["Added"]!, (int)dry["Records"]!["Assignments"]!["Added"]!));
        var kid = Assert.Single(dry["Preview"]!["Users"]!.AsArray());
        Assert.Equal("kid", (string?)kid!["Name"]);
        Assert.Equal(["Collections", "Horror", "Shows"], kid["LibrariesRemoved"]!.AsArray().Select(l => (string?)l!["Name"]).Order(StringComparer.Ordinal));
        Assert.Empty(api.Store.Current.Roles);
        Assert.Empty(Snapshots(api));

        var (saveStatus, saved) = await api.JsonAsync(HttpMethod.Post, ImportPath + "?dryRun=false&ifPreview=" + Uri.EscapeDataString((string)dry["Preview"]!["PreviewToken"]!), body: export);

        Assert.Equal(200, saveStatus);
        Assert.False((bool)saved!["DryRun"]!);
        Assert.Equal("Family", Assert.Single(api.Store.Current.Roles).Name);
        Assert.True(api.Store.Current.Ledger.ContainsKey(User(api, "kid").Id), "The kid's original access is recorded as for any change.");
        var snapshot = Assert.Single(Snapshots(api));
        Assert.Equal((string?)saved["SafetySnapshot"], Path.GetFileName(snapshot));
        Assert.Contains("import", Path.GetFileName(snapshot), StringComparison.Ordinal);
        var applied = Assert.Single(api.Audit.Read(0, 50, null, "ImportApplied", null).Items);
        Assert.Equal("Export", applied.Details!["Source"]!.ToString());
        Assert.Equal(Path.GetFileName(snapshot), applied.Details!["SafetySnapshot"]!.ToString());
        Assert.Single(api.Audit.Read(0, 50, null, "RoleCreated", null).Items);
    }

    [Fact]
    public async Task ASaveWhoseEffectIsntTheDryRuns_Is409Changed_AndLeavesNoSnapshot()
    {
        await using var api = await StartWithFamilyAsync();
        var export = await ExportAsync(api);
        await api.Store.UpdateAsync(_ => AccessRolesDocument.CreateEmpty(), TestContext.Current.CancellationToken);
        var (_, dry) = await api.JsonAsync(HttpMethod.Post, ImportPath, body: export);

        await api.Store.UpdateAsync(d => d with { Roles = [Role(9, "Meanwhile")] }, TestContext.Current.CancellationToken);
        var (status, problem) = await api.JsonAsync(HttpMethod.Post, ImportPath + "?dryRun=false&ifPreview=" + Uri.EscapeDataString((string)dry!["Preview"]!["PreviewToken"]!), body: export);

        Assert.Equal((409, AccessRolesException.Changed), (status, (string?)problem!["Code"]));
        Assert.Equal(["Meanwhile"], api.Store.Current.Roles.Select(r => r.Name));
        Assert.Empty(Snapshots(api));
    }

    [Theory]
    [InlineData("not json", 400, AccessRolesException.NotAnExport)]
    [InlineData("[1,2]", 400, AccessRolesException.NotAnExport)]
    [InlineData("""{"Format":"something-else/1"}""", 400, AccessRolesException.NotAnExport)]
    [InlineData("Format=jellyfin-plugin-export/2", 400, AccessRolesException.ExportFormatNotSupported)]
    [InlineData("PluginId=other", 400, AccessRolesException.NotThisPlugin)]
    [InlineData("SchemaVersion=2", 409, AccessRolesException.NewerSchemaVersion)]
    public async Task AFileThatIsntAnExportOfThisDataAtAVersionThisBuildReads_IsRefusedWithItsCode(string body, int expected, string code)
    {
        await using var api = await StartWithFamilyAsync();
        var export = JsonNode.Parse(await ExportAsync(api))!;
        var before = api.Store.Current;
        var sent = body switch
        {
            "Format=jellyfin-plugin-export/2" => With(export, "Format", "jellyfin-plugin-export/2"),
            "PluginId=other" => With(export, "PluginId", Guid.NewGuid().ToString("D")),
            "SchemaVersion=2" => With(export, "SchemaVersion", 2),
            _ => body
        };

        var (status, problem) = await api.JsonAsync(HttpMethod.Post, ImportPath + "?dryRun=false", body: sent);

        Assert.Equal((expected, code), (status, (string?)problem!["Code"]));
        Assert.Same(before, api.Store.Current);
        Assert.Empty(Snapshots(api));
    }

    [Fact]
    public async Task AnImportThatLocksOutTheLastFullAdministrator_IsRefusedUnlessConfirmed()
    {
        // The LockoutGuard: the export gives the only administrator a role of Movies and Kids.
        await using var api = await StartWithFamilyAsync();
        var admin = User(api, "admin");
        await api.Store.UpdateAsync(d => d with { Assignments = [.. d.Assignments, Assign(Server(admin), [d.Roles[0]])] }, TestContext.Current.CancellationToken);
        var export = await ExportAsync(api);
        await api.Store.UpdateAsync(_ => AccessRolesDocument.CreateEmpty(), TestContext.Current.CancellationToken);

        var (dryStatus, dry) = await api.JsonAsync(HttpMethod.Post, ImportPath + "?mode=Replace", body: export);
        var (saveStatus, refused) = await api.JsonAsync(HttpMethod.Post, ImportPath + "?mode=Replace&dryRun=false", body: export);

        Assert.Equal((409, AccessRolesException.NoFullAdministrator), (dryStatus, (string?)dry!["Code"]));
        Assert.True((bool)dry["Preview"]!["NoFullAdministrator"]!);
        Assert.Equal((409, AccessRolesException.NoFullAdministrator), (saveStatus, (string?)refused!["Code"]));
        Assert.Empty(api.Store.Current.Roles);
        Assert.Empty(Snapshots(api));

        var (confirmed, _) = await api.JsonAsync(HttpMethod.Post, ImportPath + "?mode=Replace&dryRun=false&confirmNoFullAdmin=true", body: export);

        Assert.Equal(200, confirmed);
        Assert.NotNull(api.Store.Current.Assignments.Single(a => a.UserId.Equals(admin.Id)).ConfirmedNoFullAdminUtc);
    }

    [Fact]
    public async Task UsersAndLibrariesNotFoundHere_AreReported_AndNothingIsWrittenForThem()
    {
        await using var api = await StartWithFamilyAsync();
        var ghost = Guid.NewGuid();
        await api.Store.UpdateAsync(
            d => d with
            {
                Assignments = [.. d.Assignments, new RoleAssignment { UserId = ghost, RoleIds = [d.Roles[0].Id], UpdatedUtc = Now }],
                KnownUsers = d.KnownUsers.With(ghost, "ghost")
            },
            TestContext.Current.CancellationToken);
        var export = JsonNode.Parse(await ExportAsync(api))!;
        var anime = Guid.NewGuid();
        export["Data"]!["Roles"]![0]!["Libraries"]!["Refs"]!.AsArray().Add(new JsonObject { ["Id"] = anime.ToString("D"), ["NameAtSelection"] = "Anime", ["Locations"] = new JsonArray(@"X:\Anime") });
        await api.Store.UpdateAsync(_ => AccessRolesDocument.CreateEmpty(), TestContext.Current.CancellationToken);

        var (status, report) = await api.JsonAsync(HttpMethod.Post, ImportPath + "?dryRun=false", body: export.ToJsonString());

        Assert.True(status == 200, report?.ToJsonString());
        var unknown = Assert.Single(report!["Users"]!["Unknown"]!.AsArray());
        Assert.Equal(("ghost", ImportUnknownUser.NotOnThisServer), ((string?)unknown!["Name"], (string?)unknown["Reason"]));
        var unmatched = Assert.Single(report["Libraries"]!["Unmatched"]!.AsArray());
        Assert.Equal(("Anime", "NoSuchLibrary"), ((string?)unmatched!["Name"], (string?)unmatched["Reason"]));
        Assert.Equal([Movies.Id, Kids.Id], api.Store.Current.Roles.Single().Libraries.Refs.Select(r => r.Id));
        Assert.Equal([User(api, "kid").Id], api.Store.Current.Assignments.Select(a => a.UserId));
        Assert.Contains(report["Notes"]!.AsArray(), n => ((string?)n)!.Contains("Unknown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnExportOfAnotherServer_OrOfNone_IsntThisServers_SoItsLedgerIsntTaken()
    {
        // Only this server's own export or snapshot brings ledger entries, for a managed user without one: the envelope's
        // ServerId decides, and another server's, or none, is never this server's.
        await using var api = await StartWithFamilyAsync();
        var token = TestContext.Current.CancellationToken;
        var kid = User(api, "kid");
        await api.Store.UpdateAsync(d => d with { Ledger = new System.Collections.Generic.Dictionary<Guid, LedgerEntry> { [kid.Id] = Ledger(false, Kids) } }, token);
        var export = JsonNode.Parse(await ExportAsync(api))!;
        await api.Store.UpdateAsync(d => d with { Ledger = new System.Collections.Generic.Dictionary<Guid, LedgerEntry>() }, token);

        var (own, ownReport) = await api.JsonAsync(HttpMethod.Post, ImportPath, body: export.ToJsonString());
        var (other, otherReport) = await api.JsonAsync(HttpMethod.Post, ImportPath, body: With(export, "ServerId", JsonValue.Create(Guid.NewGuid().ToString("N"))));
        var (none, noneReport) = await api.JsonAsync(HttpMethod.Post, ImportPath, body: With(export, "ServerId", JsonValue.Create(string.Empty)));

        Assert.Equal((200, 200, 200), (own, other, none));
        static (bool, int) LedgerOf(JsonNode? report) => ((bool)report!["Records"]!["Ledger"]!["SameServer"]!, (int)report["Records"]!["Ledger"]!["Added"]!);
        Assert.Equal((true, 1), LedgerOf(ownReport));
        Assert.Equal((false, 0), LedgerOf(otherReport));
        Assert.Equal((false, 0), LedgerOf(noneReport));
        Assert.Empty(api.Store.Current.Ledger);
    }

    [Fact]
    public async Task AUserThisServerDeleted_IsReportedAsDeletedHere_NotAsOneItNeverHad()
    {
        // The import takes this server's deleted users from the reconciler (UserReconciler.DeletedUserIds): an id Apply
        // deleted-user policy marked is DeletedOnThisServer.
        await using var api = await StartWithFamilyAsync();
        var gone = Guid.NewGuid();
        await api.Store.UpdateAsync(
            d => d with
            {
                Assignments = [.. d.Assignments, new RoleAssignment { UserId = gone, RoleIds = [d.Roles[0].Id], UpdatedUtc = Now }],
                KnownUsers = d.KnownUsers.With(gone, "former")
            },
            TestContext.Current.CancellationToken);
        var export = await ExportAsync(api);
        var (applied, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/ApplyPolicy", body: new { UserIds = new[] { gone } });

        var (status, report) = await api.JsonAsync(HttpMethod.Post, ImportPath, body: export);

        Assert.Equal((200, 200), (applied, status));
        var unknown = Assert.Single(report!["Users"]!["Unknown"]!.AsArray());
        Assert.Equal(("former", ImportUnknownUser.DeletedOnThisServer), ((string?)unknown!["Name"], (string?)unknown["Reason"]));
        Assert.DoesNotContain(api.Store.Current.Assignments, a => a.UserId.Equals(gone));
    }

    [Fact]
    public async Task ARestoreOnAReadOnlyDataFile_Is409_AndWritesNothing()
    {
        // A data file of a newer schema makes the store read-only; a restore of a snapshot that would change it is
        // refused before anything is written: no hold of the sync, no snapshot, the file as it was (409, as
        // an import's).
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var token = TestContext.Current.CancellationToken;
        var paths = api.Web.Services.GetRequiredService<AccessRolesPaths>();
        var file = Path.Combine(api.DataPath, "accessroles", AccessRolesPaths.StoreFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var bytes = System.Text.Encoding.UTF8.GetBytes("""{"Version":2,"Roles":[],"Assignments":[],"Ledger":{}}""");
        await File.WriteAllBytesAsync(file, bytes, token);
        Directory.CreateDirectory(paths.SnapshotsDirectory);
        const string Name = "accessroles-20261018-031000.json";
        await File.WriteAllTextAsync(Path.Combine(paths.SnapshotsDirectory, Name), """{"Version":1,"Roles":[{"Id":"7a1b2c3d-4e5f-4071-8293-a4b5c6d7e8f9","Name":"Guest","Locks":["Profile"],"Libraries":{"Mode":"NotManaged","Refs":[]},"CreatedUtc":"2026-10-01T00:00:00Z","UpdatedUtc":"2026-10-01T00:00:00Z"}],"Assignments":[],"Ledger":{}}""", token);

        var (status, problem) = await api.JsonAsync(HttpMethod.Post, RestorePath + "?dryRun=false&name=" + Name);

        Assert.True(status == 409, status + " " + problem?.ToJsonString());
        Assert.True(api.Store.IsReadOnly);
        Assert.False(File.Exists(paths.RestoreHoldFile));
        Assert.Empty(Snapshots(api));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file, token));
    }

    [Fact]
    public async Task ARestore_IsADryRunFirst_ThenReplacesTheRoles_AndHoldsTheSyncUntilItsDryRunIsSeenAndConfirmed()
    {
        await using var api = await StartWithFamilyAsync();
        var token = TestContext.Current.CancellationToken;
        var paths = api.Web.Services.GetRequiredService<AccessRolesPaths>();
        await api.Store.Documents.PreOpSnapshotAsync(paths.SnapshotsDirectory, SnapshotOperations.Other, token);
        await api.Store.UpdateAsync(d => d with { Roles = [d.Roles[0] with { Name = "Family (changed)", Libraries = d.Roles[0].Libraries with { Refs = [d.Roles[0].Libraries.Refs[0]] } }] }, token);
        var (_, list) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Snapshots");
        var name = (string)list!["Snapshots"]![0]!["Name"]!;
        Assert.False((bool)list["SyncHeld"]!);

        var (dryStatus, dry) = await api.JsonAsync(HttpMethod.Post, RestorePath + "?name=" + Uri.EscapeDataString(name));

        Assert.Equal(200, dryStatus);
        Assert.Equal(("Replace", "Snapshot", name), ((string?)dry!["Mode"], (string?)dry!["Source"], (string?)dry["SourceName"]));
        Assert.Equal(["Kids"], Assert.Single(dry["Preview"]!["Users"]!.AsArray())!["LibrariesAdded"]!.AsArray().Select(l => (string?)l!["Name"]));
        Assert.False(File.Exists(paths.RestoreHoldFile));
        Assert.Equal("Family (changed)", api.Store.Current.Roles.Single().Name);

        var (status, restored) = await api.JsonAsync(HttpMethod.Post, RestorePath + "?dryRun=false&name=" + Uri.EscapeDataString(name) + "&ifPreview=" + Uri.EscapeDataString((string)dry["Preview"]!["PreviewToken"]!));

        Assert.Equal(200, status);
        Assert.True((bool)restored!["SyncHeld"]!);
        Assert.True(File.Exists(paths.RestoreHoldFile));
        Assert.Equal("Family", api.Store.Current.Roles.Single().Name);
        Assert.Contains("restore", (string?)restored["SafetySnapshot"], StringComparison.Ordinal);
        var sync = api.Web.Services.GetRequiredService<LibrarySync>();
        Assert.Equal(SyncReasons.StoreRestored, sync.PauseReason());

        var (early, refused) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Sync?confirmRestore=true");
        Assert.Equal((409, AccessRolesException.DryRunRequired), (early, (string?)refused!["Code"]));
        var (_, plan) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Sync?dryRun=true");
        var (confirmed, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Sync?confirmRestore=true&ifPlan=" + Uri.EscapeDataString((string)plan!["PlanToken"]!));

        Assert.Equal(200, confirmed);
        Assert.False(File.Exists(paths.RestoreHoldFile));
    }

    [Fact]
    public async Task ARefusedRestore_LeavesNoHoldAndNoSnapshot()
    {
        // The snapshot holds no assignment for the kid, who is managed now: the restore takes them out of management, which
        // needs a choice.
        await using var api = await StartWithFamilyAsync();
        var token = TestContext.Current.CancellationToken;
        var paths = api.Web.Services.GetRequiredService<AccessRolesPaths>();
        var managed = api.Store.Current;
        await api.Store.UpdateAsync(_ => AccessRolesDocument.CreateEmpty(), token);
        await api.Store.Documents.PreOpSnapshotAsync(paths.SnapshotsDirectory, SnapshotOperations.Other, token);
        await api.Store.UpdateAsync(_ => managed, token);
        var name = Path.GetFileName(Assert.Single(Snapshots(api)));

        var (status, problem) = await api.JsonAsync(HttpMethod.Post, RestorePath + "?dryRun=false&name=" + Uri.EscapeDataString(name));

        Assert.Equal((400, AccessRolesException.UnmanageChoiceRequired), (status, (string?)problem!["Code"]));
        Assert.False(File.Exists(paths.RestoreHoldFile));
        Assert.Single(Snapshots(api));
        Assert.Equal("Family", api.Store.Current.Roles.Single().Name);
    }

    [Fact]
    public async Task ASnapshotOfANewerVersion_AnUnknownName_AndNoName_AreRefused()
    {
        await using var api = await StartWithFamilyAsync();
        var paths = api.Web.Services.GetRequiredService<AccessRolesPaths>();
        Directory.CreateDirectory(paths.SnapshotsDirectory);
        await File.WriteAllTextAsync(Path.Combine(paths.SnapshotsDirectory, "accessroles-20261018-031000.json"), """{"Version":2,"Roles":[]}""", TestContext.Current.CancellationToken);

        var (newer, problem) = await api.JsonAsync(HttpMethod.Post, RestorePath + "?name=accessroles-20261018-031000.json");
        var (unknown, _) = await api.JsonAsync(HttpMethod.Post, RestorePath + "?name=accessroles-20200101-000000.json");
        var (none, _) = await api.JsonAsync(HttpMethod.Post, RestorePath);

        Assert.Equal((409, AccessRolesException.NewerSchemaVersion), (newer, (string?)problem!["Code"]));
        Assert.Equal((404, 400), (unknown, none));
    }

    [Fact]
    public async Task RunMaintenanceNow_Is503WhileTheServersTasksArentThere()
    {
        await using var api = await StartWithFamilyAsync();

        var (status, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Maintenance/Run");

        Assert.Equal(503, status);
    }

    private static async Task<AccessRolesApi> StartWithFamilyAsync()
    {
        var api = await RolesApiTests.StartWithUsersAsync();
        var kid = Server(User(api, "kid"));
        var family = Role(1, "Family", mode: AccessRolesRoleLibraryMode.Only, refs: [Movies, Kids]);
        await api.Store.UpdateAsync(d => d with { Roles = [family], Assignments = [Assign(kid, [family])], KnownUsers = d.KnownUsers.With(kid.Id, kid.Name) }, TestContext.Current.CancellationToken);
        return api;
    }

    private static async Task<string> ExportAsync(AccessRolesApi api)
    {
        using var response = await api.SendAsync(HttpMethod.Get, "/AccessRoles/Admin/Export");
        Assert.Equal(200, (int)response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static string With(JsonNode export, string name, JsonNode value)
    {
        var copy = export.DeepClone();
        copy[name] = value;
        return copy.ToJsonString();
    }

    private static string[] Snapshots(AccessRolesApi api)
    {
        var directory = Path.Combine(api.Web.Services.GetRequiredService<AccessRolesPaths>().SnapshotsDirectory, SnapshotOperations.Folder);
        return Directory.Exists(directory) ? Directory.GetFiles(directory) : [];
    }

    private static Jellyfin.Database.Implementations.Entities.User User(AccessRolesApi api, string name) => api.UserManager.Object.AllUsers().Single(u => u.Username == name);

    private static Jellyfin.Plugin.AccessRoles.Access.ServerUser Server(Jellyfin.Database.Implementations.Entities.User user) => new(user.Id, user.Username, false, false, false, AllFolders);
}
