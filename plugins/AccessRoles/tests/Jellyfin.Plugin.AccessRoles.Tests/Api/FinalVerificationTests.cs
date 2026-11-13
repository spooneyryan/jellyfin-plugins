using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Api;

/// <summary>
/// Regressions of Access Roles 1.0.0 (three restore and import cases and the purge), as tests of the safe behaviour
/// (1.0.1): a restore that fails takes back only its own hold of the sync, and restores run one at a time; a confirmation
/// lifts only the hold it confirmed; an import leaves out a user whose own Remove list names a library not found here.
/// </summary>
public sealed class FinalVerificationTests
{
    private const string RestorePath = "/AccessRoles/Admin/Snapshots/Restore";

    // One restore at a time, inverted. The first restore (A) begins its hold and waits for the store's writer, which an admin edit holds; a
    // second restore (B) meanwhile answers 409 RestoreInProgress with nothing written. The edit lands (viewer managed by a
    // new role), so A's save is refused (viewer would leave management) and A takes back its own hold: nothing restored,
    // nothing held. On 1.0.0, B began its hold too, and A's failure deleted it: B's restored data was saved, not held.
    [Fact]
    public async Task ASecondRestoreWhileOneIsInProgress_Is409RestoreInProgress_AndTheFirstsFailureLeavesNothingHeld()
    {
        await using var api = await DataSafetyTests.StartWithFamilyAsync();
        var token = TestContext.Current.CancellationToken;
        var paths = api.Web.Services.GetRequiredService<AccessRolesPaths>();
        var admin = api.Web.Services.GetRequiredService<IAccessRolesAdmin>();
        var viewer = DataSafetyTests.User(api, "viewer");
        var teen = Role(5, "Teen", mode: AccessRolesRoleLibraryMode.Only, refs: [Movies]);
        var viewerAssign = Assign(DataSafetyTests.Server(viewer), [teen]);

        var s1 = Path.GetFileName(await api.Store.Documents.SnapshotAsync(paths.SnapshotsDirectory, 10, token))!;
        await api.Store.UpdateAsync(d => d with { Roles = [.. d.Roles, teen], Assignments = [.. d.Assignments, viewerAssign], KnownUsers = d.KnownUsers.With(viewer.Id, viewer.Username) }, token);
        var s2 = Path.GetFileName(await api.Store.Documents.SnapshotAsync(paths.SnapshotsDirectory, 10, token))!;
        Assert.NotEqual(s1, s2);
        var family = api.Store.Current.Roles.First(r => r.Name == "Family");
        await api.Store.UpdateAsync(d => d with { Roles = [family with { Name = "Family (changed)", Libraries = family.Libraries with { Refs = [family.Libraries.Refs[0]] } }], Assignments = [.. d.Assignments.Where(a => !a.UserId.Equals(viewer.Id))] }, token);

        // The writer held by a concurrent admin edit that manages viewer again once it is let go.
        using var gate = new ManualResetEventSlim(false);
        var edit = Task.Run(() => api.Store.UpdateAsync(
            d =>
            {
                gate.Wait();
                return d with { Roles = [.. d.Roles, teen], Assignments = [.. d.Assignments, viewerAssign] };
            },
            CancellationToken.None));
        await Task.Delay(200, token);

        var principal = new ClaimsPrincipal(new ClaimsIdentity());
        var first = Task.Run(() => admin.RestoreSnapshotAsync(s1, new AccessRolesWriteOptions { DryRun = false }, principal, CancellationToken.None));
        await WaitForHoldAsync(paths, api.Store, s1, token);
        var owner = Owner(paths);

        // Refused at once; one let through would wait for the store's writer behind A, so it is given 10 s, then the gate is
        // opened and the test fails (it used to hang there).
        var second = await Assert.ThrowsAsync<AccessRolesException>(async () =>
        {
            try
            {
                await admin.RestoreSnapshotAsync(s2, new AccessRolesWriteOptions { DryRun = false }, principal, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60), token);
            }
            catch (TimeoutException)
            {
                gate.Set();
                throw;
            }
        });
        var (dryStatus, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Snapshots/Restore?dryRun=true&name=" + Uri.EscapeDataString(s2));

        Assert.Equal((Jellyfin.Plugin.Kit.Api.PluginErrorKind.Conflict, AccessRolesException.RestoreInProgress), (second.Kind, second.Code));
        Assert.Equal(200, dryStatus);
        Assert.Equal(owner, Owner(paths));
        gate.Set();
        await edit;
        var refused = await Assert.ThrowsAsync<AccessRolesException>(() => first);
        Assert.Equal(AccessRolesException.UnmanageChoiceRequired, refused.Code);
        Assert.False(File.Exists(paths.RestoreHoldFile), "A saved nothing and took back its own hold");
        Assert.Contains(api.Store.Current.Roles, r => r.Name == "Teen");
        Assert.Contains(api.Store.Current.Roles, r => r.Name == "Family (changed)");

        // The restores' mark is let go whatever the outcome: B restores now, held.
        var later = await admin.RestoreSnapshotAsync(s2, new AccessRolesWriteOptions { DryRun = false, OnUnmanage = AccessRolesUnmanageChoice.Keep }, principal, CancellationToken.None);
        Assert.True(later.SyncHeld);
        Assert.True(File.Exists(paths.RestoreHoldFile));
        Assert.Contains(api.Store.Current.Roles, r => r.Name == "Family");
    }

    // The owner itself, below the one-at-a-time mark: a restore's Cancel touches only a hold file that is still its own, and
    // puts back the one it replaced.
    [Fact]
    public void ARestoresCancel_TakesBackOnlyItsOwnHold_AndPutsBackTheOneItReplaced()
    {
        using var h = new SyncHarness();
        var a = RestoreHold.Begin(h.Paths, h.Store, h.Now, "a.json");
        var aText = File.ReadAllText(h.Paths.RestoreHoldFile);
        var b = RestoreHold.Begin(h.Paths, h.Store, h.Now.AddSeconds(1), "b.json");
        var bText = File.ReadAllText(h.Paths.RestoreHoldFile);

        RestoreHold.Cancel(h.Paths, h.Store, a, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.Equal(bText, File.ReadAllText(h.Paths.RestoreHoldFile));
        Assert.False(a.Replaced);
        Assert.True(b.Replaced);
        Assert.NotEqual(a.Owner, b.Owner);

        RestoreHold.Cancel(h.Paths, h.Store, b, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.Equal(aText, File.ReadAllText(h.Paths.RestoreHoldFile));

        RestoreHold.Cancel(h.Paths, h.Store, a, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.False(File.Exists(h.Paths.RestoreHoldFile));
    }

    // A confirmation lifts only the hold it confirmed, inverted. A restore (A) is held and its dry run seen; its confirmation passed its checks and waits for the store's
    // writer; a second restore (B) begins its hold meanwhile. Under the writer the confirmation finds A's hold is no longer
    // the one there (and a restore in progress): 409 DryRunRequired, nothing lifted, and B's restored data is saved held. On
    // 1.0.0 the confirmation lifted "the" hold, B's file, and B saved with nothing held.
    [Fact]
    public async Task AConfirmRestoreRacingASecondRestore_Is409_AndTheSecondStaysHeld()
    {
        await using var api = await DataSafetyTests.StartWithFamilyAsync();
        var token = TestContext.Current.CancellationToken;
        var paths = api.Web.Services.GetRequiredService<AccessRolesPaths>();
        var admin = api.Web.Services.GetRequiredService<IAccessRolesAdmin>();
        var viewer = DataSafetyTests.User(api, "viewer");
        var teen = Role(5, "Teen", mode: AccessRolesRoleLibraryMode.Only, refs: [Movies]);
        var viewerAssign = Assign(DataSafetyTests.Server(viewer), [teen]);

        var s1 = Path.GetFileName(await api.Store.Documents.SnapshotAsync(paths.SnapshotsDirectory, 10, token))!;
        await api.Store.UpdateAsync(d => d with { Roles = [.. d.Roles, teen], Assignments = [.. d.Assignments, viewerAssign], KnownUsers = d.KnownUsers.With(viewer.Id, viewer.Username) }, token);
        var s2 = Path.GetFileName(await api.Store.Documents.SnapshotAsync(paths.SnapshotsDirectory, 10, token))!;
        var family = api.Store.Current.Roles.First(r => r.Name == "Family");
        await api.Store.UpdateAsync(d => d with { Roles = [family with { Name = "Family (changed)", Libraries = family.Libraries with { Refs = [family.Libraries.Refs[0]] } }], Assignments = [.. d.Assignments.Where(a => !a.UserId.Equals(viewer.Id))] }, token);

        var (restored, _) = await api.JsonAsync(HttpMethod.Post, RestorePath + "?dryRun=false&onUnmanage=Keep&name=" + Uri.EscapeDataString(s1));
        Assert.Equal(200, restored);
        var firstOwner = Owner(paths);
        var (dry, plan) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Sync?dryRun=true");
        Assert.Equal(200, dry);
        var planToken = (string)plan!["PlanToken"]!;

        using var gate = new ManualResetEventSlim(false);
        var block = Task.Run(() => api.Store.UpdateAsync(d => { gate.Wait(); return d; }, CancellationToken.None));
        await Task.Delay(200, token);
        var confirm = Task.Run(() => api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Sync?confirmRestore=true&ifPlan=" + Uri.EscapeDataString(planToken)));
        await Task.Delay(800, token);
        var principal = new ClaimsPrincipal(new ClaimsIdentity());
        var second = Task.Run(() => admin.RestoreSnapshotAsync(s2, new AccessRolesWriteOptions { DryRun = false }, principal, CancellationToken.None));
        var waiting = WaitForHoldAsync(paths, api.Store, s2, token);
        if (await Task.WhenAny(waiting, second) == second)
        {
            gate.Set();
            throw new Xunit.Sdk.XunitException("B ended before its hold appeared: " + (second.Exception?.ToString() ?? "without an error"));
        }

        await waiting;

        await Task.Delay(300, token);
        gate.Set();
        await block;
        var (confirmed, answer) = await confirm;
        var secondResult = await second;

        Assert.Equal((409, AccessRolesException.DryRunRequired), (confirmed, (string?)answer!["Code"]));
        Assert.Contains(api.Store.Current.Roles, r => r.Name == "Teen");
        Assert.True(secondResult.SyncHeld);
        Assert.True(File.Exists(paths.RestoreHoldFile), "B's restored data is saved, so the sync is held");
        Assert.NotEqual(firstOwner, Owner(paths));
        var (_, snapshots) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Snapshots");
        Assert.True((bool)snapshots!["SyncHeld"]!);

        // B's own dry run and confirmation lift it.
        var (_, plan2) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Sync?dryRun=true");
        var (lifted, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Sync?confirmRestore=true&ifPlan=" + Uri.EscapeDataString((string)plan2!["PlanToken"]!));
        Assert.Equal(200, lifted);
        Assert.False(File.Exists(paths.RestoreHoldFile));
    }

    // The binding itself, below the API: a confirmation lifts only the hold it confirmed, and nothing while a restore is in
    // progress.
    [Fact]
    public void ARelease_LiftsOnlyTheHoldItConfirmed_AndNothingWhileARestoreIsInProgress()
    {
        using var h = new SyncHarness();
        RestoreHold.Begin(h.Paths, h.Store, h.Now, "a.json");
        var a = RestoreHold.Current(h.Paths, h.Store)!;
        RestoreHold.Begin(h.Paths, h.Store, h.Now, "b.json");
        var b = RestoreHold.Current(h.Paths, h.Store)!;

        Assert.NotEqual(a, b);
        Assert.True(RestoreHold.IsHeld(h.Paths, h.Store, except: a));
        Assert.False(RestoreHold.IsHeld(h.Paths, h.Store, except: b));
        Assert.Equal(RestoreHold.ReleaseOutcome.AnotherHold, RestoreHold.Release(h.Paths, h.Store, a));
        Assert.True(File.Exists(h.Paths.RestoreHoldFile));
        Assert.False(h.Store.RestoreConfirmed);

        Assert.True(h.Store.TryBeginRestore());
        Assert.False(h.Store.TryBeginRestore());
        Assert.True(RestoreHold.IsHeld(h.Paths, h.Store, except: b));
        Assert.Equal(RestoreHold.ReleaseOutcome.AnotherHold, RestoreHold.Release(h.Paths, h.Store, b));
        h.Store.EndRestore();

        Assert.Equal(RestoreHold.ReleaseOutcome.Released, RestoreHold.Release(h.Paths, h.Store, b));
        Assert.False(File.Exists(h.Paths.RestoreHoldFile));
        Assert.Null(RestoreHold.Current(h.Paths, h.Store));
    }

    // Every read of the hold file is under the hold lock (1.0.1): the sync's dry-run check waits while a restore writes the
    // file, instead of holding it open then (a sharing violation on Windows that failed the restore's Begin with a 503).
    [Fact]
    public async Task TheSyncsDryRunCheck_ReadsTheHoldFileUnderTheHoldLock()
    {
        using var h = new SyncHarness();
        RestoreHold.Begin(h.Paths, h.Store, h.Now, "a.json");
        Task<bool>? seen = null;

        var doneWhileLocked = h.Store.WithHoldLock(() =>
        {
            seen = Task.Run(() => RestoreHold.DryRunSeen(h.Paths, h.Store));
            return seen.Wait(300);
        });

        Assert.False(doneWhileLocked);
        Assert.False(await seen!);
        RestoreHold.RecordDryRun(h.Paths, h.Store, h.Now, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        h.Store.RestoreDryRunSeen = false;
        Assert.True(RestoreHold.DryRunSeen(h.Paths, h.Store));
    }

    // An import never widens a user through their own Remove list, inverted: an export where kid sees every library but Horror (user
    // mode All, Remove [Horror]); here Horror is matched by two export ids (one by its folders, one by its name), so both
    // are Ambiguous. The assignment is left out whole (UserLibrariesUnmatched, naming both), in the dry run and the save:
    // kid isn't managed, so nothing widens them. On 1.0.0 kid was stored All with Remove [] (every library).
    [Fact]
    public async Task AnAssignmentsRemoveListWhoseLibraryIsAmbiguousHere_IsLeftOutWhole_NeverWidened()
    {
        await using var api = await DataSafetyTests.StartWithFamilyAsync();
        var token = TestContext.Current.CancellationToken;
        var kid = DataSafetyTests.User(api, "kid");
        await api.Store.UpdateAsync(d => d with { Roles = [], Assignments = [Assign(DataSafetyTests.Server(kid), [], AccessRolesUserLibraryMode.All, remove: [Horror])], KnownUsers = d.KnownUsers.With(kid.Id, kid.Username) }, token);
        var export = JsonNode.Parse(await DataSafetyTests.ExportAsync(api))!;
        var otherHorror = Guid.NewGuid();
        var otherNamed = Guid.NewGuid();
        var data = export["Data"]!;
        data["Assignments"]![0]!["Libraries"]!["Remove"] = new JsonArray(otherHorror.ToString("D"), otherNamed.ToString("D"));
        data["KnownLibraries"] = new JsonArray(
            new JsonObject { ["Id"] = otherHorror.ToString("D"), ["Name"] = "Scary", ["Locations"] = new JsonArray([.. Horror.Locations.Select(l => (JsonNode)l)]), ["State"] = "Active" },
            new JsonObject { ["Id"] = otherNamed.ToString("D"), ["Name"] = Horror.Name, ["Locations"] = new JsonArray(@"Q:\elsewhere"), ["State"] = "Active" });
        await api.Store.UpdateAsync(_ => AccessRolesDocument.CreateEmpty(), token);

        var (dryStatus, dry) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Import?dryRun=true", body: export.ToJsonString());
        var (status, report) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Import?dryRun=false", body: export.ToJsonString());

        Assert.Equal((200, 200), (dryStatus, status));
        foreach (var answer in new[] { dry!, report! })
        {
            var skipped = Assert.Single(answer["Skipped"]!.AsArray(), s => (string?)s!["Kind"] == "Assignment")!;
            Assert.Equal((AccessRolesDataImport.UserLibrariesUnmatched, "kid", kid.Id.ToString("N")), ((string?)skipped["Reason"], (string?)skipped["Name"], (string?)skipped["Id"]));
            Assert.Equal(
                [(otherHorror.ToString("N"), AccessRolesDataImport.Ambiguous), (otherNamed.ToString("N"), AccessRolesDataImport.Ambiguous)],
                skipped["Libraries"]!.AsArray().Select(l => ((string?)l!["ExportId"], (string?)l["Reason"])).OrderBy(l => l.Item1 == otherHorror.ToString("N") ? 0 : 1));
            Assert.DoesNotContain(answer["Skipped"]!.AsArray(), s => (string?)s!["Kind"] == "UserLibrary");
        }

        Assert.DoesNotContain(api.Store.Current.Assignments, a => a.UserId.Equals(kid.Id));
        var (_, effective) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Users/" + kid.Id.ToString("N") + "/Effective");
        Assert.False((bool)effective!["Libraries"]!["Managed"]!);
    }

    // An Add list (or a user's Only list, which is its Add list) losing a library only narrows: imported without it, the
    // library reported, as before.
    [Fact]
    public async Task AnAssignmentsAddListLosingALibrary_IsImportedWithoutIt_AndReported()
    {
        await using var api = await DataSafetyTests.StartWithFamilyAsync();
        var token = TestContext.Current.CancellationToken;
        var kid = DataSafetyTests.User(api, "kid");
        await api.Store.UpdateAsync(d => d with { Roles = [], Assignments = [Assign(DataSafetyTests.Server(kid), [], AccessRolesUserLibraryMode.Only, add: [Movies, Kids])], KnownUsers = d.KnownUsers.With(kid.Id, kid.Username) }, token);
        var export = JsonNode.Parse(await DataSafetyTests.ExportAsync(api))!;
        var ghost = Guid.NewGuid();
        export["Data"]!["Assignments"]![0]!["Libraries"]!["Add"] = new JsonArray(Movies.Id.ToString("D"), ghost.ToString("D"));
        await api.Store.UpdateAsync(_ => AccessRolesDocument.CreateEmpty(), token);

        var (status, report) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Import?dryRun=false", body: export.ToJsonString());

        Assert.Equal(200, status);
        var stored = Assert.Single(api.Store.Current.Assignments, a => a.UserId.Equals(kid.Id));
        Assert.True(stored.Libraries.Mode.Is(AccessRolesUserLibraryMode.Only));
        Assert.Equal([Movies.Id], stored.Libraries.Add);
        var skipped = Assert.Single(report!["Skipped"]!.AsArray(), s => (string?)s!["Kind"] == "UserLibrary")!;
        Assert.Equal((ghost.ToString("N"), AccessRolesDataImport.NoSuchLibrary), ((string?)skipped["Id"], (string?)skipped["Reason"]));
    }

    internal static string? Owner(AccessRolesPaths paths) => (string?)JsonNode.Parse(File.ReadAllText(paths.RestoreHoldFile))!["Owner"];

    // Read under the store's hold lock, as the plugin reads it: a read of its own would hold the file open while a restore
    // writes it (a sharing violation on Windows, which failed that restore's Begin with a 503 in about 1 run in 4 under load).
    internal static async Task WaitForHoldAsync(AccessRolesPaths paths, AccessRolesStore store, string snapshot, CancellationToken token)
    {
        for (var i = 0; i < 500; i++)
        {
            try
            {
                var text = store.WithHoldLock(() => File.Exists(paths.RestoreHoldFile) ? File.ReadAllText(paths.RestoreHoldFile) : null);
                if (text is not null && JsonNode.Parse(text) is JsonObject hold && (string?)hold["Snapshot"] == snapshot)
                {
                    return;
                }
            }
            catch (IOException)
            {
            }
            catch (System.Text.Json.JsonException)
            {
            }

            await Task.Delay(10, token);
        }

        throw new TimeoutException("the hold of " + snapshot + " never appeared");
    }
}
