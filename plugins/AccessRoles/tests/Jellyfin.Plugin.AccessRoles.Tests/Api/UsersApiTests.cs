using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Api;

/// <summary>
/// The assignments, Effective, Preview, Libraries and Audit endpoints over HTTP:
/// every id resolved, <c>IfUpdatedAt</c>, the refusals with their codes, leaving management with each
/// choice, the lockout confirmation, the ledger, the audit lines, and no user written.
/// </summary>
public sealed class UsersApiTests
{
    [Fact]
    public async Task AssigningARole_GivesTheEffectiveRulesWithTheirSources_RecordsTheLedger_AndWritesNoUser()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var role = await RolesApiTests.CreateKidAsync(api);

        var (status, change) = await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role } });

        Assert.Equal(200, status);
        var effective = change!["Effective"]!;
        Assert.Equal("Managed", (string?)effective["State"]);
        Assert.Equal("Role:Kid", (string?)effective["Locks"]!["Profile"]!["Source"]);
        Assert.True((bool)effective["Locks"]!["Profile"]!["Locked"]!);
        Assert.Equal("Default", (string?)effective["Locks"]!["Playback"]!["Source"]);

        // Each lock says whether the server enforces it in this build (the admin surfaces and user directory since 0.9.7).
        Assert.True((bool)effective["Locks"]!["Profile"]!["Enforced"]!);
        Assert.True((bool)effective["Locks"]!["AdminSurfaces"]!["Enforced"]!);
        Assert.True((bool)effective["Locks"]!["UserDirectory"]!["Enforced"]!);

        // The hidden-only sections live in the browser, so the server never enforces them.
        Assert.False((bool)effective["Locks"]!["DisplayDevice"]!["Enforced"]!);
        Assert.False((bool)effective["Locks"]!["Controls"]!["Enforced"]!);
        Assert.True((bool)effective["Locks"]!["DisplayAccount"]!["Enforced"]!);
        Assert.Equal("Roles", (string?)effective["Libraries"]!["StartingSet"]);
        Assert.Equal(["Kids"], effective["Libraries"]!["Visible"]!.AsArray().Select(v => (string?)v!["Name"]));
        Assert.True((bool)effective["Libraries"]!["Native"]!["EnableAllFolders"]!);
        Assert.False((bool)effective["Libraries"]!["PendingWrite"]!["EnableAllFolders"]!);
        Assert.Equal([Kids.Id.ToString("N")], effective["Libraries"]!["PendingWrite"]!["Add"]!.AsArray().Select(a => (string?)a));
        var row = Assert.Single(change["Preview"]!["Users"]!.AsArray())!;
        Assert.True((bool)row["PolicyWrite"]!);
        Assert.Equal(8, row["LocksAdded"]!.AsArray().Count);
        Assert.Equal([kid.Id.ToString("N")], change["Changed"]!.AsArray().Select(c => (string?)c));

        var entry = api.Store.Current.Ledger[kid.Id];
        Assert.True(entry.OriginalEnableAllFolders);
        Assert.Null(entry.LastWrite);
        Assert.Equal("kid", api.Store.Current.KnownUsers.NameOf(kid.Id));
        var line = api.Audit.Read(0, 1, kid.Id, "AssignmentChanged", null).Items.Single();
        Assert.Equal("kid", line.TargetName);
        Assert.True(((JsonElement)line.Details!["PolicyWritePending"]!).GetBoolean());

        // The roles API writes no user's policy (the library sync does): nothing reached the user manager, and the user is as before.
        api.VerifyNoUserWritten();
        Assert.True(kid.HasPermission(PermissionKind.EnableAllFolders));
    }

    [Fact]
    public async Task IfUpdatedAt_IsNoneForANewAssignment_AndTheAssignmentsTimeAfterwards()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var role = await RolesApiTests.CreateKidAsync(api);
        var (_, first) = await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role } });
        var token = DateTime.Parse((string)first!["Effective"]!["UpdatedUtc"]!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        var (blind, problem) = await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role }, LockOverrides = new { QuickConnect = "Unlock" } });
        var (ok, change) = await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role }, LockOverrides = new { QuickConnect = "Unlock" }, IfUpdatedAt = token });

        Assert.Equal(409, blind);
        Assert.Equal(AccessRolesException.Changed, (string?)problem!["Code"]);
        Assert.Equal(200, ok);
        Assert.Equal("UserOverride", (string?)change!["Effective"]!["Locks"]!["QuickConnect"]!["Source"]);
        Assert.False((bool)change["Effective"]!["Locks"]!["QuickConnect"]!["Locked"]!);
    }

    [Theory]
    [InlineData("""{"RoleIds":["5b0e7d2a3c4f4a1b9e8d1f2a3b4c5d6e"]}""", 400)]
    [InlineData("""{"LockOverrides":{"Downloads":"Lock"}}""", 400)]
    [InlineData("""{"LockOverrides":{"3":"Lock"}}""", 400)]
    [InlineData("""{"LockOverrides":{"Home":"Sometimes"}}""", 400)]
    [InlineData("""{"State":"CannotManage"}""", 400)]
    [InlineData("""{"Libraries":{"Mode":"Only","Add":["3c1d9f8e7b6a4c5d8e9f0a1b2c3d4e5f"]}}""", 400)]
    [InlineData("""{"UserId":"a1c4e5f607184293a4b5c6d7e8f90a1b"}""", 400)]
    public async Task AnInvalidAssignment_Is400(string body, int expected)
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();

        var (status, _) = await api.JsonAsync(HttpMethod.Put, Path(User(api, "kid")), body: body);

        Assert.Equal(expected, status);
        Assert.Empty(api.Store.Current.Assignments);
    }

    [Fact]
    public async Task AUserTheServerDoesntHave_Is404_AndALibraryBothAddedAndRemovedIs400()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();

        var (unknown, _) = await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Users/" + Guid.NewGuid().ToString("N"), body: new { State = "Managed" });
        var (both, _) = await api.JsonAsync(HttpMethod.Put, Path(User(api, "kid")), body: new { Libraries = new { Add = new[] { Movies.Id }, Remove = new[] { Movies.Id } } });

        Assert.Equal(404, unknown);
        Assert.Equal(400, both);
    }

    [Fact]
    public async Task AnAccidentalEmptyResult_Is409NoLibraries_AndTheConfirmedChoiceIsSaved()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");

        var (refused, problem) = await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { Libraries = new { Mode = "Only" } });
        var (ok, change) = await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { Libraries = new { Mode = "Only", ConfirmNoLibraries = true } });

        Assert.Equal(409, refused);
        Assert.Equal(AccessRolesException.NoLibraries, (string?)problem!["Code"]);
        Assert.Equal("kid", (string?)problem["Users"]![0]!["Name"]);
        Assert.Equal(200, ok);
        Assert.True((bool)change!["Effective"]!["Libraries"]!["NoLibrariesConfirmed"]!);
    }

    [Fact]
    public async Task ALibraryRoleForTheOnlyAdministrator_NeedsConfirmNoFullAdmin_AndNoLockAppliesToThem()
    {
        // Library roles apply to admins; LockoutGuard asks before no enabled admin has every library.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var admin = User(api, "admin");
        var role = await RolesApiTests.CreateKidAsync(api);

        var (refused, problem) = await api.JsonAsync(HttpMethod.Put, Path(admin), body: new { RoleIds = new[] { role } });
        var (ok, change) = await api.JsonAsync(HttpMethod.Put, Path(admin) + "?confirmNoFullAdmin=true", body: new { RoleIds = new[] { role } });

        Assert.Equal(409, refused);
        Assert.Equal(AccessRolesException.NoFullAdministrator, (string?)problem!["Code"]);
        Assert.Equal(200, ok);
        var effective = change!["Effective"]!;
        Assert.True((bool)effective["IsAdministrator"]!);
        Assert.All(effective["Locks"]!.AsObject(), l => Assert.Equal("Administrator", (string?)l.Value!["Source"]));
        Assert.Equal(["Kids"], effective["Libraries"]!["Visible"]!.AsArray().Select(v => (string?)v!["Name"]));
        Assert.Contains(effective["Warnings"]!.AsArray(), w => ((string?)w)!.StartsWith("Library roles apply to administrators too", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LeavingManagement_NeedsAChoice_AndRestoreRecordsTheOriginalAccessForTheSyncToWrite()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var viewer = User(api, "viewer");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(viewer), body: new { RoleIds = new[] { role } });
        var token = api.Store.Current.Assignments.Single().UpdatedUtc;

        // Someone changes the viewer's library access natively meanwhile (the roles API never writes it).
        viewer.SetPreference(PreferenceKind.EnabledFolders, [Horror.Id]);

        var (refused, problem) = await api.JsonAsync(HttpMethod.Put, Path(viewer), body: new { RoleIds = Array.Empty<Guid>(), IfUpdatedAt = token });
        var (ok, change) = await api.JsonAsync(HttpMethod.Put, Path(viewer) + "?onUnmanage=Restore", body: new { RoleIds = Array.Empty<Guid>(), IfUpdatedAt = token });

        Assert.Equal(400, refused);
        Assert.Equal(AccessRolesException.UnmanageChoiceRequired, (string?)problem!["Code"]);
        Assert.Equal(200, ok);
        Assert.Equal("NotManaged", (string?)change!["Effective"]!["State"]);
        var leaving = change["Preview"]!["Users"]![0]!["LeavesManagement"]!;
        Assert.Equal("Restore", (string?)leaving["Choice"]);
        Assert.Equal(Catalog.Order([Movies.Id, Shows.Id]).Select(i => i.ToString("N")), leaving["After"]!["EnabledFolders"]!.AsArray().Select(f => (string?)f));
        Assert.Empty(api.Store.Current.Assignments);
        var release = api.Store.Current.Ledger[viewer.Id].Release!;
        Assert.True(release.Choice.Is(AccessRolesUnmanageChoice.Restore));
        Assert.Equal(Catalog.Order([Movies.Id, Shows.Id]), release.EnabledFolders);
        api.VerifyNoUserWritten();
    }

    [Fact]
    public async Task Excluded_TakesTheUserOutOfLibraryManagement_KeepFreezesTheirAccess_AndTheOriginalStaysRecorded()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var viewer = User(api, "viewer");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(viewer), body: new { RoleIds = new[] { role } });
        var token = api.Store.Current.Assignments.Single().UpdatedUtc;

        var (status, change) = await api.JsonAsync(HttpMethod.Put, Path(viewer) + "?onUnmanage=Keep", body: new { RoleIds = new[] { role }, State = "Excluded", IfUpdatedAt = token });

        Assert.Equal(200, status);
        Assert.Equal("Excluded", (string?)change!["Effective"]!["State"]);
        Assert.Equal(["Movies", "Shows"], change["Preview"]!["Users"]![0]!["LeavesManagement"]!["FrozenAt"]!.AsArray().Select(f => (string?)f));
        Assert.True(api.Store.Current.Assignments.Single().State.Is(AccessRolesAssignmentState.Excluded));

        // A pause: the ledger keeps the original access, without a release, while the assignment stays.
        var entry = api.Store.Current.Ledger[viewer.Id];
        Assert.Equal([Movies.Id, Shows.Id], entry.OriginalEnabledFolders);
        Assert.Null(entry.Release);
        Assert.Single(api.Audit.Read(0, 10, viewer.Id, "LibraryAccessFrozen", null).Items);
    }

    [Fact]
    public async Task APausedUser_ManagedAgain_IsRestoredToTheirRealOriginalAccess_AndThePendingReleaseIsShown()
    {
        // A case seen in a sandbox: viewer natively sees Movies and Shows; given Kid, the ledger records them; then
        // their native access becomes Horror (standing in for the library sync's write); Excluded with Keep; Managed again; Unmanage with
        // Restore must restore Movies and Shows, not Horror.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var viewer = User(api, "viewer");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(viewer), body: new { RoleIds = new[] { role } });
        viewer.SetPreference(PreferenceKind.EnabledFolders, [Horror.Id]);
        var (paused, _) = await api.JsonAsync(HttpMethod.Put, Path(viewer) + "?onUnmanage=Keep", body: new { RoleIds = new[] { role }, State = "Excluded", IfUpdatedAt = Token(api, viewer) });
        var (again, _) = await api.JsonAsync(HttpMethod.Put, Path(viewer), body: new { RoleIds = new[] { role }, State = "Managed", IfUpdatedAt = Token(api, viewer) });

        var (dry, preview) = await api.JsonAsync(HttpMethod.Post, Path(viewer) + "/Unmanage?dryRun=true", body: new { Choice = "Restore" });
        var (ok, _) = await api.JsonAsync(HttpMethod.Post, Path(viewer) + "/Unmanage", body: new { Choice = "Restore" });
        var (_, effective) = await api.JsonAsync(HttpMethod.Get, Path(viewer) + "/Effective");
        var (_, users) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Users?state=NotManaged");
        var (_, diagnostics) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Diagnostics");

        Assert.Equal((200, 200, 200, 200), (paused, again, dry, ok));
        var original = Catalog.Order([Movies.Id, Shows.Id]).Select(i => i.ToString("N")).ToList();
        Assert.Equal(original, preview!["Preview"]!["Users"]![0]!["LeavesManagement"]!["After"]!["EnabledFolders"]!.AsArray().Select(f => (string?)f));
        var release = api.Store.Current.Ledger[viewer.Id].Release!;
        Assert.Equal(Catalog.Order([Movies.Id, Shows.Id]), release.EnabledFolders);
        Assert.Equal([Horror.Id], release.Baseline!.EnabledFolders);

        // The release is visible: Effective's PendingRelease with its baseline and PendingWrite, the Users row, Diagnostics.
        var libraries = effective!["Libraries"]!;
        Assert.Equal("NotManaged", (string?)effective["State"]);
        Assert.Equal("Restore", (string?)libraries["PendingRelease"]!["Choice"]);
        Assert.Equal(original, libraries["PendingRelease"]!["EnabledFolders"]!.AsArray().Select(f => (string?)f));
        Assert.Equal([Horror.Id.ToString("N")], libraries["PendingRelease"]!["Baseline"]!["EnabledFolders"]!.AsArray().Select(f => (string?)f));
        Assert.True((bool)libraries["PendingRelease"]!["BaselineMatches"]!);
        Assert.Equal(original, libraries["PendingWrite"]!["Add"]!.AsArray().Select(f => (string?)f));
        Assert.Equal([Horror.Id.ToString("N")], libraries["PendingWrite"]!["Remove"]!.AsArray().Select(f => (string?)f));
        var row = users!.AsArray().Single(u => (string?)u!["Name"] == "viewer")!;
        Assert.True((bool)row["PendingWrite"]!);
        Assert.Equal("Not managed; Restore waiting for the library sync", (string?)row["Summary"]);
        Assert.False((bool)users.AsArray().Single(u => (string?)u!["Name"] == "kid")!["PendingWrite"]!);
        Assert.Equal(1, (int)diagnostics!["Records"]!["PendingReleases"]!);

        // An admin then changes the unmanaged user's access natively: the sync won't overwrite it (the baseline no longer
        // matches), so nothing is pending and Effective says why.
        viewer.SetPreference(PreferenceKind.EnabledFolders, [Kids.Id]);
        var (_, edited) = await api.JsonAsync(HttpMethod.Get, Path(viewer) + "/Effective");
        Assert.False((bool)edited!["Libraries"]!["PendingRelease"]!["BaselineMatches"]!);
        Assert.Null(edited["Libraries"]!["PendingWrite"]);
        Assert.Contains(edited["Warnings"]!.AsArray(), w => ((string?)w)!.Contains("won't overwrite that change", StringComparison.Ordinal));
        api.VerifyNoUserWritten();
    }

    [Fact]
    public async Task Unmanage_NeedsAChoice_RemovesTheAssignment_AndGrantAllIsLeftForTheSync()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var viewer = User(api, "viewer");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(viewer), body: new { RoleIds = new[] { role } });

        var (missing, _) = await api.JsonAsync(HttpMethod.Post, Path(viewer) + "/Unmanage", body: new { });
        var (ok, change) = await api.JsonAsync(HttpMethod.Post, Path(viewer) + "/Unmanage", body: new { Choice = "GrantAll" });
        var (unknown, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/" + Guid.NewGuid().ToString("N") + "/Unmanage", body: new { Choice = "Keep" });

        Assert.Equal(400, missing);
        Assert.Equal(200, ok);
        Assert.Equal("NotManaged", (string?)change!["Effective"]!["State"]);
        Assert.Empty(api.Store.Current.Assignments);
        Assert.True(api.Store.Current.Ledger[viewer.Id].Release!.EnableAllFolders);
        Assert.Equal(404, unknown);
        api.VerifyNoUserWritten();
    }

    [Fact]
    public async Task Bulk_GivesAndTakesRoles_ChecksEveryId_AndNeedsAChoiceForUsersItTakesOut()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var viewer = User(api, "viewer");
        var role = await RolesApiTests.CreateKidAsync(api);

        var (given, add) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk", body: new { UserIds = new[] { kid.Id, viewer.Id }, AddRoleIds = new[] { role } });
        var (refused, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk", body: new { UserIds = new[] { kid.Id, viewer.Id }, RemoveRoleIds = new[] { role } });
        var (taken, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk", body: new { UserIds = new[] { kid.Id, viewer.Id }, RemoveRoleIds = new[] { role }, OnUnmanage = "Keep" });
        var (unknownUser, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk", body: new { UserIds = new[] { Guid.NewGuid() }, AddRoleIds = new[] { role } });
        var (unknownRole, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk", body: new { UserIds = new[] { kid.Id }, AddRoleIds = new[] { Guid.NewGuid() } });
        var (nothing, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk", body: new { UserIds = new[] { kid.Id } });
        var (none, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk", body: new { UserIds = Array.Empty<Guid>(), AddRoleIds = new[] { role } });

        Assert.Equal(200, given);
        Assert.Equal(2, add!["Changed"]!.AsArray().Count);
        Assert.Equal(400, refused);
        Assert.Equal(200, taken);
        Assert.Empty(api.Store.Current.Assignments);
        Assert.Equal(404, unknownUser);
        Assert.Equal(400, unknownRole);
        Assert.Equal(400, nothing);
        Assert.Equal(400, none);
        Assert.Equal(4, api.Audit.Read(0, 50, null, "AssignmentChanged", null).TotalRecordCount);
    }

    [Fact]
    public async Task Bulk_WithIfUpdatedAt_IsRefusedWhenAUserChangedSinceThePreview_NamesThem_AndWritesNothing()
    {
        // Regression: the page previewed "Newly locked: Profile, Home screen", another admin then unlocked
        // both for the user, and Assign still saved. IfUpdatedAt names each user as previewed; one changed since is a 409.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var viewer = User(api, "viewer");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(viewer), body: new { RoleIds = new[] { role } });
        var viewerRead = Token(api, viewer);
        object Bulk(DateTime? viewerToken) => new
        {
            UserIds = new[] { kid.Id, viewer.Id },
            AddRoleIds = new[] { role },
            IfUpdatedAt = new object[] { new { UserId = kid.Id, UpdatedUtc = (DateTime?)null }, new { UserId = viewer.Id, UpdatedUtc = viewerToken } }
        };

        var (dry, preview) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk?dryRun=true", body: Bulk(viewerRead));
        await api.JsonAsync(HttpMethod.Put, Path(viewer), body: new { RoleIds = new[] { role }, LockOverrides = new { Profile = "Unlock", Home = "Unlock" }, IfUpdatedAt = viewerRead });
        var audit = api.Audit.Read(0, 1, null, null, null).TotalRecordCount;
        var (stale, problem) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk", body: Bulk(viewerRead));
        var kidAfterStale = api.Store.Current.Assignments.Any(a => a.UserId.Equals(kid.Id));
        var auditAfterStale = api.Audit.Read(0, 1, null, null, null).TotalRecordCount;
        var (missing, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk", body: new { UserIds = new[] { kid.Id, viewer.Id }, AddRoleIds = new[] { role }, IfUpdatedAt = new[] { new { UserId = kid.Id, UpdatedUtc = (DateTime?)null } } });
        var (stranger, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk", body: new { UserIds = new[] { kid.Id }, AddRoleIds = new[] { role }, IfUpdatedAt = new[] { new { UserId = kid.Id, UpdatedUtc = (DateTime?)null }, new { UserId = viewer.Id, UpdatedUtc = (DateTime?)null } } });
        var (twice, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk", body: new { UserIds = new[] { kid.Id }, AddRoleIds = new[] { role }, IfUpdatedAt = new[] { new { UserId = kid.Id, UpdatedUtc = (DateTime?)null }, new { UserId = kid.Id, UpdatedUtc = (DateTime?)null } } });
        var (current, saved) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk", body: Bulk(Token(api, viewer)));

        Assert.Equal(200, dry);
        Assert.Equal([kid.Id.ToString("N")], preview!["Changed"]!.AsArray().Select(c => (string?)c));
        Assert.Equal(409, stale);
        Assert.Equal(AccessRolesException.Changed, (string?)problem!["Code"]);
        Assert.Equal(["viewer"], problem["Users"]!.AsArray().Select(u => (string?)u!["Name"]));
        Assert.Contains("viewer was changed since you read", (string?)problem["detail"], StringComparison.Ordinal);
        Assert.False(kidAfterStale, "nothing was written, not even for the user who didn't change");
        Assert.Equal(audit, auditAfterStale);
        Assert.Equal((400, 400, 400), (missing, stranger, twice));

        // The negative control: the tokens read now save, and kid gets the role.
        Assert.Equal(200, current);
        Assert.Equal([kid.Id.ToString("N")], saved!["Changed"]!.AsArray().Select(c => (string?)c));
        Assert.Equal([role], api.Store.Current.Assignments.Single(a => a.UserId.Equals(kid.Id)).RoleIds);
    }

    [Fact]
    public async Task Unmanage_WithIfUpdatedAt_IsRefusedWhenTheAssignmentChangedSince_AndADryRunPreviewsTheChoice()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var viewer = User(api, "viewer");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(viewer), body: new { RoleIds = new[] { role } });
        var read = Token(api, viewer);

        var (dry, preview) = await api.JsonAsync(HttpMethod.Post, Path(viewer) + "/Unmanage?dryRun=true", body: new { Choice = "Keep", IfUpdatedAt = read });
        await api.JsonAsync(HttpMethod.Put, Path(viewer), body: new { RoleIds = new[] { role }, LockOverrides = new { Home = "Unlock" }, IfUpdatedAt = read });
        var (stale, problem) = await api.JsonAsync(HttpMethod.Post, Path(viewer) + "/Unmanage", body: new { Choice = "Keep", IfUpdatedAt = read });
        var stillManaged = api.Store.Current.Assignments.Count;
        var (ok, _) = await api.JsonAsync(HttpMethod.Post, Path(viewer) + "/Unmanage", body: new { Choice = "Keep", IfUpdatedAt = Token(api, viewer) });
        var (none, _) = await api.JsonAsync(HttpMethod.Post, Path(viewer) + "/Unmanage", body: new { Choice = "Keep", IfUpdatedAt = read });

        Assert.Equal(200, dry);
        Assert.Equal("Keep", (string?)Assert.Single(preview!["Preview"]!["Users"]!.AsArray())!["LeavesManagement"]!["Choice"]);
        Assert.Equal(409, stale);
        Assert.Equal(AccessRolesException.Changed, (string?)problem!["Code"]);
        Assert.Equal(1, stillManaged);
        Assert.Equal(200, ok);
        Assert.Empty(api.Store.Current.Assignments);
        Assert.Equal(409, none);
    }

    [Fact]
    public async Task ARefusedDryRun_AnswersItsRefusalWithThePreview_ButARealRefusalAndAStaleTokenDont()
    {
        // The page's preview is the save's own dry run, so a dry run the save would have refused still says
        // what the change does (who leaves management, who would have no libraries), next to its Code.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var viewer = User(api, "viewer");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk", body: new { UserIds = new[] { kid.Id, viewer.Id }, AddRoleIds = new[] { role } });
        var remove = new { UserIds = new[] { kid.Id, viewer.Id }, RemoveRoleIds = new[] { role } };

        var (dry, dryProblem) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk?dryRun=true", body: remove);
        var (real, realProblem) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk", body: remove);
        var (staleDry, staleProblem) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk?dryRun=true", body: new { remove.UserIds, remove.RemoveRoleIds, IfUpdatedAt = new[] { new { UserId = kid.Id, UpdatedUtc = (DateTime?)null }, new { UserId = viewer.Id, UpdatedUtc = (DateTime?)null } } });

        Assert.Equal(400, dry);
        Assert.Equal(AccessRolesException.UnmanageChoiceRequired, (string?)dryProblem!["Code"]);
        Assert.Equal(["kid", "viewer"], dryProblem["Preview"]!["UnmanageChoiceRequired"]!.AsArray().Select(u => (string?)u!["Name"]).Order(StringComparer.Ordinal));
        Assert.Equal(2, dryProblem["Preview"]!["Users"]!.AsArray().Count);
        Assert.Equal(400, real);
        Assert.Null(realProblem!["Preview"]);
        Assert.Equal(409, staleDry);
        Assert.Equal(AccessRolesException.Changed, (string?)staleProblem!["Code"]);
        Assert.Null(staleProblem["Preview"]);
        Assert.Equal(2, api.Store.Current.Assignments.Count);
    }

    [Fact]
    public async Task GetUsers_ListsEveryUser_WithTheirState_AndFiltersByRoleAndState()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role } });

        var (_, all) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Users");
        var (_, members) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Users?roleId=" + role.ToString("N"));
        var (_, unmanaged) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Users?state=NotManaged");
        var (bad, _) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Users?state=Sometimes");

        Assert.Equal(["admin", "kid", "viewer"], all!.AsArray().Select(u => (string?)u!["Name"]));
        var row = Assert.Single(members!.AsArray())!;
        Assert.Equal("Managed", (string?)row["State"]);
        Assert.Equal(8, (int)row["LockedCount"]!);
        Assert.True((bool)row["PendingWrite"]!);
        Assert.False((bool)row["Drift"]!);
        Assert.Equal("Role Kid; 8 locked; libraries: Kids", (string?)row["Summary"]);
        Assert.Equal(["admin", "viewer"], unmanaged!.AsArray().Select(u => (string?)u!["Name"]));
        Assert.Equal(400, bad);
    }

    [Fact]
    public async Task Effective_ResolvesAnyUser_AndSimulatesOtherRoles_WithoutSavingThem()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var viewer = User(api, "viewer");
        var role = await RolesApiTests.CreateKidAsync(api);

        var (_, plain) = await api.JsonAsync(HttpMethod.Get, Path(viewer) + "/Effective");
        var (_, simulated) = await api.JsonAsync(HttpMethod.Get, Path(viewer) + "/Effective?simulateRoleIds=" + role.ToString("N"));
        var (unknownRole, _) = await api.JsonAsync(HttpMethod.Get, Path(viewer) + "/Effective?simulateRoleIds=" + Guid.NewGuid().ToString("N"));
        var (notAGuid, _) = await api.JsonAsync(HttpMethod.Get, Path(viewer) + "/Effective?simulateRoleIds=kid");
        var (unknownUser, _) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Users/" + Guid.NewGuid().ToString("N") + "/Effective");

        Assert.Equal("NotManaged", (string?)plain!["State"]);
        Assert.Equal(["Native"], plain["Libraries"]!["Visible"]![0]!["Sources"]!.AsArray().Select(s => (string?)s));
        Assert.Equal("Managed", (string?)simulated!["State"]);
        Assert.Equal("Role:Kid", (string?)simulated["Locks"]!["Home"]!["Source"]);
        Assert.Empty(api.Store.Current.Assignments);
        Assert.Equal(400, unknownRole);
        Assert.Equal(400, notAGuid);
        Assert.Equal(404, unknownUser);
    }

    [Fact]
    public async Task Effective_CarriesTheStoredAssignment_ForThePanel_WhereTheSourcesDontShowIt()
    {
        // The per-user panel edits the stored assignment, but an administrator's locks say only "Administrator" and
        // an excluded user's nothing, so the Effective view carries the assignment as stored (Inherit overrides left out).
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var admin = User(api, "admin");
        var viewer = User(api, "viewer");
        var role = await RolesApiTests.CreateKidAsync(api);
        var (adminStatus, _) = await api.JsonAsync(HttpMethod.Put, Path(admin), body: new { RoleIds = new[] { role }, LockOverrides = new { Home = "Unlock", Playback = "Inherit" }, Libraries = new { Mode = "All" } });
        var (kidStatus, _) = await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role }, State = "Excluded", Libraries = new { Add = new[] { Movies.Id }, Remove = new[] { Horror.Id } } });

        var (_, adminView) = await api.JsonAsync(HttpMethod.Get, Path(admin) + "/Effective");
        var (_, kidView) = await api.JsonAsync(HttpMethod.Get, Path(kid) + "/Effective");
        var (_, viewerView) = await api.JsonAsync(HttpMethod.Get, Path(viewer) + "/Effective");

        Assert.Equal((200, 200), (adminStatus, kidStatus));
        Assert.Equal("Administrator", (string?)adminView!["Locks"]!["Home"]!["Source"]);
        var stored = adminView["Assignment"]!;
        Assert.Equal([role.ToString("N")], stored["RoleIds"]!.AsArray().Select(r => (string?)r));
        Assert.Equal("""{"Home":"Unlock"}""", stored["LockOverrides"]!.ToJsonString());
        Assert.Equal("All", (string?)stored["Libraries"]!["Mode"]);
        Assert.Equal("Managed", (string?)stored["State"]);

        Assert.Equal("Excluded", (string?)kidView!["State"]);
        var excluded = kidView["Assignment"]!;
        Assert.Equal("Excluded", (string?)excluded["State"]);
        Assert.Equal("Inherit", (string?)excluded["Libraries"]!["Mode"]);
        Assert.Equal([Movies.Id.ToString("N")], excluded["Libraries"]!["Add"]!.AsArray().Select(a => (string?)a));
        Assert.Equal([Horror.Id.ToString("N")], excluded["Libraries"]!["Remove"]!.AsArray().Select(a => (string?)a));
        Assert.False((bool)excluded["Libraries"]!["ConfirmNoLibraries"]!);
        Assert.Null(viewerView!["Assignment"]);
    }

    [Fact]
    public async Task Preview_ShowsWhatAProposalDoesPerUser_AndItsRefusals_AndSavesNothing()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var admin = User(api, "admin");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role } });
        var before = api.Store.Current;
        var lines = api.Audit.Read(0, 1, null, null, null).TotalRecordCount;

        var (_, widened) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Preview", body: new
        {
            Roles = new[] { new { Id = role, Name = "Kid", Locks = new[] { "Profile" }, Libraries = new { Mode = "Only", Refs = new[] { new { Id = Kids.Id }, new { Id = Movies.Id } } } } }
        });
        var (_, deleted) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Preview", body: new { DeleteRoleIds = new[] { role } });
        var (_, kept) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Preview", body: new { DeleteRoleIds = new[] { role }, OnUnmanage = "Keep" });
        var (_, lockout) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Preview", body: new { Assignments = new[] { new { UserId = admin.Id, RoleIds = new[] { role } } } });
        var (_, empty) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Preview", body: new { Assignments = new[] { new { UserId = kid.Id, Libraries = new { Mode = "Only" } } } });
        var (unknownUser, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Preview", body: new { Assignments = new[] { new { UserId = Guid.NewGuid() } } });
        var (unknownRole, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Preview", body: new { DeleteRoleIds = new[] { Guid.NewGuid() } });

        var row = Assert.Single(widened!["Users"]!.AsArray())!;
        Assert.Equal(["Movies"], row["LibrariesAdded"]!.AsArray().Select(l => (string?)l!["Name"]));
        Assert.Equal(7, row["LocksRemoved"]!.AsArray().Count);
        Assert.Equal("kid", (string?)deleted!["UnmanageChoiceRequired"]![0]!["Name"]);
        Assert.Null(deleted["Users"]![0]!["LeavesManagement"]!["Choice"]);
        Assert.Equal(["Collections", "Horror", "Kids", "Movies", "Shows"], kept!["Users"]![0]!["LeavesManagement"]!["FrozenAt"]!.AsArray().Select(f => (string?)f));
        Assert.Empty(kept["UnmanageChoiceRequired"]!.AsArray());
        Assert.True((bool)lockout!["NoFullAdministrator"]!);
        Assert.Equal("kid", (string?)empty!["NoLibraries"]![0]!["Name"]);
        Assert.True((bool)empty["Users"]![0]!["NoLibrariesRefused"]!);
        Assert.Equal(404, unknownUser);
        Assert.Equal(404, unknownRole);
        Assert.Same(before, api.Store.Current);
        Assert.Equal(lines, api.Audit.Read(0, 1, null, null, null).TotalRecordCount);
    }

    [Fact]
    public async Task Libraries_ListEveryLibraryWithItsRoles_AndAGoneLibraryARoleStillRefersTo()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var role = await RolesApiTests.CreateKidAsync(api);
        api.Libraries.Remove(Kids);

        var (_, libraries) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Libraries");

        var rows = libraries!.AsArray().Select(l => l!).ToList();
        Assert.Equal(["Collections", "Horror", "Movies", "Shows", "Kids"], rows.Select(r => (string?)r["Name"]));
        Assert.All(rows.Take(4), r => Assert.True((bool)r["InNoRole"]!));
        var gone = rows[^1];
        Assert.Equal("Orphaned", (string?)gone["State"]);
        Assert.Equal([role.ToString("N")], gone["RoleIds"]!.AsArray().Select(r => (string?)r));
        Assert.Equal("boxsets", (string?)rows[0]["CollectionType"]);
    }

    [Fact]
    public async Task TheAuditLog_IsReadNewestFirstWithPaging_AndExportedAsJsonLinesNotCached()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role } });

        var (_, page) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Audit?limit=1");
        var (_, forKid) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Audit?userId=" + kid.Id.ToString("N"));
        var (badLimit, _) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Audit?limit=201");
        var (badStart, _) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Audit?startIndex=-1");
        using var export = await api.SendAsync(HttpMethod.Get, "/AccessRoles/Admin/Audit/Export");
        var text = await export.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var refused = await api.SendAsync(HttpMethod.Get, "/AccessRoles/Admin/Audit/Export", caller: "user");

        Assert.Equal(2, (int)page!["TotalRecordCount"]!);
        Assert.Equal("AssignmentChanged", (string?)page["Items"]![0]!["Action"]);
        Assert.Equal(2, (long)page["Items"]![0]!["Seq"]!);
        Assert.Equal("kid", (string?)Assert.Single(forKid!["Items"]!.AsArray())!["TargetName"]);
        Assert.Equal(400, badLimit);
        Assert.Equal(400, badStart);
        Assert.Equal("application/x-ndjson", export.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", export.Headers.CacheControl?.ToString());
        Assert.StartsWith("accessroles-audit-", export.Content.Headers.ContentDisposition?.FileName?.Trim('"'), StringComparison.Ordinal);
        Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(403, (int)refused.StatusCode);
    }

    [Fact]
    public async Task TheAuditLog_PagesByTheLastLineShown_WhichLinesWrittenMeanwhileDontShift()
    {
        // Regression: the next page by startIndex, after lines were written, showed lines again.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        await Append(api, 7);
        var (_, first) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Audit?limit=3");
        var shown = Seqs(first);
        await Append(api, 2);

        var last = shown[^1].ToString(CultureInfo.InvariantCulture);
        var (_, next) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Audit?limit=3&before=" + last);
        var (_, byIndex) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Audit?limit=3&startIndex=3");
        var (_, filtered) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Audit?action=RoleCreated&userId=" + Guid.NewGuid().ToString("N") + "&before=" + last);
        var (_, older) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Audit?action=RoleCreated&before=" + last);
        var (negative, _) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Audit?before=-1");

        Assert.Equal([shown[^1] - 1, shown[^1] - 2, shown[^1] - 3], Seqs(next));
        Assert.Equal((int)first!["TotalRecordCount"]! - 3, (int)next!["TotalRecordCount"]!);
        Assert.NotEmpty(Seqs(byIndex).Intersect(shown));

        // The cursor is one more filter: with the others, and counted as they are.
        Assert.Equal(0, (int)filtered!["TotalRecordCount"]!);
        Assert.Equal(4, (int)older!["TotalRecordCount"]!);
        Assert.All(Seqs(older), s => Assert.True(s < shown[^1]));
        Assert.Equal(400, negative);
    }

    [Fact]
    public async Task TheAuditLogsRange_WithoutAZone_IsUtc_AsTheNamesSay()
    {
        // MVC binds a time without a zone with no kind, which was read as the server's local
        // time. (On a machine whose zone isn't UTC, the old reading moved the one-tick range by hours and found nothing.)
        await using var api = await RolesApiTests.StartWithUsersAsync();
        await Append(api, 1);
        var latest = Assert.Single(api.Audit.Read(0, 1, null, null, null).Items);
        var at = latest.Utc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        var after = latest.Utc.AddTicks(1).ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);

        var (_, zoneless) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Audit?fromUtc=" + at + "&toUtc=" + after);
        var (_, withZ) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Audit?fromUtc=" + at + "Z&toUtc=" + after + "Z");
        using var export = await api.SendAsync(HttpMethod.Get, "/AccessRoles/Admin/Audit/Export?fromUtc=" + at + "&toUtc=" + after);
        var text = await export.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var (reversed, _) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Audit?fromUtc=" + after + "&toUtc=" + at + "Z");

        Assert.Equal(DateTimeKind.Utc, latest.Utc.Kind);
        Assert.Equal([latest.Seq], Seqs(zoneless));
        Assert.Equal([latest.Seq], Seqs(withZ));
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(400, reversed);
    }

    [Fact]
    public async Task ApplyPolicy_RemovesAnUnknownUsersAssignmentAndLedgerEntry_AndRemapDryRunAnswers()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var gone = Guid.NewGuid();
        await api.Store.UpdateAsync(
            d => d with
            {
                Assignments = [new RoleAssignment { UserId = gone, State = AccessRolesAssignmentState.Excluded }],
                Ledger = new Dictionary<Guid, LedgerEntry> { [gone] = Ledger(true) },
                KnownUsers = d.KnownUsers.With(gone, "former")
            },
            TestContext.Current.CancellationToken);

        var admin = User(api, "admin");
        var (remap, report) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Remap");
        var (current, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/ApplyPolicy", body: new { UserIds = new[] { User(api, "kid").Id } });
        var (status, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/ApplyPolicy", body: new { UserIds = new[] { gone } }, userId: admin.Id);

        Assert.Equal(200, remap);
        Assert.NotNull(report);
        Assert.Equal(400, current);
        Assert.Equal(200, status);
        Assert.Empty(api.Store.Current.Assignments);
        Assert.Empty(api.Store.Current.Ledger);
        var line = api.Audit.Read(0, 10, null, "UserDeleted", null).Items.Single();
        Assert.Equal(gone, line.TargetUserId);

        // The admin asked for it, so the line is theirs, not the plugin's.
        Assert.Equal(("Admin", (Guid?)admin.Id, (string?)"admin"), (line.Actor.Kind, line.Actor.UserId, line.Actor.Name));
        Assert.Null(AuditActorScope.Current);
    }

    [Fact]
    public async Task RemapByName_IsAuditedAsTheCallers_AndApplyPolicyByAnApiKeyAsTheKeys()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var viewer = User(api, "viewer");
        var (gone, former) = (Guid.NewGuid(), Guid.NewGuid());
        await api.Store.UpdateAsync(
            d => d with
            {
                Assignments = [new RoleAssignment { UserId = gone, State = AccessRolesAssignmentState.Excluded }, new RoleAssignment { UserId = former, State = AccessRolesAssignmentState.Excluded }],
                KnownUsers = d.KnownUsers.With(gone, "viewer").With(former, "nobody")
            },
            TestContext.Current.CancellationToken);

        // A remap that writes needs its dry run's PlanToken: without it, 400 and nothing moved.
        var (unbound, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Remap?dryRun=false", userId: User(api, "admin").Id);
        var (dry, plan) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Remap", userId: User(api, "admin").Id);
        Assert.Equal((400, 200), (unbound, dry));
        Assert.Equal(2, api.Store.Current.Assignments.Count);
        var (stale, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Remap?dryRun=false&ifPlan=0000", userId: User(api, "admin").Id);
        var (remap, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Remap?dryRun=false&ifPlan=" + Uri.EscapeDataString((string)plan!["PlanToken"]!), userId: User(api, "admin").Id);
        var (applied, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/ApplyPolicy", caller: "apikey", body: new { UserIds = new[] { former } });

        Assert.Equal((409, 200, 200), (stale, remap, applied));
        Assert.Equal(viewer.Id, Assert.Single(api.Store.Current.Assignments).UserId);
        var remapped = api.Audit.Read(0, 10, null, "UsersRemapped", null).Items.Single();
        Assert.Equal(("Admin", (string?)"admin"), (remapped.Actor.Kind, remapped.Actor.Name));
        Assert.Equal("ApiKey", api.Audit.Read(0, 10, null, "UserDeleted", null).Items.Single().Actor.Kind);
    }

    [Fact]
    public async Task ADryRun_AnswersTheStoredToken_SoTheSaveThatFollowsIsntRefused()
    {
        // A dry run's Effective.UpdatedUtc is the stored assignment's (none for a new one), never a time
        // that wasn't saved, so the admin page's preview-then-save sends a token that matches.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var role = await RolesApiTests.CreateKidAsync(api);

        var (firstDry, newOne) = await api.JsonAsync(HttpMethod.Put, Path(kid) + "?dryRun=true", body: new { RoleIds = new[] { role } });
        var (first, saved) = await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role }, IfUpdatedAt = (string?)newOne!["Effective"]!["UpdatedUtc"] });
        var stored = (string?)saved!["Effective"]!["UpdatedUtc"];
        var (secondDry, preview) = await api.JsonAsync(HttpMethod.Put, Path(kid) + "?dryRun=true", body: new { RoleIds = new[] { role }, LockOverrides = new { Home = "Unlock" }, IfUpdatedAt = stored });
        var (second, _) = await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role }, LockOverrides = new { Home = "Unlock" }, IfUpdatedAt = (string?)preview!["Effective"]!["UpdatedUtc"] });

        Assert.Equal((200, 200, 200, 200), (firstDry, first, secondDry, second));
        Assert.Null(newOne["Effective"]!["UpdatedUtc"]);
        Assert.Equal(stored, (string?)preview["Effective"]!["UpdatedUtc"]);
        Assert.Equal("UserOverride", (string?)preview["Effective"]!["Locks"]!["Home"]!["Source"]);
        Assert.True(api.Store.Current.Assignments.Single().LockOverrides.ContainsKey(AccessRolesLock.Home));
    }

    [Fact]
    public async Task APreviewThatDeletesADefaultRole_ListsTheRefusal_UnlessItsDefaultRolesLeaveItOut()
    {
        // DELETE Roles/{id} of a default role is 409 DefaultRole, so a preview of that deletion says so.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/DefaultRoles", body: new { RoleIds = new[] { role } });

        var (refused, deleted) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Preview", body: new { DeleteRoleIds = new[] { role } });
        var (_, alsoDefault) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Preview", body: new { DeleteRoleIds = new[] { role }, DefaultRoleIds = new[] { role } });
        var (_, defaultsCleared) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Preview", body: new { DeleteRoleIds = new[] { role }, DefaultRoleIds = Array.Empty<Guid>() });
        var (realDelete, problem) = await api.JsonAsync(HttpMethod.Delete, "/AccessRoles/Admin/Roles/" + role.ToString("N"));

        Assert.Equal(200, refused);
        var refusal = Assert.Single(deleted!["DefaultRole"]!.AsArray())!;
        Assert.Equal(("Kid", role.ToString("N")), ((string?)refusal["Name"], (string?)refusal["Id"]));
        Assert.Single(alsoDefault!["DefaultRole"]!.AsArray());
        Assert.Empty(defaultsCleared!["DefaultRole"]!.AsArray());
        Assert.Equal(409, realDelete);
        Assert.Equal(AccessRolesException.DefaultRole, (string?)problem!["Code"]);
        Assert.Equal([role], api.Store.Current.DefaultRoleIds);
    }

    [Fact]
    public async Task WritingToAnAssignmentWithValuesANewerBuildWrote_KeepsThemUnlessTheRequestSetsThem()
    {
        // The keep-unknown rule: a PUT that leaves State and Libraries.Mode out keeps
        // the unknown values, and the user stays UnknownValue (every settings section locked, nothing written).
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.Store.UpdateAsync(
            d => d with
            {
                Assignments =
                [
                    new RoleAssignment
                    {
                        UserId = kid.Id,
                        State = LenientEnum<AccessRolesAssignmentState>.Parse("Suspended"),
                        Libraries = new AssignmentLibraries { Mode = LenientEnum<AccessRolesUserLibraryMode>.Parse("Smart") },
                        UpdatedUtc = Now
                    }
                ]
            },
            TestContext.Current.CancellationToken);

        var (kept, change) = await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role }, IfUpdatedAt = Now });
        var stored = api.Store.Current.Assignments.Single();
        var (replaced, _) = await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { RoleIds = new[] { role }, State = "Managed", Libraries = new { Mode = "Inherit" }, IfUpdatedAt = stored.UpdatedUtc });

        Assert.Equal(200, kept);
        Assert.Equal(("Suspended", "Smart"), (stored.State.Name, stored.Libraries.Mode.Name));
        Assert.Equal([role], stored.RoleIds);
        Assert.Equal("UnknownValue", (string?)change!["Effective"]!["State"]);
        Assert.Equal(200, replaced);
        var after = api.Store.Current.Assignments.Single();
        Assert.True(after.State.Is(AccessRolesAssignmentState.Managed));
        Assert.True(after.Libraries.Mode.Is(AccessRolesUserLibraryMode.Inherit));
        api.VerifyNoUserWritten();
    }

    [Fact]
    public async Task AReadOnlyStore_Answers503ToWrites_KeepsItsFileByteForByte_AndAuditsNothing_ButDryRunsAnswer()
    {
        // A data file of a newer schema (Version 2) makes the store read-only: every write is 503 before it
        // changes anything, the file isn't touched and no audit line is written; a dry run still answers.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var file = System.IO.Path.Combine(api.DataPath, "accessroles", AccessRolesPaths.StoreFileName);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        var bytes = System.Text.Encoding.UTF8.GetBytes("""{"Version":2,"Roles":[],"Assignments":[],"Ledger":{},"Shelves":[{"Id":1}]}""");
        await System.IO.File.WriteAllBytesAsync(file, bytes, TestContext.Current.CancellationToken);

        var (createRole, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: RolesApiTests.KidRequest());
        var (putUser, _) = await api.JsonAsync(HttpMethod.Put, Path(kid), body: new { LockOverrides = new { Home = "Lock" } });
        var (dryRole, dryRoleBody) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles?dryRun=true", body: RolesApiTests.KidRequest());
        var (dryUser, dryUserBody) = await api.JsonAsync(HttpMethod.Put, Path(kid) + "?dryRun=true", body: new { LockOverrides = new { Home = "Lock" } });

        Assert.Equal((503, 503), (createRole, putUser));
        Assert.Equal((200, 200), (dryRole, dryUser));
        Assert.True((bool)dryRoleBody!["DryRun"]!);
        Assert.Equal("UserOverride", (string?)dryUserBody!["Effective"]!["Locks"]!["Home"]!["Source"]);
        Assert.True(api.Store.IsReadOnly);
        Assert.Equal(bytes, await System.IO.File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        Assert.Equal(0, api.Audit.Read(0, 10, null, null, null).TotalRecordCount);
        var audit = System.IO.Path.Combine(api.DataPath, "accessroles", "audit");
        Assert.True(!System.IO.Directory.Exists(audit) || !System.IO.Directory.EnumerateFiles(audit).Any(), "An audit file was written.");
        api.VerifyNoUserWritten();
    }

    [Fact]
    public async Task Bulk_WithIfPreview_IsRefusedWhenTheRoleItAddsWasEditedSinceTheDryRun_AndWritesNothing()
    {
        // Regression: the dry run of adding a lock-only role said nobody's libraries change; another admin
        // then gave the role every library and two more locks, and the save with the preview's IfUpdatedAt still saved (no
        // token covered the role). ifPreview is the dry run's PreviewToken: the effect itself is compared.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var viewer = User(api, "viewer");
        var (_, created) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: new { Name = "Guest", Locks = new[] { "Profile" }, Libraries = new { Mode = "NotManaged" } });
        var role = Guid.Parse((string)created!["Role"]!["Id"]!);
        object Bulk() => new
        {
            UserIds = new[] { kid.Id, viewer.Id },
            AddRoleIds = new[] { role },
            IfUpdatedAt = new object[] { new { UserId = kid.Id, UpdatedUtc = (DateTime?)null }, new { UserId = viewer.Id, UpdatedUtc = (DateTime?)null } }
        };

        var (dry, preview) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk?dryRun=true", body: Bulk());
        var token = (string?)preview!["Preview"]!["PreviewToken"];
        var (again, previewAgain) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk?dryRun=true", body: Bulk());
        await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Roles/" + role.ToString("N"), body: new { Name = "Guest", Locks = new[] { "Profile", "Playback", "Home" }, Libraries = new { Mode = "All" }, IfUpdatedAt = (string?)created["Role"]!["UpdatedUtc"] });
        var audit = api.Audit.Read(0, 1, null, null, null).TotalRecordCount;
        var (stale, problem) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk?ifPreview=" + token, body: Bulk());
        var assignmentsAfterStale = api.Store.Current.Assignments.Count;
        var auditAfterStale = api.Audit.Read(0, 1, null, null, null).TotalRecordCount;
        var (_, now) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk?dryRun=true", body: Bulk());
        var fresh = (string?)now!["Preview"]!["PreviewToken"];
        var (saved, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk?ifPreview=" + fresh!.ToUpperInvariant() + "%20", body: Bulk());

        Assert.Equal((200, 200), (dry, again));
        Assert.False(string.IsNullOrEmpty(token));
        Assert.Equal(token, (string?)previewAgain!["Preview"]!["PreviewToken"]);
        Assert.All(preview["Preview"]!["Users"]!.AsArray(), u => Assert.Empty(u!["LibrariesAdded"]!.AsArray()));
        Assert.Equal(409, stale);
        Assert.Equal(AccessRolesException.Changed, (string?)problem!["Code"]);
        Assert.Equal(["kid", "viewer"], problem["Users"]!.AsArray().Select(u => (string?)u!["Name"]).Order(StringComparer.Ordinal));
        Assert.Contains("isn't what its preview showed any more", (string?)problem["detail"], StringComparison.Ordinal);
        Assert.Null(problem["Preview"]);
        Assert.Equal(0, assignmentsAfterStale);
        Assert.Equal(audit, auditAfterStale);
        Assert.NotEqual(token, fresh);
        Assert.Contains(now["Preview"]!["Users"]!.AsArray(), u => (string?)u!["Name"] == "viewer" && u["LibrariesAdded"]!.AsArray().Count > 0);

        // The negative control: the token of the dry run seen now saves (its letter case and a space aside).
        Assert.Equal(200, saved);
        Assert.Equal(2, api.Store.Current.Assignments.Count);
        api.VerifyNoUserWritten();
    }

    [Fact]
    public async Task PutUser_WithIfPreview_IsRefusedWhenARoleItNamesWasEditedSince_AndWithoutIt_AnOlderClientIsStillSaved()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var role = await RolesApiTests.CreateKidAsync(api);
        var stored = api.Store.Current.Roles.Single().UpdatedUtc;
        object Give() => new { RoleIds = new[] { role }, IfUpdatedAt = (DateTime?)null };

        var (_, dry) = await api.JsonAsync(HttpMethod.Put, Path(kid) + "?dryRun=true", body: Give());
        var token = (string?)dry!["Preview"]!["PreviewToken"];
        await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Roles/" + role.ToString("N"), body: new { Name = "Kid", Locks = new[] { "Profile" }, Libraries = new { Mode = "All" }, IfUpdatedAt = stored });
        var (stale, problem) = await api.JsonAsync(HttpMethod.Put, Path(kid) + "?ifPreview=" + token, body: Give());
        var kidAfterStale = api.Store.Current.Assignments.Any(a => a.UserId.Equals(kid.Id));
        var (older, _) = await api.JsonAsync(HttpMethod.Put, Path(kid), body: Give());

        Assert.Equal(409, stale);
        Assert.Equal(AccessRolesException.Changed, (string?)problem!["Code"]);
        Assert.Equal(["kid"], problem["Users"]!.AsArray().Select(u => (string?)u!["Name"]));
        Assert.False(kidAfterStale, "nothing was written");

        // A save without ifPreview (a client older than 0.9.10) isn't checked: it saves what the role gives now.
        Assert.Equal(200, older);
        Assert.Equal([role], api.Store.Current.Assignments.Single(a => a.UserId.Equals(kid.Id)).RoleIds);
    }

    [Fact]
    public async Task PutUser_WithIfPreview_IsRefusedWhenOnlyTheLocksOfARoleItNamesChangedSince()
    {
        // The locks are part of what a change does: a role that only locks one more section is another effect.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var (_, created) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: new { Name = "Guest", Locks = new[] { "Profile" }, Libraries = new { Mode = "NotManaged" } });
        var role = Guid.Parse((string)created!["Role"]!["Id"]!);
        object Give() => new { RoleIds = new[] { role } };

        var (_, dry) = await api.JsonAsync(HttpMethod.Put, Path(kid) + "?dryRun=true", body: Give());
        await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Roles/" + role.ToString("N"), body: new { Name = "Guest", Locks = new[] { "Profile", "Subtitles" }, Libraries = new { Mode = "NotManaged" }, IfUpdatedAt = (string?)created["Role"]!["UpdatedUtc"] });
        var (stale, problem) = await api.JsonAsync(HttpMethod.Put, Path(kid) + "?ifPreview=" + (string?)dry!["Preview"]!["PreviewToken"], body: Give());
        var (_, now) = await api.JsonAsync(HttpMethod.Put, Path(kid) + "?dryRun=true", body: Give());
        var (ok, _) = await api.JsonAsync(HttpMethod.Put, Path(kid) + "?ifPreview=" + (string?)now!["Preview"]!["PreviewToken"], body: Give());

        Assert.Equal(["Profile"], dry["Preview"]!["Users"]![0]!["LocksAdded"]!.AsArray().Select(l => (string?)l));
        Assert.Equal(409, stale);
        Assert.Equal(["kid"], problem!["Users"]!.AsArray().Select(u => (string?)u!["Name"]));
        Assert.Equal(200, ok);
        api.VerifyNoUserWritten();
    }

    [Fact]
    public async Task Unmanage_WithIfPreview_IsRefusedWhenKeepWouldFreezeOtherAccessThanItsDryRunShowed()
    {
        // The user's IfUpdatedAt doesn't move when someone changes their access on the stock Library access page; Keep
        // freezes the access they have at the save, so the preview's "frozen at" is covered by ifPreview only.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var viewer = User(api, "viewer");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, Path(viewer), body: new { RoleIds = new[] { role } });
        object Keep() => new { Choice = "Keep", IfUpdatedAt = Token(api, viewer) };

        var (_, dry) = await api.JsonAsync(HttpMethod.Post, Path(viewer) + "/Unmanage?dryRun=true", body: Keep());
        var token = (string?)dry!["Preview"]!["PreviewToken"];
        viewer.SetPreference(PreferenceKind.EnabledFolders, new[] { Movies.Id });
        var (stale, problem) = await api.JsonAsync(HttpMethod.Post, Path(viewer) + "/Unmanage?ifPreview=" + token, body: Keep());
        var stillManaged = api.Store.Current.Assignments.Count;
        var (_, now) = await api.JsonAsync(HttpMethod.Post, Path(viewer) + "/Unmanage?dryRun=true", body: Keep());
        var (ok, _) = await api.JsonAsync(HttpMethod.Post, Path(viewer) + "/Unmanage?ifPreview=" + (string?)now!["Preview"]!["PreviewToken"], body: Keep());

        Assert.Equal(["Movies", "Shows"], dry["Preview"]!["Users"]![0]!["LeavesManagement"]!["FrozenAt"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(409, stale);
        Assert.Equal(["viewer"], problem!["Users"]!.AsArray().Select(u => (string?)u!["Name"]));
        Assert.Equal(1, stillManaged);
        Assert.Equal(["Movies"], now["Preview"]!["Users"]![0]!["LeavesManagement"]!["FrozenAt"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(200, ok);
        Assert.Empty(api.Store.Current.Assignments);
    }

    [Fact]
    public async Task ALockoutSave_TakesTheRefusedDryRunsPreviewToken_WithItsConfirmation()
    {
        // The page shows a dry run refused for NoFullAdministrator and saves it with "Save anyway": the refusal's Preview
        // carries the token, and confirmNoFullAdmin doesn't change what the change does.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var admin = User(api, "admin");
        var role = await RolesApiTests.CreateKidAsync(api);

        var (refused, problem) = await api.JsonAsync(HttpMethod.Put, Path(admin) + "?dryRun=true", body: new { RoleIds = new[] { role } });
        var token = (string?)problem!["Preview"]!["PreviewToken"];
        var (ok, _) = await api.JsonAsync(HttpMethod.Put, Path(admin) + "?confirmNoFullAdmin=true&ifPreview=" + token, body: new { RoleIds = new[] { role } });

        Assert.Equal(409, refused);
        Assert.Equal(AccessRolesException.NoFullAdministrator, (string?)problem["Code"]);
        Assert.True((bool)problem["Preview"]!["NoFullAdministrator"]!);
        Assert.Equal(200, ok);
    }

    // Audit lines of the plugin's own, written as one append (they share its time).
    private static Task Append(AccessRolesApi api, int count)
        => api.Audit.AppendAsync([.. Enumerable.Range(0, count).Select(i => new AuditRecord(AuditActor.System, "RoleCreated", null, Guid.NewGuid(), "role " + i.ToString(CultureInfo.InvariantCulture), null))], TestContext.Current.CancellationToken);

    private static long[] Seqs(JsonNode? page) => [.. page!["Items"]!.AsArray().Select(e => (long)e!["Seq"]!)];

    private static string Path(User user) => "/AccessRoles/Admin/Users/" + user.Id.ToString("N");

    private static DateTime Token(AccessRolesApi api, User user) => api.Store.Current.Assignments.Single(a => a.UserId.Equals(user.Id)).UpdatedUtc;

    private static User User(AccessRolesApi api, string name) => api.UserManager.Object.Users.Single(u => u.Username == name);
}
