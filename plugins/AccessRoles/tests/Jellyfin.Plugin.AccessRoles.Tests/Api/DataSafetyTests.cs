using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;
using Jellyfin.Plugin.Kit.TestSupport;

namespace Jellyfin.Plugin.AccessRoles.Tests.Api;

/// <summary>
/// What the pre-1.0 review of 0.9.15.0 found in the Data tab's writes, as tests of the safe behaviour (0.9.16.0): a restore
/// keeps its hold of the sync once it may have saved, whatever happens to the request; an import never widens access, takes
/// only roles the roles API would take, and is refused quickly when it is too large; the restore's confirmation is the
/// plan the admin saw.
/// </summary>
public sealed class DataSafetyTests
{
    private const string ImportPath = "/AccessRoles/Admin/Import";
    private const string RestorePath = "/AccessRoles/Admin/Snapshots/Restore";

    [Fact]
    public async Task ARestoreWhoseRequestIsCancelledAfterTheSave_StaysHeld_AndAnswersWhatItSaved()
    {
        // The client goes away while the store saves (Changed is raised right after the save, before the
        // audit). The restore is saved, so it must answer so, held, with its audit lines.
        await using var api = await StartWithFamilyAsync();
        var token = TestContext.Current.CancellationToken;
        var paths = api.Web.Services.GetRequiredService<AccessRolesPaths>();
        var name = await SnapshotThenChangeAsync(api, token);
        var admin = api.Web.Services.GetRequiredService<IAccessRolesAdmin>();
        using var cts = new CancellationTokenSource();
        api.Store.Changed += (_, _) => cts.Cancel();

        var result = await admin.RestoreSnapshotAsync(name, new AccessRolesWriteOptions { DryRun = false }, new ClaimsPrincipal(new ClaimsIdentity()), cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal("Family", api.Store.Current.Roles.Single().Name);
        Assert.True(File.Exists(paths.RestoreHoldFile), "the restored data is saved, so the sync is held");
        Assert.False(result.DryRun);
        Assert.True(result.SyncHeld);
        Assert.NotEmpty(result.AuditSeq);
        Assert.DoesNotContain(AccessRolesAdminService.AuditNotWritten, result.Notes);
    }

    [Fact]
    public async Task ARoleSaveWhoseRequestIsCancelledAfterTheSave_AnswersWhatItSaved_WithItsAuditLine()
    {
        await using var api = await StartWithFamilyAsync();
        var admin = api.Web.Services.GetRequiredService<IAccessRolesAdmin>();
        using var cts = new CancellationTokenSource();
        api.Store.Changed += (_, _) => cts.Cancel();

        var result = await admin.CreateRoleAsync(
            new AccessRolesRoleRequest { Name = "Guests", Locks = [], Libraries = new AccessRolesRoleLibrariesRequest { Mode = AccessRolesRoleLibraryMode.NotManaged } },
            new AccessRolesWriteOptions { DryRun = false },
            new ClaimsPrincipal(new ClaimsIdentity()),
            cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.Contains(api.Store.Current.Roles, r => r.Name == "Guests");
        Assert.False(result.DryRun);
        Assert.Single(result.AuditSeq);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public async Task ARoleSaveWhoseChangedHandlerFails_IsSavedAndSaysSo()
    {
        // Handlers must never throw; one that does is logged, and the saved change is answered as saved.
        await using var api = await StartWithFamilyAsync();
        api.Store.Changed += (_, _) => throw new InvalidOperationException("a handler that breaks its contract");

        var (status, answer) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles?dryRun=false", body: new { Name = "Guests", Locks = Array.Empty<string>(), Libraries = new { Mode = "NotManaged", Refs = Array.Empty<object>() } });

        Assert.Equal(200, status);
        Assert.Contains(api.Store.Current.Roles, r => r.Name == "Guests");
        Assert.Single(answer!["AuditSeq"]!.AsArray());
    }

    [Fact]
    public async Task ARestoreThatFailsBeforeItsSave_NeverRemovesAnEarlierRestoresHold()
    {
        // A mutation test's survivor: a restore that fails after Begin (here its pre-operation snapshot can't be written) while an
        // earlier restore's hold is there leaves that hold, whose restored data is still the stored one.
        await using var api = await StartWithFamilyAsync();
        var token = TestContext.Current.CancellationToken;
        var paths = api.Web.Services.GetRequiredService<AccessRolesPaths>();
        var name = await SnapshotThenChangeAsync(api, token);
        var (first, _) = await api.JsonAsync(HttpMethod.Post, RestorePath + "?dryRun=false&name=" + Uri.EscapeDataString(name));
        Assert.Equal(200, first);
        Assert.True(File.Exists(paths.RestoreHoldFile));
        await api.Store.UpdateAsync(d => d with { Roles = [d.Roles[0] with { Name = "Family (again)", Libraries = d.Roles[0].Libraries with { Refs = [d.Roles[0].Libraries.Refs[0]] } }] }, token);
        BreakPreOpSnapshots(paths);

        var (second, _) = await api.JsonAsync(HttpMethod.Post, RestorePath + "?dryRun=false&name=" + Uri.EscapeDataString(name));

        Assert.NotEqual(200, second);
        Assert.Equal("Family (again)", api.Store.Current.Roles.Single().Name);
        Assert.True(File.Exists(paths.RestoreHoldFile), "the earlier restore's hold stays");
    }

    [Fact]
    public async Task ARestoreThatFailsBeforeItsSave_RemovesTheHoldItWrote()
    {
        await using var api = await StartWithFamilyAsync();
        var token = TestContext.Current.CancellationToken;
        var paths = api.Web.Services.GetRequiredService<AccessRolesPaths>();
        var name = await SnapshotThenChangeAsync(api, token);
        BreakPreOpSnapshots(paths);

        var (status, _) = await api.JsonAsync(HttpMethod.Post, RestorePath + "?dryRun=false&name=" + Uri.EscapeDataString(name));

        Assert.NotEqual(200, status);
        Assert.Equal("Family (changed)", api.Store.Current.Roles.Single().Name);
        Assert.False(File.Exists(paths.RestoreHoldFile));
    }

    [Fact]
    public async Task ARestoreWhoseStoreWriteFails_KeepsItsHold_ItMayHaveSaved()
    {
        // The store had the document when its write failed: nothing here can be sure it wasn't swapped in, so the hold stays
        // (fail closed); the admin confirms the sync on the Overview as after any restore.
        await using var api = await StartWithFamilyAsync();
        var token = TestContext.Current.CancellationToken;
        var paths = api.Web.Services.GetRequiredService<AccessRolesPaths>();
        var name = await SnapshotThenChangeAsync(api, token);
        var file = Path.Combine(paths.Root, AccessRolesPaths.StoreFileName);

        int status;
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            (status, _) = await api.JsonAsync(HttpMethod.Post, RestorePath + "?dryRun=false&name=" + Uri.EscapeDataString(name));
        }

        Assert.Equal(503, status);
        Assert.True(File.Exists(paths.RestoreHoldFile));
    }

    [Fact]
    public async Task RemapByName_DropsTheOldIdsLockoutConfirmation_SoTheSyncGuardsTheRecreatedAdmin()
    {
        // The only full administrator was re-created (a new id, the same name); the old id's assignment
        // narrows them and carries a confirmation given for the old account. Remapped, the confirmation is gone, and the
        // sync's lockout guard leaves the admin's every-library access alone.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var admin = User(api, "admin");
        var old = Guid.NewGuid();
        var narrow = Role(3, "Narrow", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await api.Store.UpdateAsync(
            d => d with
            {
                Roles = [narrow],
                Assignments = [new RoleAssignment { UserId = old, RoleIds = [narrow.Id], UpdatedUtc = Now, ConfirmedNoFullAdminUtc = Now }],
                KnownUsers = d.KnownUsers.With(old, "admin")
            },
            TestContext.Current.CancellationToken);

        var (dry, plan) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Remap", userId: admin.Id);
        var (remap, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Remap?dryRun=false&ifPlan=" + Uri.EscapeDataString((string?)plan?["PlanToken"] ?? string.Empty), userId: admin.Id);
        var (syncStatus, sync) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Sync?dryRun=true");

        Assert.Equal((200, 200, 200), (dry, remap, syncStatus));
        var moved = Assert.Single(api.Store.Current.Assignments, a => a.UserId.Equals(admin.Id));
        Assert.Null(moved.ConfirmedNoFullAdminUtc);
        var row = Assert.Single(sync!["Users"]!.AsArray(), u => (string?)u!["UserId"] is { } id && Guid.Parse(id).Equals(admin.Id));
        Assert.Equal(("Skip", "NoFullAdministrator"), ((string?)row!["Action"], (string?)row["Reason"]));
    }

    [Fact]
    public async Task AnAllExceptRoleWhoseExcludedLibraryIsAmbiguousHere_IsLeftOut_NeverWidenedToEveryLibrary()
    {
        // The export's role is "every library but Horror". Here two export ids find the same library (one
        // by folders, one by name), so both are Ambiguous: imported without them, the role would grant Horror.
        await using var api = await StartWithFamilyAsync();
        var kid = User(api, "kid");
        var except = Role(2, "NoHorror", mode: AccessRolesRoleLibraryMode.AllExcept, refs: [Horror]);
        await api.Store.UpdateAsync(d => d with { Roles = [except], Assignments = [Assign(Server(kid), [except])] }, TestContext.Current.CancellationToken);
        var export = JsonNode.Parse(await ExportAsync(api))!;
        var otherHorror = Guid.NewGuid();
        var otherNamed = Guid.NewGuid();
        var data = export["Data"]!;
        data["Roles"]![0]!["Libraries"]!["Refs"]![0]!["Id"] = otherHorror.ToString("D");
        data["Roles"]![0]!["Libraries"]!["Refs"]!.AsArray().Add(new JsonObject { ["Id"] = otherNamed.ToString("D"), ["NameAtSelection"] = Horror.Name, ["Locations"] = new JsonArray(@"Q:\elsewhere") });
        data["KnownLibraries"] = new JsonArray(
            new JsonObject { ["Id"] = otherHorror.ToString("D"), ["Name"] = "Scary", ["Locations"] = new JsonArray([.. Horror.Locations.Select(l => (JsonNode)l)]), ["State"] = "Active" },
            new JsonObject { ["Id"] = otherNamed.ToString("D"), ["Name"] = Horror.Name, ["Locations"] = new JsonArray(@"Q:\elsewhere"), ["State"] = "Active" });
        await api.Store.UpdateAsync(_ => AccessRolesDocument.CreateEmpty(), TestContext.Current.CancellationToken);

        var (dryStatus, dry) = await api.JsonAsync(HttpMethod.Post, ImportPath, body: export.ToJsonString());
        var (status, report) = await api.JsonAsync(HttpMethod.Post, ImportPath + "?dryRun=false", body: export.ToJsonString());

        Assert.Equal((200, 200), (dryStatus, status));
        Assert.Empty(api.Store.Current.Roles);
        foreach (var answer in new[] { dry!, report! })
        {
            var skipped = Assert.Single(answer["Skipped"]!.AsArray(), s => (string?)s!["Kind"] == "Role");
            Assert.Equal(("NoHorror", AccessRolesDataImport.LibrariesUnmatched), ((string?)skipped!["Name"], (string?)skipped["Reason"]));
            Assert.Equal(
                new[] { otherHorror, otherNamed }.Order(),
                skipped["Libraries"]!.AsArray().Select(l => Guid.Parse((string)l!["ExportId"]!)).Order());
            Assert.All(skipped["Libraries"]!.AsArray(), l => Assert.Equal(AccessRolesDataImport.Ambiguous, (string?)l!["Reason"]));
        }

        Assert.DoesNotContain(api.Store.Current.Assignments, a => a.RoleIds.Contains(except.Id));
    }

    [Fact]
    public async Task AnAllExceptRoleWhoseExcludedLibraryIsntHere_IsLeftOut_NotStoredAsAllExceptNothing()
    {
        // The library the role leaves out isn't on this server at all.
        await using var api = await StartWithFamilyAsync();
        var kid = User(api, "kid");
        var except = Role(2, "NoHorror", mode: AccessRolesRoleLibraryMode.AllExcept, refs: [Horror]);
        await api.Store.UpdateAsync(d => d with { Roles = [except], Assignments = [Assign(Server(kid), [except])] }, TestContext.Current.CancellationToken);
        var export = JsonNode.Parse(await ExportAsync(api))!;
        var data = export["Data"]!;
        var other = Guid.NewGuid();
        data["Roles"]![0]!["Libraries"]!["Refs"]![0]!["Id"] = other.ToString("D");
        data["Roles"]![0]!["Libraries"]!["Refs"]![0]!["NameAtSelection"] = "Adult";
        data["Roles"]![0]!["Libraries"]!["Refs"]![0]!["Locations"] = new JsonArray(@"Q:\Adult");
        data["KnownLibraries"] = new JsonArray();
        await api.Store.UpdateAsync(_ => AccessRolesDocument.CreateEmpty(), TestContext.Current.CancellationToken);

        var (status, report) = await api.JsonAsync(HttpMethod.Post, ImportPath + "?dryRun=false", body: export.ToJsonString());

        Assert.Equal(200, status);
        Assert.DoesNotContain(api.Store.Current.Roles, r => r.Libraries.Mode.Is(AccessRolesRoleLibraryMode.AllExcept) && r.Libraries.Refs.Count == 0);
        Assert.Empty(api.Store.Current.Roles);
        var skipped = Assert.Single(report!["Skipped"]!.AsArray(), s => (string?)s!["Kind"] == "Role");
        Assert.Equal(AccessRolesDataImport.LibrariesUnmatched, (string?)skipped!["Reason"]);
        var library = Assert.Single(skipped["Libraries"]!.AsArray());
        Assert.Equal(("Adult", AccessRolesDataImport.NoSuchLibrary), ((string?)library!["Name"], (string?)library["Reason"]));
    }

    [Fact]
    public async Task AnImportsRoles_AreCheckedAsTheRolesApiChecksThem_TheInvalidOnesLeftOutWithTheReason()
    {
        // The roles API's rules: each role the roles API would refuse is left out, Skipped with
        // the reason and the API's message; a valid one is stored with its name trimmed, as the API stores it.
        await using var api = await StartWithFamilyAsync();
        var export = JsonNode.Parse(await ExportAsync(api))!;
        var roles = export["Data"]!["Roles"]!.AsArray();
        var template = roles[0]!;
        JsonNode Copy(string? name, string mode, bool refs, string? description = null)
        {
            var copy = template.DeepClone();
            copy["Id"] = Guid.NewGuid().ToString("D");
            copy["Name"] = name;
            copy["Description"] = description;
            copy["Libraries"]!["Mode"] = mode;
            if (!refs)
            {
                copy["Libraries"]!["Refs"] = new JsonArray();
            }

            roles.Add(copy);
            return copy;
        }

        Copy("   ", "Only", refs: true);
        Copy("Bad\u0000Name\u0007", "Only", refs: true);
        Copy(new string('x', 101), "Only", refs: true);
        Copy(null, "Only", refs: true);
        Copy("Described", "Only", refs: true, description: new string('d', 501));
        Copy("EveryButNothing", "AllExcept", refs: false);
        Copy("AllWithRefs", "All", refs: true);
        Copy("NotManagedWithRefs", "NotManaged", refs: true);
        Copy("OnlyNothing", "Only", refs: false);
        Copy("  Teens  ", "Only", refs: true);

        var (dryStatus, dry) = await api.JsonAsync(HttpMethod.Post, ImportPath, body: export.ToJsonString());
        var (status, report) = await api.JsonAsync(HttpMethod.Post, ImportPath + "?dryRun=false", body: export.ToJsonString());

        Assert.Equal((200, 200), (dryStatus, status));
        Assert.Equal(["Family", "Teens"], api.Store.Current.Roles.Select(r => r.Name));
        foreach (var answer in new[] { dry!, report! })
        {
            var left = answer["Skipped"]!.AsArray().Where(s => (string?)s!["Kind"] == "Role").Select(s => (string?)s!["Reason"]).ToList();
            Assert.Equal(
                [
                    AccessRolesDataImport.InvalidName, AccessRolesDataImport.InvalidName, AccessRolesDataImport.InvalidName, AccessRolesDataImport.InvalidName,
                    AccessRolesDataImport.InvalidDescription, AccessRolesDataImport.InvalidLibraries, AccessRolesDataImport.InvalidLibraries,
                    AccessRolesDataImport.InvalidLibraries, AccessRolesException.EmptyOnlyRole
                ],
                left);
            Assert.All(answer["Skipped"]!.AsArray().Where(s => (string?)s!["Kind"] == "Role"), s => Assert.False(string.IsNullOrWhiteSpace((string?)s!["Detail"])));
        }

        Assert.DoesNotContain(api.Store.Current.Roles, r => string.IsNullOrWhiteSpace(r.Name) || r.Name.Any(char.IsControl));
    }

    [Fact]
    public async Task AFileWithTooManyRoles_IsRefusedAtOnce_AndOneAtTheLimitsIsImportedInSeconds()
    {
        // 20,001 roles used to take 19.7 s inside the store's writer. Over the limits, 400 TooManyRecords before
        // anything is mapped; at them (1,000 roles, 10,000 assignments), the dry run and the save each take a few seconds.
        await using var api = await StartWithFamilyAsync();
        var export = JsonNode.Parse(await ExportAsync(api))!;
        var template = export["Data"]!["Roles"]![0]!;
        var assignment = export["Data"]!["Assignments"]![0]!;
        JsonNode With(int roles, int assignments)
        {
            var copy = export.DeepClone();
            var list = copy["Data"]!["Roles"]!.AsArray();
            var ids = new List<string>();
            for (var i = 1; i < roles; i++)
            {
                var role = template.DeepClone();
                var id = Guid.NewGuid().ToString("D");
                role["Id"] = id;
                role["Name"] = "Role " + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                list.Add(role);
                ids.Add(id);
            }

            var given = copy["Data"]!["Assignments"]!.AsArray();
            for (var i = 1; i < assignments; i++)
            {
                var other = assignment.DeepClone();
                other["UserId"] = Guid.NewGuid().ToString("D");
                other["RoleIds"] = new JsonArray([.. ids.Skip(i % Math.Max(1, ids.Count)).Take(5).Select(id => (JsonNode)id)]);
                given.Add(other);
            }

            return copy;
        }

        var over = With(20_001, 1).ToJsonString();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (overStatus, problem) = await api.JsonAsync(HttpMethod.Post, ImportPath, body: over);
        var refusedIn = watch.Elapsed;

        Assert.Equal((400, AccessRolesException.TooManyRecords), (overStatus, (string?)problem!["Code"]));
        Assert.True(refusedIn < TimeSpan.FromSeconds(5), $"refused in {refusedIn.TotalSeconds:F1} s");
        var (manyAssignments, tooMany) = await api.JsonAsync(HttpMethod.Post, ImportPath, body: With(1, AccessRolesDataImport.MaxUserRecords + 1).ToJsonString());
        Assert.Equal((400, AccessRolesException.TooManyRecords), (manyAssignments, (string?)tooMany!["Code"]));

        var at = With(AccessRolesDataImport.MaxRoles, AccessRolesDataImport.MaxUserRecords).ToJsonString();
        watch.Restart();
        var (dryStatus, dry) = await api.JsonAsync(HttpMethod.Post, ImportPath, body: at);
        var dryIn = watch.Elapsed;
        watch.Restart();
        var (saveStatus, saved) = await api.JsonAsync(HttpMethod.Post, ImportPath + "?dryRun=false&ifPreview=" + Uri.EscapeDataString((string)dry!["Preview"]!["PreviewToken"]!), body: at);
        var saveIn = watch.Elapsed;

        Assert.Equal((200, 200), (dryStatus, saveStatus));
        Assert.Equal(AccessRolesDataImport.MaxRoles, api.Store.Current.Roles.Count);
        Assert.Equal(AccessRolesDataImport.MaxRoles - 1, (int)saved!["Records"]!["Roles"]!["Added"]!);
        Assert.True(dryIn < TimeSpan.FromSeconds(5) && saveIn < TimeSpan.FromSeconds(5), $"dry run {dryIn.TotalSeconds:F1} s, save {saveIn.TotalSeconds:F1} s");
    }

    [Fact]
    public async Task ANameGivenTwiceInsideTheData_OrAWrongType_IsA400WithTheCode_AndInASnapshotA409()
    {
        // A duplicate key nested in Data escaped as a raw ArgumentException (a 500).
        await using var api = await StartWithFamilyAsync();
        var token = TestContext.Current.CancellationToken;
        var text = JsonNode.Parse(await ExportAsync(api))!.ToJsonString();
        var inData = text.Replace("\"Roles\":", "\"DefaultRoleIds\":[],\"Roles\":", StringComparison.Ordinal);
        var inARole = text.Replace("\"Name\":\"Family\"", "\"Name\":\"Family\",\"Name\":\"Twice\"", StringComparison.Ordinal);
        var inUsers = text.Replace("\"Name\":\"kid\"", "\"Name\":\"kid\",\"Name\":\"kid\"", StringComparison.Ordinal);
        var export = JsonNode.Parse(text)!;
        export["Data"]!["Roles"] = "not an array";
        Assert.NotEqual(text, inData);
        Assert.NotEqual(text, inARole);
        Assert.NotEqual(text, inUsers);

        foreach (var body in new[] { inData, inARole, inUsers, export.ToJsonString() })
        {
            var (status, problem) = await api.JsonAsync(HttpMethod.Post, ImportPath, body: body);
            Assert.True((status, (string?)problem?["Code"]) == (400, AccessRolesException.InvalidData), status + " " + problem?.ToJsonString());
        }

        var paths = api.Web.Services.GetRequiredService<AccessRolesPaths>();
        Directory.CreateDirectory(paths.SnapshotsDirectory);
        const string Name = "accessroles-20261018-031000.json";
        await File.WriteAllTextAsync(Path.Combine(paths.SnapshotsDirectory, Name), """{"Version":1,"Roles":[{"Id":"7a1b2c3d-4e5f-4071-8293-a4b5c6d7e8f9","Name":"Guest","Name":"Twice","Locks":[],"Libraries":{"Mode":"NotManaged","Refs":[]},"CreatedUtc":"2026-10-01T00:00:00Z","UpdatedUtc":"2026-10-01T00:00:00Z"}],"Assignments":[],"Ledger":{}}""", token);
        var (restore, refused) = await api.JsonAsync(HttpMethod.Post, RestorePath + "?name=" + Name);
        Assert.True((restore, (string?)refused?["Code"]) == (409, AccessRolesException.InvalidData), restore + " " + refused?.ToJsonString());
        Assert.Equal("Family", api.Store.Current.Roles.Single().Name);
    }

    [Fact]
    public async Task ADryRunSeenForAnEarlierRestore_DoesntConfirmTheNextOne_AndTheConfirmationNeedsItsPlan()
    {
        // A mutation test's survivor: a dry run of the first restore's hold, then a second restore, then confirmRestore without ifPlan:
        // the second hold's dry run wasn't seen (409 DryRunRequired). Its dry run seen, confirmRestore still needs ifPlan
        // (400 IfPlanRequired); with the PlanToken it lifts the hold.
        await using var api = await StartWithFamilyAsync();
        var token = TestContext.Current.CancellationToken;
        var paths = api.Web.Services.GetRequiredService<AccessRolesPaths>();
        var name = await SnapshotThenChangeAsync(api, token);
        var (first, _) = await api.JsonAsync(HttpMethod.Post, RestorePath + "?dryRun=false&name=" + Uri.EscapeDataString(name));
        var (firstDry, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Sync?dryRun=true");
        await api.Store.UpdateAsync(d => d with { Roles = [d.Roles[0] with { Name = "Family (again)", Libraries = d.Roles[0].Libraries with { Refs = [d.Roles[0].Libraries.Refs[0]] } }] }, token);
        var (second, _) = await api.JsonAsync(HttpMethod.Post, RestorePath + "?dryRun=false&name=" + Uri.EscapeDataString(name));
        Assert.Equal((200, 200, 200), (first, firstDry, second));

        var (early, refused) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Sync?confirmRestore=true");

        Assert.Equal((409, AccessRolesException.DryRunRequired), (early, (string?)refused!["Code"]));
        Assert.True(File.Exists(paths.RestoreHoldFile));
        var (_, plan) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Sync?dryRun=true");
        var (noPlan, planRefused) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Sync?confirmRestore=true");
        Assert.Equal((400, AccessRolesException.IfPlanRequired), (noPlan, (string?)planRefused!["Code"]));
        Assert.True(File.Exists(paths.RestoreHoldFile));
        var (confirmed, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Sync?confirmRestore=true&ifPlan=" + Uri.EscapeDataString((string)plan!["PlanToken"]!));
        Assert.Equal(200, confirmed);
        Assert.False(File.Exists(paths.RestoreHoldFile));
    }

    [Fact]
    public async Task ARestore_KeysEveryRecordOnTheUsersId_AUserRenamedOrRecreatedSinceGetsNothingByName()
    {
        // A mutation test's survivor: the snapshot's records are the kid's by id. Renamed since, the kid keeps them; the snapshot's other
        // user, deleted and created again under the same name (a new id), gets nothing (a restore never maps by name).
        await using var api = await StartWithFamilyAsync();
        var token = TestContext.Current.CancellationToken;
        var paths = api.Web.Services.GetRequiredService<AccessRolesPaths>();
        var kid = User(api, "kid");
        var viewer = User(api, "viewer");
        var old = Guid.NewGuid();
        var family = api.Store.Current.Roles.Single();
        await api.Store.UpdateAsync(d => d with { Assignments = [.. d.Assignments, new RoleAssignment { UserId = old, RoleIds = [family.Id], UpdatedUtc = Now }], KnownUsers = d.KnownUsers.With(old, viewer.Username) }, token);
        var snapshot = Path.GetFileName(await api.Store.Documents.SnapshotAsync(paths.SnapshotsDirectory, 5, token))!;
        await api.Store.UpdateAsync(d => d with { Assignments = [], KnownUsers = d.KnownUsers.Without(old) }, token);
        kid.Username = "kid (renamed)";

        var (status, report) = await api.JsonAsync(HttpMethod.Post, RestorePath + "?dryRun=false&name=" + Uri.EscapeDataString(snapshot));

        Assert.True(status == 200, status + " " + report?.ToJsonString());
        Assert.Equal([kid.Id], api.Store.Current.Assignments.Select(a => a.UserId));
        Assert.DoesNotContain(api.Store.Current.Assignments, a => a.UserId.Equals(viewer.Id));
        Assert.Equal("Id", (string?)report!["MapUsersBy"]);
        var unknown = Assert.Single(report["Users"]!["Unknown"]!.AsArray());
        Assert.Equal(old, Guid.Parse((string)unknown!["Id"]!));
    }

    internal static async Task<AccessRolesApi> StartWithFamilyAsync()
    {
        var api = await RolesApiTests.StartWithUsersAsync();
        var kid = Server(User(api, "kid"));
        var family = Role(1, "Family", mode: AccessRolesRoleLibraryMode.Only, refs: [Movies, Kids]);
        await api.Store.UpdateAsync(d => d with { Roles = [family], Assignments = [Assign(kid, [family])], KnownUsers = d.KnownUsers.With(kid.Id, kid.Name) }, TestContext.Current.CancellationToken);
        return api;
    }

    internal static async Task<string> ExportAsync(AccessRolesApi api)
    {
        using var response = await api.SendAsync(HttpMethod.Get, "/AccessRoles/Admin/Export");
        Assert.Equal(200, (int)response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    internal static Jellyfin.Database.Implementations.Entities.User User(AccessRolesApi api, string name) => api.UserManager.Object.AllUsers().Single(u => u.Username == name);

    internal static ServerUser Server(Jellyfin.Database.Implementations.Entities.User user) => new(user.Id, user.Username, false, false, false, AllFolders);

    // A daily snapshot of the family role, then the role changed: the snapshot's name, to restore.
    private static async Task<string> SnapshotThenChangeAsync(AccessRolesApi api, CancellationToken token)
    {
        var paths = api.Web.Services.GetRequiredService<AccessRolesPaths>();
        var snapshot = await api.Store.Documents.SnapshotAsync(paths.SnapshotsDirectory, 5, token);
        await api.Store.UpdateAsync(d => d with { Roles = [d.Roles[0] with { Name = "Family (changed)", Libraries = d.Roles[0].Libraries with { Refs = [d.Roles[0].Libraries.Refs[0]] } }] }, token);
        return Path.GetFileName(snapshot!);
    }

    // A file where the pre-operation snapshots' folder goes: the restore's snapshot before its save can't be written.
    private static void BreakPreOpSnapshots(AccessRolesPaths paths)
    {
        var preOp = Path.Combine(paths.SnapshotsDirectory, SnapshotOperations.Folder);
        if (Directory.Exists(preOp))
        {
            Directory.Delete(preOp, recursive: true);
        }

        File.WriteAllText(preOp, "not a folder");
    }
}
