using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;
using Jellyfin.Plugin.Kit.TestSupport;

namespace Jellyfin.Plugin.AccessRoles.Tests.Api;

/// <summary>
/// The library sync's endpoints over HTTP: <c>POST Sync</c> (admin only, a dry run, a run, the drift
/// policy, a read-only store), the drift in Effective and the Users list, and the Libraries tab's "grant it again" and
/// purge.
/// </summary>
public sealed class SyncApiTests
{
    private const string SyncPath = "/AccessRoles/Admin/Sync";
    private const string DiagnosticsPath = "/AccessRoles/Admin/Diagnostics";
    private const string LibrariesPath = "/AccessRoles/Admin/Libraries";

    [Fact]
    public async Task Sync_IsForAdministrators_AndADryRunWritesNothing()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role } });

        using var anonymous = await api.SendAsync(HttpMethod.Post, SyncPath, caller: null);
        using var user = await api.SendAsync(HttpMethod.Post, SyncPath, caller: "user");
        var (status, dry) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?dryRun=true");

        Assert.Equal((401, 403, 200), ((int)anonymous.StatusCode, (int)user.StatusCode, status));
        Assert.True((bool)dry!["DryRun"]!);
        Assert.Null(dry["Paused"]);
        Assert.Equal(("Manual", "Reapply", 1), ((string?)dry["Trigger"], (string?)dry["DriftPolicy"], (int)dry["ManagedUsers"]!));
        var row = Assert.Single(dry["Users"]!.AsArray())!;
        Assert.Equal(("kid", "Write", "FirstWrite"), ((string?)row["Name"], (string?)row["Action"], (string?)row["Reason"]));
        Assert.Equal([Kids.Id.ToString("N")], row["Target"]!["EnabledFolders"]!.AsArray().Select(i => (string?)i));
        Assert.Null(row["Outcome"]);
        api.VerifyNoUserWritten();
    }

    [Fact]
    public async Task ASync_WritesTheManagedUserOnly_AndSaysWhatItDid_AsTheAdmin()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var viewer = User(api, "viewer");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role } });

        var (status, run) = await api.JsonAsync(HttpMethod.Post, SyncPath, userId: User(api, "admin").Id);

        Assert.Equal(200, status);
        Assert.Equal((1, 0), ((int)run!["Written"]!, (int)run["Skipped"]!));
        var row = Assert.Single(run["Users"]!.AsArray())!;
        Assert.Equal("Written", (string?)row["Outcome"]);
        Assert.Empty(row["Added"]!.AsArray());
        Assert.Equal(["Collections", "Horror", "Movies", "Shows"], row["Removed"]!.AsArray().Select(r => (string?)r!["Name"]).Order());
        Assert.False(kid.HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal([Kids.Id], kid.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Equal([Movies.Id, Shows.Id], viewer.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        var line = api.Audit.Read(0, 1, kid.Id, AuditActions.PolicySynced, null).Items.Single();
        Assert.Equal((AuditActor.AdminKind, "admin"), (line.Actor.Kind, line.Actor.Name));
        var (_, effective) = await api.JsonAsync(HttpMethod.Get, Path(kid) + "/Effective");
        Assert.False((bool)effective!["Libraries"]!["Drift"]!);
        Assert.Equal([Kids.Id.ToString("N")], effective["Libraries"]!["LastWrite"]!["EnabledFolders"]!.AsArray().Select(i => (string?)i));
        Assert.Null(effective["Libraries"]!["PendingWrite"]);
    }

    [Fact]
    public async Task AConfirmedLockoutSave_IsRecorded_SoTheSyncWritesTheOnlyAdministratorsNarrowerAccess()
    {
        // Library roles apply to administrators; a save that leaves none with every library needs
        // confirmNoFullAdmin=true, and the sync honours that confirmation (and refuses without it).
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var admin = User(api, "admin");
        var role = await RolesApiTests.CreateKidAsync(api);

        var (refused, _) = await api.JsonAsync(HttpMethod.Put, Path(admin), body: new { RoleIds = new[] { role } });
        var (saved, _) = await api.JsonAsync(HttpMethod.Put, Path(admin) + "?confirmNoFullAdmin=true", body: new { RoleIds = new[] { role } });
        var (_, run) = await api.JsonAsync(HttpMethod.Post, SyncPath);

        Assert.Equal((409, 200), (refused, saved));
        Assert.NotNull(api.Store.Current.Assignments.Single(a => a.UserId.Equals(admin.Id)).ConfirmedNoFullAdminUtc);
        Assert.Equal("Written", (string?)run!["Users"]!.AsArray().Single(u => (string?)u!["Name"] == "admin")!["Outcome"]);
        Assert.Equal([Kids.Id], admin.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task Drift_ShowsInEffectiveAndTheUsersList_ReportOnlyLeavesIt_AndReapplyDriftRevertsIt()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role } });
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        kid.SetPermission(PermissionKind.EnableAllFolders, true);
        api.Configuration.DriftPolicy = "ReportOnly";

        var (_, users) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Users");
        var (_, effective) = await api.JsonAsync(HttpMethod.Get, Path(kid) + "/Effective");
        var (_, reported) = await api.JsonAsync(HttpMethod.Post, SyncPath);

        Assert.True((bool)users!.AsArray().Single(u => (string?)u!["Name"] == "kid")!["Drift"]!);
        Assert.True((bool)effective!["Libraries"]!["Drift"]!);
        Assert.Contains(effective["Warnings"]!.AsArray(), w => ((string?)w)!.Contains("changed outside Access Roles", StringComparison.Ordinal));
        Assert.Equal(("Drift", "ReportOnly"), ((string?)reported!["Users"]![0]!["Action"], (string?)reported["DriftPolicy"]));
        Assert.True(kid.HasPermission(PermissionKind.EnableAllFolders));

        var (_, reverted) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?reapplyDrift=true");

        Assert.Equal(("Write", "Drift", "Written"), ((string?)reverted!["Users"]![0]!["Action"], (string?)reverted["Users"]![0]!["Reason"], (string?)reverted["Users"]![0]!["Outcome"]));
        Assert.False(kid.HasPermission(PermissionKind.EnableAllFolders));
        Assert.Single(api.Audit.Read(0, 10, kid.Id, AuditActions.DriftDetected, null).Items);
    }

    [Fact]
    public async Task Revert_WithIfPlan_WritesOnlyWhatTheConfirmedDryRunListed_AndRefusesAnotherPlan()
    {
        // Revert's confirmation names a dry run's users, but the run is global, so a user to write
        // since (here viewer, assigned meanwhile) would be written unseen. ifPlan is the dry run's PlanToken.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var viewer = User(api, "viewer");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role } });
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        kid.SetPermission(PermissionKind.EnableAllFolders, true);
        api.Configuration.DriftPolicy = "ReportOnly";

        var (_, dry) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?dryRun=true&reapplyDrift=true");
        var (_, again) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?dryRun=true&reapplyDrift=true");
        await api.JsonAsync(HttpMethod.Put, Path(viewer), body: new { RoleIds = new[] { role } });
        var (stale, problem) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?reapplyDrift=true&ifPlan=" + (string?)dry!["PlanToken"]);
        var kidAfterStale = kid.HasPermission(PermissionKind.EnableAllFolders);
        var viewerAfterStale = viewer.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders);
        var (_, now) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?dryRun=true&reapplyDrift=true");
        var (ok, run) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?reapplyDrift=true&ifPlan=" + (string?)now!["PlanToken"]);

        Assert.Equal(["kid"], dry["Users"]!.AsArray().Where(u => (string?)u!["Action"] == "Write").Select(u => (string?)u!["Name"]));
        Assert.Equal((string?)dry["PlanToken"], (string?)again!["PlanToken"]);
        Assert.Equal(409, stale);
        Assert.Equal("Changed", (string?)problem!["Code"]);
        // Only whom the run would write otherwise than the dry run said: viewer, not kid.
        Assert.Equal(["viewer"], problem["Users"]!.AsArray().Select(u => (string?)u!["Name"]));
        Assert.True(kidAfterStale, "nothing was written");
        Assert.Equal([Movies.Id, Shows.Id], viewerAfterStale);
        Assert.NotEqual((string?)dry["PlanToken"], (string?)now["PlanToken"]);

        // The negative control: the token of the dry run seen now writes both.
        Assert.Equal(200, ok);
        Assert.Equal(2, (int)run!["Written"]!);
        Assert.False(kid.HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal([Kids.Id], viewer.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task Revert_ForOneUser_PlansWritesAndAuditsOnlyThem_WithATokenOfTheirOwn()
    {
        // Drift's Revert gets a per-user scope, through the same dry run, token and write: one
        // user's drift is reverted, the other drifted user is left as they are, and what was reported of them stays.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var viewer = User(api, "viewer");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role } });
        await api.JsonAsync(HttpMethod.Put, Path(viewer), body: new { RoleIds = new[] { role } });
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        kid.SetPermission(PermissionKind.EnableAllFolders, true);
        viewer.SetPermission(PermissionKind.EnableAllFolders, true);
        api.Configuration.DriftPolicy = "ReportOnly";
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        var scope = "&userId=" + kid.Id.ToString("N");

        var (_, all) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?dryRun=true&reapplyDrift=true");
        var (_, dry) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?dryRun=true&reapplyDrift=true" + scope);
        var (_, before) = await api.JsonAsync(HttpMethod.Get, DiagnosticsPath);
        var (stale, problem) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?reapplyDrift=true" + scope + "&ifPlan=" + (string?)all!["PlanToken"]);
        var kidAfterStale = kid.HasPermission(PermissionKind.EnableAllFolders);
        var (ok, run) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?reapplyDrift=true" + scope + "&ifPlan=" + (string?)dry!["PlanToken"]);
        var (_, after) = await api.JsonAsync(HttpMethod.Get, DiagnosticsPath);
        await api.JsonAsync(HttpMethod.Post, SyncPath);

        Assert.Equal(["kid", "viewer"], all["Users"]!.AsArray().Where(u => (string?)u!["Action"] == "Write").Select(u => (string?)u!["Name"]));
        Assert.Null(all["UserId"]);
        var row = Assert.Single(dry["Users"]!.AsArray())!;
        Assert.Equal(("kid", "Write", "Drift"), ((string?)row["Name"], (string?)row["Action"], (string?)row["Reason"]));
        Assert.Equal(kid.Id, Guid.Parse((string)dry["UserId"]!));
        Assert.NotEqual((string?)all["PlanToken"], (string?)dry["PlanToken"]);

        // The run for everyone's token is another plan: 409, naming only the user the run is for, and nothing written.
        Assert.Equal(409, stale);
        Assert.Equal("Changed", (string?)problem!["Code"]);
        Assert.Equal(["kid"], problem["Users"]!.AsArray().Select(u => (string?)u!["Name"]));
        Assert.True(kidAfterStale, "nothing was written");

        // The negative control: their own dry run's token reverts them, and only them.
        Assert.Equal(200, ok);
        Assert.Equal(1, (int)run!["Written"]!);
        Assert.Equal(kid.Id, Guid.Parse((string)Assert.Single(run["Users"]!.AsArray())!["UserId"]!));
        Assert.False(kid.HasPermission(PermissionKind.EnableAllFolders));
        Assert.True(viewer.HasPermission(PermissionKind.EnableAllFolders), "the other drifted user is left as they are");
        Assert.Equal(2, api.Audit.Read(0, 10, kid.Id, AuditActions.PolicySynced, null).Items.Count);
        Assert.Single(api.Audit.Read(0, 10, viewer.Id, AuditActions.PolicySynced, null).Items);

        // Diagnostics: the drift count loses kid only; the last run stays the last run for everyone.
        Assert.Equal((2, 1), ((int)before!["LibrarySync"]!["DriftCount"]!, (int)after!["LibrarySync"]!["DriftCount"]!));
        Assert.Equal((1, 2), ((int)after["LibrarySync"]!["Totals"]!["UserRuns"]!, (int)after["LibrarySync"]!["Totals"]!["Runs"]!));
        Assert.Equal(0, (int)after["LibrarySync"]!["LastRun"]!["Written"]!);

        // viewer's drift was reported once: the run for kid kept what was reported of the others, so the next run for
        // everyone doesn't report it again.
        Assert.Single(api.Audit.Read(0, 10, viewer.Id, AuditActions.DriftDetected, null).Items);
    }

    [Fact]
    public async Task Revert_ForOneUser_Is404ForAnUnknownUser_400WithConfirmRestore_AndItsTokenIsNotTheTokenOfARunForEveryone()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role } });
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        kid.SetPermission(PermissionKind.EnableAllFolders, true);
        api.Configuration.DriftPolicy = "ReportOnly";
        var scope = "&userId=" + kid.Id.ToString("N");

        var (unknown, _) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?dryRun=true&userId=" + Guid.NewGuid().ToString("N"));
        var (restore, _) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?confirmRestore=true" + scope);
        var (user, _) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?dryRun=true" + scope, caller: "user");
        var (_, dry) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?dryRun=true&reapplyDrift=true" + scope);
        var (_, all) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?dryRun=true&reapplyDrift=true");
        var (crossed, _) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?reapplyDrift=true&ifPlan=" + (string?)dry!["PlanToken"]);
        var (crossedBack, _) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?reapplyDrift=true" + scope + "&ifPlan=" + (string?)all!["PlanToken"]);

        Assert.Equal((404, 400, 403), (unknown, restore, user));
        Assert.Equal(["kid"], all["Users"]!.AsArray().Select(u => (string?)u!["Name"]));
        Assert.Equal(["kid"], dry["Users"]!.AsArray().Select(u => (string?)u!["Name"]));

        // The same write, but a confirmation of "kid only" is not a confirmation of a run for everyone, nor the other way.
        Assert.NotEqual((string?)all["PlanToken"], (string?)dry["PlanToken"]);
        Assert.Equal((409, 409), (crossed, crossedBack));
        Assert.True(kid.HasPermission(PermissionKind.EnableAllFolders), "nothing was written");
    }

    [Fact]
    public async Task Sync_WithAUserIdOfNoValue_Is400_AndIsNeverARunForEveryone()
    {
        // MVC binds "userId=" as no userId, so a per-user Revert whose id was lost ran for everyone.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role } });
        Assert.True(kid.HasPermission(PermissionKind.EnableAllFolders), "a run for everyone would write the kid");

        var (empty, problem) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?userId=");
        var (emptyDry, _) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?dryRun=true&userId=");
        var (blank, _) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?USERID=%20&reapplyDrift=true");
        var (notAGuid, _) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?userId=kid");

        Assert.Equal((400, 400, 400, 400), (empty, emptyDry, blank, notAGuid));
        Assert.Contains("userId has no value", (string?)problem!["detail"], StringComparison.Ordinal);
        Assert.True(kid.HasPermission(PermissionKind.EnableAllFolders), "nothing was written");

        // Without the key it is the run for everyone, as before.
        var (ok, _) = await api.JsonAsync(HttpMethod.Post, SyncPath);
        Assert.Equal(200, ok);
        Assert.False(kid.HasPermission(PermissionKind.EnableAllFolders));
    }

    [Fact]
    public async Task Revert_WithIfPlan_IsRefusedWhenTheUsersNativeAccessChangedSinceTheDryRun_ForOneUserAndForEveryone()
    {
        // Regression: the dry run said "added nothing; removed Shows", another administrator
        // then removed Movies natively, and the write with ifPlan wrote "added Movies", which the admin never saw. The
        // PlanToken covers the native access the run writes over too, so the write is a 409 Changed naming the user.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role }, Libraries = new { Add = new[] { Movies.Id } } });
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        kid.SetPreference(PreferenceKind.EnabledFolders, new[] { Kids.Id, Movies.Id, Shows.Id });
        api.Configuration.DriftPolicy = "ReportOnly";
        var scope = "&userId=" + kid.Id.ToString("N");

        var (_, dryUser) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?dryRun=true&reapplyDrift=true" + scope);
        var (_, dryAll) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?dryRun=true&reapplyDrift=true");
        kid.SetPreference(PreferenceKind.EnabledFolders, new[] { Kids.Id, Shows.Id });
        var synced = api.Audit.Read(0, 10, kid.Id, AuditActions.PolicySynced, null).Items.Count;
        var (staleUser, problemUser) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?reapplyDrift=true" + scope + "&ifPlan=" + (string?)dryUser!["PlanToken"]);
        var (staleAll, problemAll) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?reapplyDrift=true&ifPlan=" + (string?)dryAll!["PlanToken"]);
        var afterStale = kid.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).Order().ToArray();
        var syncedAfterStale = api.Audit.Read(0, 10, kid.Id, AuditActions.PolicySynced, null).Items.Count;
        var (_, dryNow) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?dryRun=true&reapplyDrift=true" + scope);
        var (ok, run) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?reapplyDrift=true" + scope + "&ifPlan=" + (string?)dryNow!["PlanToken"]);

        var shown = Assert.Single(dryUser["Users"]!.AsArray())!;
        Assert.Equal(("Write", "Drift"), ((string?)shown["Action"], (string?)shown["Reason"]));
        Assert.Empty(shown["Added"]!.AsArray());
        Assert.Equal(["Shows"], shown["Removed"]!.AsArray().Select(r => (string?)r!["Name"]));
        Assert.Equal((409, 409), (staleUser, staleAll));
        Assert.Equal(("Changed", "Changed"), ((string?)problemUser!["Code"], (string?)problemAll!["Code"]));
        Assert.Equal(["kid"], problemUser["Users"]!.AsArray().Select(u => (string?)u!["Name"]));
        Assert.Equal(["kid"], problemAll["Users"]!.AsArray().Select(u => (string?)u!["Name"]));
        Assert.Equal(new[] { Kids.Id, Shows.Id }.Order(), afterStale);
        Assert.Equal(synced, syncedAfterStale);

        // The dialog's Look again: the dry run seen now says "added Movies" too, and its token writes it.
        var now = Assert.Single(dryNow["Users"]!.AsArray())!;
        Assert.Equal(["Movies"], now["Added"]!.AsArray().Select(r => (string?)r!["Name"]));
        Assert.NotEqual((string?)dryUser["PlanToken"], (string?)dryNow["PlanToken"]);
        Assert.Equal((200, 1), (ok, (int)run!["Written"]!));
        Assert.Equal(new[] { Kids.Id, Movies.Id }.Order(), kid.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).Order());
    }

    [Fact]
    public async Task Revert_WithoutIfPlan_StillWritesOverANativeChange_TheNegativeControl()
    {
        // ifPlan is the confirmation; a run without one (an interval run, or an API caller who confirmed nothing) writes
        // the roles' result whatever the native access.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role }, Libraries = new { Add = new[] { Movies.Id } } });
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        kid.SetPreference(PreferenceKind.EnabledFolders, new[] { Kids.Id, Movies.Id, Shows.Id });
        api.Configuration.DriftPolicy = "ReportOnly";
        await api.JsonAsync(HttpMethod.Post, SyncPath + "?dryRun=true&reapplyDrift=true&userId=" + kid.Id.ToString("N"));
        kid.SetPreference(PreferenceKind.EnabledFolders, new[] { Kids.Id, Shows.Id });

        var (status, run) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?reapplyDrift=true&userId=" + kid.Id.ToString("N"));

        Assert.Equal((200, 1), (status, (int)run!["Written"]!));
        Assert.Equal(new[] { Kids.Id, Movies.Id }.Order(), kid.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).Order());
    }

    [Fact]
    public async Task Libraries_SayWhoCanOpenEachAndWhy_AndWhoseOverridesReferToIt_ADeletedOnesToo()
    {
        // The Libraries tab: "what each role and user gets", and "1 user override referred to it".
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var viewer = User(api, "viewer");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role } });
        await api.JsonAsync(HttpMethod.Put, Path(viewer), body: new { RoleIds = new[] { role }, Libraries = new { Add = new[] { Shows.Id }, Remove = new[] { Kids.Id } } });

        var (_, libraries) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Libraries");
        api.Libraries.RemoveAll(l => l.Id.Equals(Shows.Id));
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        var (_, after) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Libraries");

        var kids = libraries!.AsArray().Single(l => (string?)l!["Name"] == "Kids")!;
        Assert.Equal(["admin", "kid"], kids["Users"]!.AsArray().Select(u => (string?)u!["Name"]));
        Assert.Equal(["Role:Kid"], kids["Users"]![1]!["Sources"]!.AsArray().Select(s => (string?)s));
        Assert.True((bool)kids["Users"]![0]!["IsAdministrator"]!);
        Assert.Equal(["viewer"], kids["RemovedFor"]!.AsArray().Select(u => (string?)u!["Name"]));
        var shows = libraries.AsArray().Single(l => (string?)l!["Name"] == "Shows")!;
        Assert.Equal(["admin", "kid", "viewer"], shows["Users"]!.AsArray().Select(u => (string?)u!["Name"]));
        Assert.Equal(["UserAdd"], shows["Users"]![2]!["Sources"]!.AsArray().Select(s => (string?)s));
        Assert.Equal(["viewer"], shows["AddedFor"]!.AsArray().Select(u => (string?)u!["Name"]));

        // Who opens it is the native access: the kid's every library, until the sync writes the
        // role's result, marked so.
        Assert.Equal((null, "Pending", null), ((string?)shows["Users"]![0]!["Mismatch"], (string?)shows["Users"]![1]!["Mismatch"], (string?)shows["Users"]![2]!["Mismatch"]));
        Assert.Empty(shows["Users"]![1]!["Sources"]!.AsArray());

        var deleted = after!.AsArray().Single(l => (string?)l!["Id"] == Shows.Id.ToString("N"))!;
        Assert.Equal("Orphaned", (string?)deleted["State"]);
        Assert.Empty(deleted["Users"]!.AsArray());
        Assert.Equal(["viewer"], deleted["AddedFor"]!.AsArray().Select(u => (string?)u!["Name"]));
    }

    [Fact]
    public async Task Revert_ForOneAdministrator_LockoutGuardStillLooksAtEveryone()
    {
        // A run for one user plans only them, but LockoutGuard
        // must look at every user. Refused when no other enabled administrator would have every library; written when one
        // does, though that one isn't in the run.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var admin = User(api, "admin");
        var second = api.AddUser("second", admin: true);
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(second), body: new { RoleIds = new[] { role } });
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        second.SetPermission(PermissionKind.EnableAllFolders, true);
        api.Configuration.DriftPolicy = "ReportOnly";
        var scope = "&userId=" + second.Id.ToString("N");

        // The main administrator (not managed) has only Movies natively: the second is the only one with every library.
        admin.SetPermission(PermissionKind.EnableAllFolders, false);
        admin.SetPreference(PreferenceKind.EnabledFolders, new[] { Movies.Id });
        var (_, refusedDry) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?dryRun=true&reapplyDrift=true" + scope);
        var (_, refused) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?reapplyDrift=true" + scope);
        var secondAfterRefused = second.HasPermission(PermissionKind.EnableAllFolders);

        // The main administrator has every library again: the second's revert leaves one with every library.
        admin.SetPermission(PermissionKind.EnableAllFolders, true);
        var (_, allowed) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?reapplyDrift=true" + scope);

        var row = Assert.Single(refusedDry!["Users"]!.AsArray())!;
        Assert.Equal(("second", "Skip", "NoFullAdministrator"), ((string?)row["Name"], (string?)row["Action"], (string?)row["Reason"]));
        Assert.Equal(0, (int)refused!["Written"]!);
        Assert.True(secondAfterRefused, "nothing was written");
        Assert.Equal(1, (int)allowed!["Written"]!);
        Assert.Equal(("Write", "Drift"), ((string?)allowed["Users"]![0]!["Action"], (string?)allowed["Users"]![0]!["Reason"]));
        Assert.False(second.HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal([Kids.Id], second.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task ANoLibraryResultNobodyChose_IsNeverDrift_ButItsOwnState_AndIsntCounted()
    {
        // The kid's only library is deleted, so the rules would leave them none; the sync skips
        // them (NoLibraries) and leaves their access, changed natively since, as it is. That isn't drift: nothing to keep
        // or revert, no "changed outside Access Roles", not in the drift count.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role } });
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        kid.SetPreference(PreferenceKind.EnabledFolders, new[] { Kids.Id, Movies.Id });
        api.Libraries.RemoveAll(l => l.Id.Equals(Kids.Id));
        api.Configuration.DriftPolicy = "ReportOnly";

        var (_, run) = await api.JsonAsync(HttpMethod.Post, SyncPath);
        var (_, users) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Users");
        var (_, effective) = await api.JsonAsync(HttpMethod.Get, Path(kid) + "/Effective");
        var (_, diagnostics) = await api.JsonAsync(HttpMethod.Get, DiagnosticsPath);

        Assert.Equal(("Skip", "NoLibraries"), ((string?)run!["Users"]![0]!["Action"], (string?)run["Users"]![0]!["Reason"]));
        var row = users!.AsArray().Single(u => (string?)u!["Name"] == "kid")!;
        Assert.Equal((false, true, false), ((bool)row["Drift"]!, (bool)row["NoLibraries"]!, (bool)row["NoLibrariesConfirmed"]!));
        Assert.False((bool)effective!["Libraries"]!["Drift"]!);
        Assert.DoesNotContain(effective["Warnings"]!.AsArray(), w => ((string?)w)!.Contains("changed outside Access Roles", StringComparison.Ordinal));
        Assert.Contains(effective["Warnings"]!.AsArray(), w => (string?)w == "kid would have no libraries.");
        Assert.Equal(0, (int)diagnostics!["LibrarySync"]!["DriftCount"]!);
        Assert.Empty(api.Audit.Read(0, 10, kid.Id, AuditActions.DriftDetected, null).Items);
        Assert.Equal(new[] { Kids.Id, Movies.Id }, kid.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task ANoLibraryResultThatWasChosen_IsManagedLikeAnyOther_SoALibraryGivenNativelyIsDrift_CountedAndReverted()
    {
        // The other side of the skip above: Only with nothing, confirmed, is written, and a library given natively since is drift.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { Libraries = new { Mode = "Only", ConfirmNoLibraries = true } });
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        var written = kid.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders);
        kid.SetPreference(PreferenceKind.EnabledFolders, new[] { Movies.Id });
        api.Configuration.DriftPolicy = "ReportOnly";

        var (_, run) = await api.JsonAsync(HttpMethod.Post, SyncPath);
        var (_, users) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Users");
        var (_, diagnostics) = await api.JsonAsync(HttpMethod.Get, DiagnosticsPath);
        var (_, reverted) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?reapplyDrift=true&userId=" + kid.Id.ToString("N"));

        Assert.Empty(written);
        Assert.Equal(("Drift", "ReportOnly"), ((string?)run!["Users"]![0]!["Action"], (string?)run["Users"]![0]!["Reason"]));
        var row = users!.AsArray().Single(u => (string?)u!["Name"] == "kid")!;
        Assert.Equal((true, true, true), ((bool)row["Drift"]!, (bool)row["NoLibraries"]!, (bool)row["NoLibrariesConfirmed"]!));
        Assert.Equal(1, (int)diagnostics!["LibrarySync"]!["DriftCount"]!);
        Assert.Single(api.Audit.Read(0, 10, kid.Id, AuditActions.DriftDetected, null).Items);
        Assert.Equal(1, (int)reverted!["Written"]!);
        Assert.Empty(kid.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task Libraries_ListWhoOpensEachNatively_MarkingADriftReportOnlyKeeps_AndAWriteTheNextRunMakes()
    {
        // The kid natively opened a library under ReportOnly and wasn't listed, as the
        // rules' result was. Who opens a library is the native access; where it isn't the rules' result, the row says why.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role } });
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        var (_, synced) = await api.JsonAsync(HttpMethod.Get, LibrariesPath);
        kid.SetPreference(PreferenceKind.EnabledFolders, new[] { Movies.Id });

        var (_, reapply) = await api.JsonAsync(HttpMethod.Get, LibrariesPath);
        api.Configuration.DriftPolicy = "ReportOnly";
        var (_, reportOnly) = await api.JsonAsync(HttpMethod.Get, LibrariesPath);

        // In sync: the kid opens Kids by the role, nothing marked.
        var kidRow = Row(synced, "Kids", "Users", "kid");
        Assert.Equal(["Role:Kid"], kidRow["Sources"]!.AsArray().Select(s => (string?)s));
        Assert.Null((string?)kidRow["Mismatch"]);
        Assert.Empty(Library(synced, "Kids")["NotOpenedBy"]!.AsArray());
        Assert.DoesNotContain("kid", Names(Library(synced, "Movies")["Users"]!));

        // Changed natively: the kid opens Movies, which the rules don't give, and not Kids, which they do. Under Reapply the
        // next run writes it back; under ReportOnly the drift is kept.
        foreach (var (libraries, why) in new[] { (reapply, "Pending"), (reportOnly, "Drift") })
        {
            var opened = Row(libraries, "Movies", "Users", "kid");
            Assert.Empty(opened["Sources"]!.AsArray());
            Assert.Equal(why, (string?)opened["Mismatch"]);
            Assert.DoesNotContain("kid", Names(Library(libraries, "Kids")["Users"]!));
            var given = Row(libraries, "Kids", "NotOpenedBy", "kid");
            Assert.Equal(["Role:Kid"], given["Sources"]!.AsArray().Select(s => (string?)s));
            Assert.Equal(why, (string?)given["Mismatch"]);

            // An unmanaged user's access is their own: listed, never marked, never "not opened".
            var admin = Row(libraries, "Movies", "Users", "admin");
            Assert.Equal(["Native"], admin["Sources"]!.AsArray().Select(s => (string?)s));
            Assert.Null((string?)admin["Mismatch"]);
            Assert.All(libraries!.AsArray(), l => Assert.DoesNotContain("viewer", Names(l!["NotOpenedBy"]!)));
        }
    }

    [Fact]
    public async Task Libraries_MarkAUserTheRulesWouldLeaveNoLibraryWhoOpensOneNatively_AsNoLibraries()
    {
        // The kid's role grants only Kids; Kids is deleted, so the rules would leave the kid no library, which the sync never
        // writes by accident (a skip): the kid still opens Movies natively, marked NoLibraries.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role } });
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        kid.SetPreference(PreferenceKind.EnabledFolders, new[] { Kids.Id, Movies.Id });
        api.Libraries.RemoveAll(l => l.Id.Equals(Kids.Id));
        var (_, run) = await api.JsonAsync(HttpMethod.Post, SyncPath);

        var (_, libraries) = await api.JsonAsync(HttpMethod.Get, LibrariesPath);

        Assert.Equal(("Skip", "NoLibraries"), ((string?)run!["Users"]![0]!["Action"], (string?)run["Users"]![0]!["Reason"]));
        Assert.Equal("NoLibraries", (string?)Row(libraries, "Movies", "Users", "kid")["Mismatch"]);
        Assert.All(libraries!.AsArray(), l => Assert.DoesNotContain("kid", Names(l!["NotOpenedBy"]!)));
    }

    [Theory]
    [InlineData("LibrarySyncDisabled")]
    [InlineData("EnforcementDisabled")]
    [InlineData("SafeMode")]
    public async Task Libraries_MarkWhatThePausedSyncHasntWritten_WithThePause(string pause)
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var viewer = User(api, "viewer");
        var role = await RolesApiTests.CreateKidAsync(api);
        switch (pause)
        {
            case "LibrarySyncDisabled":
                api.Configuration.LibrarySyncEnabled = false;
                break;
            case "EnforcementDisabled":
                api.Configuration.EnforcementEnabled = false;
                break;
            default:
                await File.WriteAllTextAsync(api.Web.Services.GetRequiredService<AccessRolesPaths>().SafeModeFile, string.Empty, TestContext.Current.CancellationToken);
                break;
        }

        await api.JsonAsync(HttpMethod.Put, Path(viewer), body: new { RoleIds = new[] { role } });
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        var (_, libraries) = await api.JsonAsync(HttpMethod.Get, LibrariesPath);

        Assert.Equal([Movies.Id, Shows.Id], viewer.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Equal(pause, (string?)Row(libraries, "Movies", "Users", "viewer")["Mismatch"]);
        Assert.Equal(pause, (string?)Row(libraries, "Kids", "NotOpenedBy", "viewer")["Mismatch"]);
        Assert.DoesNotContain("viewer", Names(Library(libraries, "Kids")["Users"]!));
    }

    [Fact]
    public async Task GrantAgain_IsForARecreatedLibrary_AndGivesItBack()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var created = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        SetCreated(api, Kids.Id, created);
        var kid = User(api, "kid");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role } });
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        var (notRecreated, problem) = await api.JsonAsync(HttpMethod.Post, Grant(Kids.Id));
        var (unknown, _) = await api.JsonAsync(HttpMethod.Post, Grant(Guid.NewGuid()));
        SetCreated(api, Kids.Id, created.AddDays(3));
        await api.JsonAsync(HttpMethod.Post, SyncPath);

        var (_, libraries) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Libraries");
        var (status, change) = await api.JsonAsync(HttpMethod.Post, Grant(Kids.Id));
        var (_, after) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Libraries");
        var (_, written) = await api.JsonAsync(HttpMethod.Post, SyncPath);

        Assert.Equal((409, 404), (notRecreated, unknown));
        Assert.Contains("nothing to grant again", (string?)problem!["detail"], StringComparison.Ordinal);
        var kids = libraries!.AsArray().Single(l => (string?)l!["Name"] == "Kids")!;
        Assert.Equal((true, true), ((bool)kids["Recreated"]!, (bool)kids["Known"]!));
        Assert.Equal(200, status);
        Assert.Equal("kid", (string?)change!["Preview"]!["Users"]!.AsArray().Single()!["Name"]);
        Assert.False((bool)after!.AsArray().Single(l => (string?)l!["Name"] == "Kids")!["Recreated"]!);
        Assert.Equal("RulesChanged", (string?)written!["Users"]![0]!["Reason"]);
        Assert.Equal([Kids.Id], kid.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Single(api.Audit.Read(0, 10, null, AuditActions.LibraryGrantedAgain, null).Items);
        Assert.Single(api.Audit.Read(0, 10, null, AuditActions.LibraryRecreated, null).Items);
    }

    [Fact]
    public async Task Purge_IsForADeletedLibrary_AndRemovesItsReferences()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        var (exists, _) = await api.JsonAsync(HttpMethod.Post, Purge(Kids.Id));
        api.Libraries.RemoveAll(l => l.Id.Equals(Kids.Id));
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        var (_, before) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Libraries");

        var (status, _) = await api.JsonAsync(HttpMethod.Post, Purge(Kids.Id));
        var (again, _) = await api.JsonAsync(HttpMethod.Post, Purge(Kids.Id));

        Assert.Equal((409, 200, 404), (exists, status, again));
        Assert.Equal("Orphaned", (string?)before!.AsArray().Single(l => (string?)l!["Name"] == "Kids")!["State"]);
        Assert.Empty(api.Store.Current.Roles.Single(r => r.Id.Equals(role)).Libraries.Refs);
        Assert.DoesNotContain(api.Store.Current.KnownLibraries, k => k.Id.Equals(Kids.Id));
        Assert.Single(api.Audit.Read(0, 10, null, AuditActions.LibraryPurged, null).Items);
    }

    [Fact]
    public async Task Purge_KeepsTheExclusionsOfAllExceptRolesAndRemoveLists_SoALibraryCreatedAgainUnderItsIdStaysLeftOut()
    {
        // Regression (1.0.1): the purge used to drop the All except role's
        // ref and the known library, so Kids created again under its id was a new library that role gave everyone.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var created = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        SetCreated(api, Kids.Id, created);
        var kid = User(api, "kid");
        var viewer = User(api, "viewer");
        var only = await RolesApiTests.CreateKidAsync(api);
        var (_, allBut) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: new { Name = "All but Kids", Libraries = new { Mode = "AllExcept", Refs = new[] { new { Id = Kids.Id } } } });
        var (_, everything) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: new { Name = "Everything", Libraries = new { Mode = "All" } });
        var allButId = Guid.Parse((string)allBut!["Role"]!["Id"]!);
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { allButId } });
        await api.JsonAsync(HttpMethod.Put, Path(viewer), body: new { RoleIds = new[] { Guid.Parse((string)everything!["Role"]!["Id"]!) }, Libraries = new { Remove = new[] { Kids.Id } } });
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        var kids = api.Libraries.Single(l => l.Id.Equals(Kids.Id));
        api.Libraries.RemoveAll(l => l.Id.Equals(Kids.Id));
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        var (_, before) = await api.JsonAsync(HttpMethod.Get, LibrariesPath);

        var (status, _) = await api.JsonAsync(HttpMethod.Post, Purge(Kids.Id));
        var (_, after) = await api.JsonAsync(HttpMethod.Get, LibrariesPath);
        var (again, problem) = await api.JsonAsync(HttpMethod.Post, Purge(Kids.Id));
        api.Libraries.Add(kids with { CreatedUtc = created.AddDays(3) });
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        var (_, back) = await api.JsonAsync(HttpMethod.Get, LibrariesPath);

        Assert.Equal((true, 200), ((bool)Library(before, "Kids")["Purgeable"]!, status));
        Assert.Empty(api.Store.Current.Roles.Single(r => r.Id.Equals(only)).Libraries.Refs);
        Assert.Equal([Kids.Id], api.Store.Current.Roles.Single(r => r.Id.Equals(allButId)).Libraries.Refs.Select(r => r.Id));
        Assert.Equal([Kids.Id], api.Store.Current.Assignments.Single(a => a.UserId.Equals(viewer.Id)).Libraries.Remove);
        Assert.Equal((false, true, "Orphaned"), ((bool)Library(after, "Kids")["Purgeable"]!, (bool)Library(after, "Kids")["Known"]!, (string?)Library(after, "Kids")["State"]));
        Assert.Equal(409, again);
        Assert.Contains("Only exclusions refer to library", (string?)problem!["detail"], StringComparison.Ordinal);
        Assert.True((bool)Library(back, "Kids")["Recreated"]!);
        Assert.DoesNotContain(Kids.Id, kid.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Contains(Movies.Id, kid.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.DoesNotContain(Kids.Id, viewer.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.False(viewer.HasPermission(PermissionKind.EnableAllFolders));
        var line = Assert.Single(api.Audit.Read(0, 10, null, AuditActions.LibraryPurged, null).Items);
        var kept = JsonNode.Parse(JsonSerializer.Serialize(line.Details!["ExclusionsKept"]))!;
        Assert.Equal(allButId.ToString("N"), (string?)kept["Roles"]![0]);
        Assert.Equal(viewer.Id.ToString("N"), (string?)kept["Users"]![0]);
    }

    [Fact]
    public async Task APurgedLibraryCreatedAgain_StaysLeftOutOfItsExclusions_AlsoAfterGrantAgain_AsThe409Says()
    {
        // 1.0.2: the 409, the page and the 1.0.1 changelog said such a library stays left out "until you grant it again";
        // Grant again takes off the re-created mark only, so the All except role and the Remove list still leave it out.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var created = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        SetCreated(api, Kids.Id, created);
        var kid = User(api, "kid");
        var viewer = User(api, "viewer");
        var (_, allBut) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: new { Name = "All but Kids", Libraries = new { Mode = "AllExcept", Refs = new[] { new { Id = Kids.Id } } } });
        var (_, everything) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: new { Name = "Everything", Libraries = new { Mode = "All" } });
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { Guid.Parse((string)allBut!["Role"]!["Id"]!) } });
        await api.JsonAsync(HttpMethod.Put, Path(viewer), body: new { RoleIds = new[] { Guid.Parse((string)everything!["Role"]!["Id"]!) }, Libraries = new { Remove = new[] { Kids.Id } } });
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        var kids = api.Libraries.Single(l => l.Id.Equals(Kids.Id));
        api.Libraries.RemoveAll(l => l.Id.Equals(Kids.Id));
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        await api.JsonAsync(HttpMethod.Post, Purge(Kids.Id));

        var (again, problem) = await api.JsonAsync(HttpMethod.Post, Purge(Kids.Id));
        api.Libraries.Add(kids with { CreatedUtc = created.AddDays(3) });
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        var (granted, _) = await api.JsonAsync(HttpMethod.Post, Grant(Kids.Id));
        await api.JsonAsync(HttpMethod.Post, SyncPath);
        var (_, after) = await api.JsonAsync(HttpMethod.Get, LibrariesPath);

        Assert.Equal((409, 200), (again, granted));
        Assert.Equal(
            "Only exclusions refer to library \"Kids\" now (an All except… role or a user's own Remove list): they keep leaving it out, so a library created again under its id stays left out of them, also after Grant again. To drop one, take the library out of that role or out of that user's Remove list.",
            (string?)problem!["detail"]);
        Assert.False((bool)Library(after, "Kids")["Recreated"]!);
        Assert.DoesNotContain(Kids.Id, kid.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Contains(Movies.Id, kid.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.DoesNotContain(Kids.Id, viewer.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.False(viewer.HasPermission(PermissionKind.EnableAllFolders));
    }

    [Fact]
    public async Task OnAReadOnlyStore_ASyncIs503_AndADryRunSaysItIsPaused()
    {
        await using var api = await AccessRolesApi.StartAsync();
        api.AddUser("admin", admin: true);
        var file = api.Store.Documents.FilePath;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, """{"Version":2}""", TestContext.Current.CancellationToken);

        var (real, _) = await api.JsonAsync(HttpMethod.Post, SyncPath);
        var (dry, report) = await api.JsonAsync(HttpMethod.Post, SyncPath + "?dryRun=true");

        Assert.Equal((503, 200), (real, dry));
        Assert.Equal("StoreReadOnly", (string?)report!["Paused"]);
        Assert.Equal("""{"Version":2}""", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
    }

    private static JsonNode Library(JsonNode? libraries, string name) => libraries!.AsArray().Single(l => (string?)l!["Name"] == name)!;

    private static JsonNode Row(JsonNode? libraries, string library, string list, string user) => Library(libraries, library)[list]!.AsArray().Single(u => (string?)u!["Name"] == user)!;

    private static string?[] Names(JsonNode users) => [.. users.AsArray().Select(u => (string?)u!["Name"])];

    private static void SetCreated(AccessRolesApi api, Guid id, DateTime created)
    {
        var index = api.Libraries.FindIndex(l => l.Id.Equals(id));
        api.Libraries[index] = api.Libraries[index] with { CreatedUtc = created };
    }

    private static string Grant(Guid id) => "/AccessRoles/Admin/Libraries/" + id.ToString("N") + "/GrantAgain";

    private static string Purge(Guid id) => "/AccessRoles/Admin/Libraries/" + id.ToString("N") + "/Purge";

    private static string Path(User user) => "/AccessRoles/Admin/Users/" + user.Id.ToString("N");

    private static User User(AccessRolesApi api, string name) => api.UserManager.Object.AllUsers().Single(u => u.Username == name);
}
