using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;
using Jellyfin.Plugin.Kit.TestSupport;

namespace Jellyfin.Plugin.AccessRoles.Tests.Api;

/// <summary>
/// The roles endpoints over HTTP: administrators and API keys only, roles from the
/// presets, the <i>Dashboard</i> row, validation, <c>IfUpdatedAt</c>, dry runs, deletion with leaving management, default
/// roles, and their audit lines. No user is written.
/// </summary>
public sealed class RolesApiTests
{
    public static TheoryData<string, string> AdminEndpoints => new()
    {
        { "GET", "/AccessRoles/Admin/Roles" },
        { "GET", "/AccessRoles/Admin/Roles/" + Guid.NewGuid().ToString("N") },
        { "POST", "/AccessRoles/Admin/Roles" },
        { "PUT", "/AccessRoles/Admin/Roles/" + Guid.NewGuid().ToString("N") },
        { "DELETE", "/AccessRoles/Admin/Roles/" + Guid.NewGuid().ToString("N") },
        { "PUT", "/AccessRoles/Admin/DefaultRoles" },
        { "GET", "/AccessRoles/Admin/Users" },
        { "PUT", "/AccessRoles/Admin/Users/" + Guid.NewGuid().ToString("N") },
        { "POST", "/AccessRoles/Admin/Users/Bulk" },
        { "POST", "/AccessRoles/Admin/Users/" + Guid.NewGuid().ToString("N") + "/Unmanage" },
        { "GET", "/AccessRoles/Admin/Users/" + Guid.NewGuid().ToString("N") + "/Effective" },
        { "POST", "/AccessRoles/Admin/Preview" },
        { "GET", "/AccessRoles/Admin/Libraries" },
        { "GET", "/AccessRoles/Admin/Audit" },
        { "GET", "/AccessRoles/Admin/Audit/Export" },
        { "GET", "/AccessRoles/Admin/Settings" },
        { "PUT", "/AccessRoles/Admin/Settings" },
        { "POST", "/AccessRoles/Admin/Users/Remap" },
        { "POST", "/AccessRoles/Admin/Users/ApplyPolicy" },
        { "POST", "/AccessRoles/Admin/ImportExisting" },
        { "GET", "/AccessRoles/Admin/Export" },
        { "POST", "/AccessRoles/Admin/Import" },
        { "GET", "/AccessRoles/Admin/Snapshots" },
        { "POST", "/AccessRoles/Admin/Snapshots/Restore?name=x" },
        { "POST", "/AccessRoles/Admin/Maintenance/Run" }
    };

    [Theory]
    [MemberData(nameof(AdminEndpoints))]
    public async Task EveryAdminEndpoint_Is401Anonymously_And403ForAUser(string method, string path)
    {
        // RequiresElevation, decided before the body is read.
        await using var api = await AccessRolesApi.StartAsync();

        using var anonymous = await api.SendAsync(new HttpMethod(method), path, caller: null, body: "{}");
        using var user = await api.SendAsync(new HttpMethod(method), path, caller: "user", body: "{}");

        Assert.Equal(401, (int)anonymous.StatusCode);
        Assert.Equal(403, (int)user.StatusCode);
        api.VerifyNoUserWritten();
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("apikey")]
    public async Task AdministratorsAndApiKeys_ReadEveryList(string caller)
    {
        // API keys are allowed: they are the break-glass path.
        await using var api = await StartWithUsersAsync();

        foreach (var path in new[] { "/AccessRoles/Admin/Roles", "/AccessRoles/Admin/Users", "/AccessRoles/Admin/Libraries", "/AccessRoles/Admin/Audit" })
        {
            var (status, _) = await api.JsonAsync(HttpMethod.Get, path, caller);
            Assert.Equal(200, status);
        }
    }

    [Fact]
    public async Task AKidRole_FromThePreset_NeedsALibrary_TakesThePresetsLocks_AndShowsTheDashboardRow()
    {
        await using var api = await StartWithUsersAsync();

        var (emptyStatus, empty) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: new { Name = "Kid", Preset = "Kid" });
        var (status, created) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: KidRequest(), userId: AdminId(api));

        Assert.Equal(400, emptyStatus);
        Assert.Equal(AccessRolesException.EmptyOnlyRole, (string?)empty!["Code"]);
        Assert.Equal(200, status);
        var role = created!["Role"]!;
        Assert.Equal(AccessPresets.Locks(AccessRolesPreset.Kid).Select(l => l.ToString()), role["Locks"]!.AsArray().Select(l => (string?)l));
        Assert.Equal("Only", (string?)role["Libraries"]!["Mode"]);
        Assert.Equal("Kids", (string?)role["Libraries"]!["Refs"]![0]!["Name"]);
        Assert.Equal(Kids.Id.ToString("N"), (string?)role["Libraries"]!["Refs"]![0]!["Id"]);
        Assert.True((bool)role["Dashboard"]!["BlocksAdminSurfaces"]!);

        // AdminSurfaces was stored but not enforced before 0.9.7, and the row said so; since 0.9.7 it is.
        Assert.True((bool)role["Dashboard"]!["Enforced"]!);
        Assert.Equal(AccessRolesMapper.DashboardBlocked, (string?)role["Dashboard"]!["NonAdministrators"]);
        Assert.DoesNotContain("Not enforced yet", (string?)role["Dashboard"]!["NonAdministrators"], StringComparison.Ordinal);
        Assert.Equal("Administrators are not restricted.", (string?)role["Dashboard"]!["Administrators"]);
        Assert.False((bool)created["DryRun"]!);

        var line = Assert.Single(api.Audit.Read(0, 10, null, null, null).Items);
        Assert.Equal("RoleCreated", line.Action);
        Assert.Equal(("Admin", AdminId(api), "admin"), (line.Actor.Kind, line.Actor.UserId, line.Actor.Name));
        Assert.Equal(Guid.Parse((string)role["Id"]!), line.TargetRoleId);
        Assert.Equal("Kid", line.Details!["Preset"]!.ToString());
        Assert.Equal([1L], created["AuditSeq"]!.AsArray().Select(s => (long)s!));
        api.VerifyNoUserWritten();
    }

    [Fact]
    public async Task TheGuestAndStandardPresets_AreAsDesigned()
    {
        await using var api = await StartWithUsersAsync();

        var (_, guest) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: new { Name = "Guest", Preset = "Guest", Libraries = new { Refs = new[] { new { Id = Movies.Id } } } });
        var (_, standard) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: new { Name = "Standard", Preset = "Standard" });

        Assert.Equal(11, guest!["Role"]!["Locks"]!.AsArray().Count);
        Assert.Empty(standard!["Role"]!["Locks"]!.AsArray());
        Assert.Equal("NotManaged", (string?)standard["Role"]!["Libraries"]!["Mode"]);
        Assert.False((bool)standard["Role"]!["Dashboard"]!["BlocksAdminSurfaces"]!);
        Assert.Equal(AccessRolesMapper.DashboardOpen, (string?)standard["Role"]!["Dashboard"]!["NonAdministrators"]);
    }

    [Theory]
    [InlineData("""{"Name":"x","Locks":["Downloads"]}""")]
    [InlineData("""{"Name":"x","Preset":"Teen"}""")]
    [InlineData("""{"Name":"   "}""")]
    [InlineData("""{"Name":"x","Libraries":{"Mode":"All","Refs":[{"Id":"3c1d9f8e7b6a4c5d8e9f0a1b2c3d4e5f"}]}}""")]
    [InlineData("""{"Name":"x","Libraries":{"Mode":"Only","Refs":[{"Id":"3c1d9f8e7b6a4c5d8e9f0a1b2c3d4e5f"}]}}""")]
    [InlineData("""{"Name":"x","Libraries":{"Mode":"AllExcept"}}""")]
    [InlineData("""{"Name":"x","Id":"5b0e7d2a3c4f4a1b9e8d1f2a3b4c5d6e"}""")]
    [InlineData("""{"Name":"a\u0007b"}""")]
    public async Task AnInvalidRole_Is400_AndNothingIsSaved(string body)
    {
        await using var api = await StartWithUsersAsync();

        var (status, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: body);

        Assert.Equal(400, status);
        Assert.Empty(api.Store.Current.Roles);
        Assert.Equal(0, api.Audit.Read(0, 10, null, null, null).TotalRecordCount);
    }

    [Fact]
    public async Task ANameTooLongOrTaken_IsRefused()
    {
        await using var api = await StartWithUsersAsync();
        await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: KidRequest());

        var (longStatus, _) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: new { Name = new string('n', 101) });
        var (takenStatus, taken) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: new { Name = "KID" });

        Assert.Equal(400, longStatus);
        Assert.Equal(409, takenStatus);
        Assert.Equal(AccessRolesException.DuplicateName, (string?)taken!["Code"]);
    }

    [Fact]
    public async Task AnUpdate_NeedsIfUpdatedAt_RefusesAStaleOne_TakesTheMillisecondToken_AndANoChangeWritesNothing()
    {
        await using var api = await StartWithUsersAsync();
        var (_, created) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: KidRequest());
        var id = (string)created!["Role"]!["Id"]!;
        var updated = DateTime.Parse((string)created["Role"]!["UpdatedUtc"]!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var millisecond = new DateTime(updated.Ticks - (updated.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
        object Rename(string name, DateTime? token) => new { Name = name, Locks = new[] { "Profile" }, Libraries = new { Mode = "Only", Refs = new[] { new { Id = Kids.Id } } }, IfUpdatedAt = token };

        var (missing, _) = await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Roles/" + id, body: Rename("Kids", null));
        var (stale, staleBody) = await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Roles/" + id, body: Rename("Kids", updated.AddSeconds(-1)));
        var (ok, renamed) = await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Roles/" + id, body: Rename("Little ones", millisecond));
        var token = DateTime.Parse((string)renamed!["Role"]!["UpdatedUtc"]!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var (again, unchanged) = await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Roles/" + id, body: Rename("Little ones", token));
        var (preset, _) = await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Roles/" + id, body: new { Name = "x", Preset = "Kid", IfUpdatedAt = token });

        Assert.Equal(400, missing);
        Assert.Equal(409, stale);
        Assert.Equal(AccessRolesException.Changed, (string?)staleBody!["Code"]);
        Assert.Equal(200, ok);
        Assert.Equal("Little ones", (string?)renamed["Role"]!["Name"]);
        Assert.True(token > updated);
        Assert.Equal(200, again);
        Assert.Empty(unchanged!["AuditSeq"]!.AsArray());
        Assert.Equal(400, preset);
        var update = api.Audit.Read(0, 10, null, "RoleUpdated", null).Items.Single();
        Assert.Equal("Kid", ((System.Text.Json.JsonElement)update.Details!["Before"]!).GetProperty("Name").GetString());
        Assert.Equal("Little ones", ((System.Text.Json.JsonElement)update.Details["After"]!).GetProperty("Name").GetString());
    }

    [Fact]
    public async Task ADryRun_AnswersTheSame_ButSavesNothing_AndWritesNoAuditLine()
    {
        await using var api = await StartWithUsersAsync();

        var (status, preview) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles?dryRun=true", body: KidRequest());

        Assert.Equal(200, status);
        Assert.True((bool)preview!["DryRun"]!);
        Assert.Equal("Kid", (string?)preview["Role"]!["Name"]);
        Assert.Null(preview["Role"]!["UpdatedUtc"]);
        Assert.Empty(api.Store.Current.Roles);
        Assert.Equal(0, api.Audit.Read(0, 10, null, null, null).TotalRecordCount);
    }

    [Fact]
    public async Task ADryRunOfAChange_AnswersTheStoredToken_SoTheSaveThatFollowsIsntRefused()
    {
        // A dry run's Role.UpdatedUtc is the stored role's, never a time that wasn't saved, so the admin page's
        // preview-then-save sends a token that matches.
        await using var api = await StartWithUsersAsync();
        var (_, created) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: KidRequest());
        var id = (string)created!["Role"]!["Id"]!;
        var stored = (string?)created["Role"]!["UpdatedUtc"];
        object Rename(string? token) => new { Name = "Little ones", Locks = new[] { "Profile" }, Libraries = new { Mode = "Only", Refs = new[] { new { Id = Kids.Id } } }, IfUpdatedAt = token };

        var (dry, preview) = await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Roles/" + id + "?dryRun=true", body: Rename(stored));
        var (saved, renamed) = await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Roles/" + id, body: Rename((string?)preview!["Role"]!["UpdatedUtc"]));

        Assert.Equal((200, 200), (dry, saved));
        Assert.Equal(stored, (string?)preview["Role"]!["UpdatedUtc"]);
        Assert.Equal("Little ones", (string?)preview["Role"]!["Name"]);
        Assert.NotEqual(stored, (string?)renamed!["Role"]!["UpdatedUtc"]);
        Assert.Equal("Little ones", api.Store.Current.Roles.Single().Name);
    }

    [Fact]
    public async Task ARoleWithALibraryModeANewerBuildWrote_KeepsItAndItsReferences_UnlessTheRequestSetsAMode()
    {
        // The keep-unknown rule.
        await using var api = await StartWithUsersAsync();
        var id = await CreateKidAsync(api);
        var smart = LenientEnum<AccessRolesRoleLibraryMode>.Parse("Smart");
        await api.Store.UpdateAsync(d => d with { Roles = [d.Roles.Single() with { Libraries = d.Roles.Single().Libraries with { Mode = smart } }] }, TestContext.Current.CancellationToken);
        var token = api.Store.Current.Roles.Single().UpdatedUtc;

        var (kept, _) = await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Roles/" + id.ToString("N"), body: new { Name = "Kid", Locks = new[] { "Home" }, IfUpdatedAt = token });
        var role = api.Store.Current.Roles.Single();
        var (replaced, _) = await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Roles/" + id.ToString("N"), body: new { Name = "Kid", Locks = new[] { "Home" }, Libraries = new { Mode = "All" }, IfUpdatedAt = role.UpdatedUtc });

        Assert.Equal(200, kept);
        Assert.Equal("Smart", role.Libraries.Mode.Name);
        Assert.Equal([Kids.Id], role.Libraries.Refs.Select(r => r.Id));
        Assert.Equal([AccessRolesLock.Home], role.KnownLocks);
        Assert.Equal(200, replaced);
        Assert.True(api.Store.Current.Roles.Single().Libraries.Mode.Is(AccessRolesRoleLibraryMode.All));
    }

    [Fact]
    public async Task DeletingARole_NeedsAChoiceForMembersItTakesOutOfLibraryManagement_AndAuditsTheirFrozenAccess()
    {
        await using var api = await StartWithUsersAsync();
        var kid = api.UserManager.Object.AllUsers().Single(u => u.Username == "kid");
        var id = await CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Users/" + kid.Id.ToString("N"), body: new { RoleIds = new[] { id } });

        var (refused, problem) = await api.JsonAsync(HttpMethod.Delete, "/AccessRoles/Admin/Roles/" + id.ToString("N"));
        var (status, deleted) = await api.JsonAsync(HttpMethod.Delete, "/AccessRoles/Admin/Roles/" + id.ToString("N") + "?onUnmanage=Keep");

        Assert.Equal(400, refused);
        Assert.Equal(AccessRolesException.UnmanageChoiceRequired, (string?)problem!["Code"]);
        Assert.Equal("kid", (string?)problem["Users"]![0]!["Name"]);
        Assert.Equal(200, status);
        Assert.Equal(["Keep"], deleted!["Preview"]!["Users"]!.AsArray().Select(u => (string?)u!["LeavesManagement"]!["Choice"]));
        Assert.Empty(api.Store.Current.Roles);
        Assert.Empty(api.Store.Current.Assignments);
        Assert.False(api.Store.Current.Ledger.ContainsKey(kid.Id));
        Assert.Equal(["RoleDeleted", "AssignmentChanged", "LibraryAccessFrozen"], api.Audit.Read(0, 3, null, null, null).Items.Reverse().Select(e => e.Action));
        var frozen = api.Audit.Read(0, 1, null, "LibraryAccessFrozen", null).Items.Single();
        Assert.Equal(kid.Id, frozen.TargetUserId);
        Assert.True(((System.Text.Json.JsonElement)frozen.Details!["EnableAllFolders"]!).GetBoolean());
        api.VerifyNoUserWritten();
    }

    [Fact]
    public async Task DeletingARole_CanReassignItsMembers_ButNotWhileItIsADefaultRole()
    {
        await using var api = await StartWithUsersAsync();
        var kid = api.UserManager.Object.AllUsers().Single(u => u.Username == "kid");
        var id = await CreateKidAsync(api);
        var (_, other) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: new { Name = "Little", Locks = new[] { "Home" }, Libraries = new { Mode = "Only", Refs = new[] { new { Id = Kids.Id } } } });
        var otherId = Guid.Parse((string)other!["Role"]!["Id"]!);
        await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Users/" + kid.Id.ToString("N"), body: new { RoleIds = new[] { id } });
        await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/DefaultRoles", body: new { RoleIds = new[] { id } });

        var (defaultStatus, defaultBody) = await api.JsonAsync(HttpMethod.Delete, "/AccessRoles/Admin/Roles/" + id.ToString("N") + "?reassignTo=" + otherId.ToString("N"));
        await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/DefaultRoles", body: new { RoleIds = Array.Empty<Guid>() });
        var (self, _) = await api.JsonAsync(HttpMethod.Delete, "/AccessRoles/Admin/Roles/" + id.ToString("N") + "?reassignTo=" + id.ToString("N"));
        var (status, _) = await api.JsonAsync(HttpMethod.Delete, "/AccessRoles/Admin/Roles/" + id.ToString("N") + "?reassignTo=" + otherId.ToString("N"));

        Assert.Equal(409, defaultStatus);
        Assert.Equal(AccessRolesException.DefaultRole, (string?)defaultBody!["Code"]);
        Assert.Equal(400, self);
        Assert.Equal(200, status);
        Assert.Equal([otherId], Assert.Single(api.Store.Current.Assignments).RoleIds);
    }

    [Fact]
    public async Task DeletingARole_WithIfMembers_IsRefusedWhenAMemberWasAddedOrChangedSince_AndChangesNothing()
    {
        // The delete dialog's members came from the page's load, so a reassignment moved a member
        // nobody saw. ifMembers is the role's MembersToken as read; a member added, or a member's assignment changed, since
        // then is a 409 Changed naming the members, and nothing is changed.
        await using var api = await StartWithUsersAsync();
        var kid = api.UserManager.Object.AllUsers().Single(u => u.Username == "kid");
        var viewer = api.UserManager.Object.AllUsers().Single(u => u.Username == "viewer");
        var id = await CreateKidAsync(api);
        var (_, other) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: new { Name = "Little", Locks = new[] { "Home" }, Libraries = new { Mode = "Only", Refs = new[] { new { Id = Kids.Id } } } });
        var otherId = Guid.Parse((string)other!["Role"]!["Id"]!);
        await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Users/" + kid.Id.ToString("N"), body: new { RoleIds = new[] { id } });
        var path = "/AccessRoles/Admin/Roles/" + id.ToString("N") + "?reassignTo=" + otherId.ToString("N") + "&ifMembers=";
        var read = await MembersTokenAsync(api, id);

        var (dry, dryBody) = await api.JsonAsync(HttpMethod.Delete, path + read + "&dryRun=true");
        var rolesAfterDryRun = api.Store.Current.Roles.Count;
        await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Users/" + viewer.Id.ToString("N"), body: new { RoleIds = new[] { id } });
        var auditBefore = api.Audit.Read(0, 1, null, null, null).TotalRecordCount;
        var (added, addedBody) = await api.JsonAsync(HttpMethod.Delete, path + read);
        var auditAfterAdded = api.Audit.Read(0, 1, null, null, null).TotalRecordCount;
        var reread = await MembersTokenAsync(api, id);
        var kidToken = api.Store.Current.Assignments.Single(a => a.UserId.Equals(kid.Id)).UpdatedUtc;
        await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Users/" + kid.Id.ToString("N"), body: new { RoleIds = new[] { id }, LockOverrides = new { Home = "Unlock" }, IfUpdatedAt = kidToken });
        var auditBeforeChanged = api.Audit.Read(0, 1, null, null, null).TotalRecordCount;
        var (changed, _) = await api.JsonAsync(HttpMethod.Delete, path + reread);
        var rolesAfterRefusals = api.Store.Current.Roles.Count;
        var auditAfterChanged = api.Audit.Read(0, 1, null, null, null).TotalRecordCount;
        var current = await MembersTokenAsync(api, id);
        var (status, _) = await api.JsonAsync(HttpMethod.Delete, path + current.ToUpperInvariant());

        Assert.Equal(200, dry);
        Assert.True((bool)dryBody!["DryRun"]!);
        Assert.Equal(2, rolesAfterDryRun);
        Assert.Equal(409, added);
        Assert.Equal(AccessRolesException.Changed, (string?)addedBody!["Code"]);
        Assert.Equal(["viewer"], addedBody["Users"]!.AsArray().Select(u => (string?)u!["Name"]));
        Assert.Equal(auditBefore, auditAfterAdded);
        Assert.NotEqual(read, reread);
        Assert.Equal(409, changed);
        Assert.Equal(auditBeforeChanged, auditAfterChanged);
        Assert.Equal(2, rolesAfterRefusals);
        Assert.NotEqual(reread, current);

        // The negative control: the token read now deletes the role and moves both members (a token's letter case aside).
        Assert.Equal(200, status);
        Assert.Equal(2, api.Store.Current.Assignments.Count);
        Assert.All(api.Store.Current.Assignments, a => Assert.Equal([otherId], a.RoleIds));
    }

    [Fact]
    public async Task AnUpdateWithIfMembers_IsRefusedWhenTheMembersChangedSince_AndADryRunAnswersTheStoredToken()
    {
        await using var api = await StartWithUsersAsync();
        var viewer = api.UserManager.Object.AllUsers().Single(u => u.Username == "viewer");
        var (_, created) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: KidRequest());
        var id = (string)created!["Role"]!["Id"]!;
        var stored = (string?)created["Role"]!["UpdatedUtc"];
        var members = (string?)created["Role"]!["MembersToken"];
        object Rename(string? token) => new { Name = "Little ones", Locks = new[] { "Profile" }, Libraries = new { Mode = "Only", Refs = new[] { new { Id = Kids.Id } } }, IfUpdatedAt = stored, IfMembers = token };

        var (dry, preview) = await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Roles/" + id + "?dryRun=true", body: Rename(members));
        await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Users/" + viewer.Id.ToString("N"), body: new { RoleIds = new[] { Guid.Parse(id) } });
        var (stale, staleBody) = await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Roles/" + id, body: Rename(members));
        var nameAfterStale = api.Store.Current.Roles.Single().Name;
        var (ok, _) = await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Roles/" + id, body: Rename(await MembersTokenAsync(api, Guid.Parse(id))));

        Assert.False(string.IsNullOrEmpty(members));
        Assert.Equal(200, dry);
        Assert.Equal(members, (string?)preview!["Role"]!["MembersToken"]);
        Assert.Equal(409, stale);
        Assert.Equal(AccessRolesException.Changed, (string?)staleBody!["Code"]);
        Assert.Equal(["viewer"], staleBody["Users"]!.AsArray().Select(u => (string?)u!["Name"]));
        Assert.Equal("Kid", nameAfterStale);
        Assert.Equal(200, ok);
        Assert.Equal("Little ones", api.Store.Current.Roles.Single().Name);
    }

    [Fact]
    public async Task DefaultRoles_AreStored_Shown_AndAudited_AndAnUnknownRoleIs404()
    {
        await using var api = await StartWithUsersAsync();
        var id = await CreateKidAsync(api);

        var (unknown, _) = await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/DefaultRoles", body: new { RoleIds = new[] { Guid.NewGuid() } });
        var (status, set) = await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/DefaultRoles", body: new { RoleIds = new[] { id } });
        var (_, roles) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Roles");

        Assert.Equal(404, unknown);
        Assert.Equal(200, status);
        Assert.Equal("Kid", (string?)set!["DefaultRoles"]![0]!["Name"]);
        Assert.True((bool)roles![0]!["IsDefault"]!);
        Assert.Equal([id], api.Store.Current.DefaultRoleIds);
        Assert.Single(api.Audit.Read(0, 10, null, "DefaultRolesChanged", null).Items);
    }

    [Fact]
    public async Task DefaultRoles_PreviewToken_CoversTheRolesSet_AndTheirDefinitions()
    {
        // A default roles change affects no existing user, so its PreviewToken was the
        // token of nothing (e3b0c442…), the same for every body, and a save of other roles with it was a 200.
        await using var api = await StartWithUsersAsync();
        var kid = await CreateKidAsync(api);
        var (_, family) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: new { Name = "Family", Libraries = new { Mode = "All" } });
        var familyId = Guid.Parse((string)family!["Role"]!["Id"]!);
        const string Path = "/AccessRoles/Admin/DefaultRoles";
        static string? Token(System.Text.Json.Nodes.JsonNode? answer) => (string?)answer!["Preview"]!["PreviewToken"];

        var (_, kidDry) = await api.JsonAsync(HttpMethod.Put, Path + "?dryRun=true", body: new { RoleIds = new[] { kid } });
        var (_, familyDry) = await api.JsonAsync(HttpMethod.Put, Path + "?dryRun=true", body: new { RoleIds = new[] { familyId } });
        var (other, otherProblem) = await api.JsonAsync(HttpMethod.Put, Path + "?ifPreview=" + Token(kidDry), body: new { RoleIds = new[] { familyId } });
        var afterOther = api.Store.Current.DefaultRoleIds;

        // A role among them edited since the dry run: its definition is another, so the token is stale.
        var (_, before) = await api.JsonAsync(HttpMethod.Put, Path + "?dryRun=true", body: new { RoleIds = new[] { kid } });
        var (edited, _) = await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Roles/" + kid.ToString("N"), body: new { Name = "Little ones", IfUpdatedAt = api.Store.Current.Roles.Single(r => r.Id.Equals(kid)).UpdatedUtc });
        var (stale, _) = await api.JsonAsync(HttpMethod.Put, Path + "?ifPreview=" + Token(before), body: new { RoleIds = new[] { kid } });
        var afterStale = api.Store.Current.DefaultRoleIds;

        // The negative control: the dry run seen now, and its token.
        var (_, now) = await api.JsonAsync(HttpMethod.Put, Path + "?dryRun=true", body: new { RoleIds = new[] { kid } });
        var (ok, _) = await api.JsonAsync(HttpMethod.Put, Path + "?ifPreview=" + Token(now), body: new { RoleIds = new[] { kid } });

        Assert.NotEqual(Token(kidDry), Token(familyDry));
        Assert.DoesNotContain(new[] { Token(kidDry), Token(familyDry) }, t => t!.StartsWith("e3b0c442", StringComparison.Ordinal));
        Assert.Equal((409, "Changed"), (other, (string?)otherProblem!["Code"]));
        Assert.Empty(afterOther);
        Assert.Equal((200, 409), (edited, stale));
        Assert.Empty(afterStale);
        Assert.NotEqual(Token(before), Token(now));
        Assert.Equal(200, ok);
        Assert.Equal([kid], api.Store.Current.DefaultRoleIds);
    }

    [Fact]
    public async Task AnApiKey_IsAuditedAsAnApiKey()
    {
        await using var api = await StartWithUsersAsync();

        await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", caller: "apikey", body: KidRequest());

        var actor = Assert.Single(api.Audit.Read(0, 10, null, null, null).Items).Actor;
        Assert.Equal(("ApiKey", (Guid?)null, (string?)null), (actor.Kind, actor.UserId, actor.Name));
    }

    [Fact]
    public async Task GetRole_Is404ForAnUnknownRole()
    {
        await using var api = await StartWithUsersAsync();

        var (status, body) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Roles/" + Guid.NewGuid().ToString("N"));

        Assert.Equal(404, status);
        Assert.Null(body!["Code"]);
    }

    [Fact]
    public async Task DeletingWithReassignTo_AndIfPreview_IsRefusedWhenTheTargetRoleWasEditedSince_AndMovesNobody()
    {
        // Regression: the dry run of deleting a role with its members reassigned showed the target's
        // libraries; another admin then gave the target every library, and the DELETE with ifUpdatedAt and ifMembers still
        // deleted and moved the members to it. ifPreview compares the effect.
        await using var api = await StartWithUsersAsync();
        var kid = api.UserManager.Object.AllUsers().Single(u => u.Username == "kid");
        var id = await CreateKidAsync(api);
        var (_, target) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: new { Name = "Shows only", Libraries = new { Mode = "Only", Refs = new[] { new { Id = Shows.Id } } } });
        var targetId = Guid.Parse((string)target!["Role"]!["Id"]!);
        await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Users/" + kid.Id.ToString("N"), body: new { RoleIds = new[] { id } });
        var path = "/AccessRoles/Admin/Roles/" + id.ToString("N") + "?reassignTo=" + targetId.ToString("N") + "&ifMembers=" + await MembersTokenAsync(api, id);

        var (_, dry) = await api.JsonAsync(HttpMethod.Delete, path + "&dryRun=true");
        var token = (string?)dry!["Preview"]!["PreviewToken"];
        await api.JsonAsync(HttpMethod.Put, "/AccessRoles/Admin/Roles/" + targetId.ToString("N"), body: new { Name = "Shows only", Locks = new[] { "Playback" }, Libraries = new { Mode = "All" }, IfUpdatedAt = (string?)target["Role"]!["UpdatedUtc"] });
        var audit = api.Audit.Read(0, 1, null, null, null).TotalRecordCount;
        var (stale, problem) = await api.JsonAsync(HttpMethod.Delete, path + "&ifPreview=" + token);
        var rolesAfterStale = api.Store.Current.Roles.Count;
        var kidRolesAfterStale = api.Store.Current.Assignments.Single(a => a.UserId.Equals(kid.Id)).RoleIds;
        var auditAfterStale = api.Audit.Read(0, 1, null, null, null).TotalRecordCount;
        var (_, now) = await api.JsonAsync(HttpMethod.Delete, path + "&dryRun=true");
        var (ok, _) = await api.JsonAsync(HttpMethod.Delete, path + "&ifPreview=" + (string?)now!["Preview"]!["PreviewToken"]);

        Assert.Equal(["Shows"], dry["Preview"]!["Users"]![0]!["LibrariesAdded"]!.AsArray().Select(l => (string?)l!["Name"]));
        Assert.Equal(409, stale);
        Assert.Equal(AccessRolesException.Changed, (string?)problem!["Code"]);
        Assert.Equal(["kid"], problem["Users"]!.AsArray().Select(u => (string?)u!["Name"]));
        Assert.Equal(2, rolesAfterStale);
        Assert.Equal([id], kidRolesAfterStale);
        Assert.Equal(audit, auditAfterStale);

        // The negative control: the dry run seen now (every library, Playback locked) deletes and reassigns.
        Assert.Equal(200, ok);
        Assert.Equal([targetId], api.Store.Current.Assignments.Single(a => a.UserId.Equals(kid.Id)).RoleIds);
    }

    [Fact]
    public async Task AStaleMembersToken_NamesOnlyTheMembersAddedRemovedOrChangedSince_AndAnUnknownOneEveryMemberNow()
    {
        // The 409 used to name every member now, so a member removed since went unnamed and the unchanged
        // ones were named.
        await using var api = await StartWithUsersAsync();
        var kid = api.UserManager.Object.AllUsers().Single(u => u.Username == "kid");
        var viewer = api.UserManager.Object.AllUsers().Single(u => u.Username == "viewer");
        var id = await CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk", body: new { UserIds = new[] { kid.Id, viewer.Id }, AddRoleIds = new[] { id } });
        var read = await MembersTokenAsync(api, id);
        await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Users/Bulk?onUnmanage=Keep", body: new { UserIds = new[] { viewer.Id }, RemoveRoleIds = new[] { id } });
        var path = "/AccessRoles/Admin/Roles/" + id.ToString("N") + "?dryRun=true&onUnmanage=Keep&ifMembers=";

        var (removed, removedBody) = await api.JsonAsync(HttpMethod.Delete, path + read);
        var (unknown, unknownBody) = await api.JsonAsync(HttpMethod.Delete, path + new string('0', 32));

        Assert.Equal(409, removed);
        Assert.Equal(["viewer"], removedBody!["Users"]!.AsArray().Select(u => (string?)u!["Name"]));
        Assert.Equal(409, unknown);
        Assert.Equal(["kid"], unknownBody!["Users"]!.AsArray().Select(u => (string?)u!["Name"]));
    }

    internal static object KidRequest() => new { Name = "Kid", Preset = "Kid", Libraries = new { Refs = new[] { new { Id = Kids.Id } } } };

    internal static async Task<Guid> CreateKidAsync(AccessRolesApi api)
    {
        var (_, created) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/Roles", body: KidRequest());
        return Guid.Parse((string)created!["Role"]!["Id"]!);
    }

    internal static async Task<AccessRolesApi> StartWithUsersAsync()
    {
        var api = await AccessRolesApi.StartAsync();
        api.AddUser("admin", admin: true);
        api.AddUser("kid");
        api.AddUser("viewer", allFolders: false, folders: [Movies, Shows]);
        return api;
    }

    private static Guid AdminId(AccessRolesApi api) => api.UserManager.Object.AllUsers().Single(u => u.Username == "admin").Id;

    private static async Task<string> MembersTokenAsync(AccessRolesApi api, Guid roleId)
    {
        var (_, role) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Roles/" + roleId.ToString("N"));
        return (string)role!["MembersToken"]!;
    }
}
