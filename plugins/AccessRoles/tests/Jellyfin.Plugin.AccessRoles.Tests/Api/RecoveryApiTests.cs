using System;
using System.Collections.Generic;
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
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Storage;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;
using Jellyfin.Plugin.Kit.TestSupport;

namespace Jellyfin.Plugin.AccessRoles.Tests.Api;

/// <summary>
/// Lockout and recovery's API: <c>POST Degraded/Resolve</c> and <c>POST ReleaseLocks</c>, each
/// with its dry run, token and 409.
/// </summary>
public sealed class RecoveryApiTests
{
    private const string ResolvePath = "/AccessRoles/Admin/Degraded/Resolve";
    private const string ReleasePath = "/AccessRoles/Admin/ReleaseLocks";

    [Fact]
    public async Task EndingDegradedMode_IsADryRun_ThenTheConfirmationWithItsToken_Audited()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = api.UserManager.Object.AllUsers().Single(u => u.Username == "kid").Id;
        WriteMarker(api, kid);

        using var user = await api.SendAsync(HttpMethod.Post, ResolvePath + "?dryRun=true", caller: "user");
        var (dryStatus, dry) = await api.JsonAsync(HttpMethod.Post, ResolvePath + "?dryRun=true");
        var (stale, refused) = await api.JsonAsync(HttpMethod.Post, ResolvePath + "?ifPreview=0123456789abcdef0123456789abcdef");
        var (none, refusedWithout) = await api.JsonAsync(HttpMethod.Post, ResolvePath);
        Assert.NotNull(api.Store.Degraded.Current);
        var (status, ended) = await api.JsonAsync(HttpMethod.Post, ResolvePath + "?ifPreview=" + (string?)dry!["PreviewToken"]);
        var (again, notDegraded) = await api.JsonAsync(HttpMethod.Post, ResolvePath + "?dryRun=true");

        Assert.Equal((403, 200, 409, 409, 200, 409), ((int)user.StatusCode, dryStatus, stale, none, status, again));
        Assert.True((bool)dry["DryRun"]!);
        Assert.True((bool)dry["FromEarlierStart"]!);
        Assert.Equal(["kid"], dry["Users"]!.AsArray().Select(u => (string?)u!["Name"]));
        Assert.Equal(("Changed", "Changed"), ((string?)refused!["Code"], (string?)refusedWithout!["Code"]));
        Assert.True((bool)ended!["Resolved"]!);
        Assert.Equal("NotDegraded", (string?)notDegraded!["Code"]);
        Assert.Null(api.Store.Degraded.Current);
        Assert.False(File.Exists(api.Store.Degraded.MarkerPath));
        var line = Assert.Single(api.Audit.Read(0, 10, null, AuditActions.DegradedModeEnded, null).Items);
        Assert.Equal("AdminConfirmed", line.Details!["Reason"]!.ToString());
    }

    [Fact]
    public async Task DegradedModeOfAReadOnlyStore_CantBeEndedHere()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var file = Path.Combine(api.DataPath, "accessroles", "accessroles.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "not json", TestContext.Current.CancellationToken);

        var (status, refused) = await api.JsonAsync(HttpMethod.Post, ResolvePath + "?dryRun=true");

        Assert.Equal(409, status);
        Assert.Equal("StoreReadOnly", (string?)refused!["Code"]);
        Assert.True(api.Store.Degraded.Current!.EveryNonAdmin);
    }

    [Fact]
    public async Task ReleasingTheLocks_IsADryRun_ThenTheConfirmation_FlagsBackOn_TheManagedExcluded_RolesKept_Audited()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var viewer = User(api, "viewer");
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, UserPath(kid), body: new { RoleIds = new[] { role } });
        await MirroredAsync(api, kid);

        using var user = await api.SendAsync(HttpMethod.Post, ReleasePath + "?dryRun=true", caller: "user");
        var (dryStatus, dry) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?dryRun=true");

        // A flag of someone else the mirror turned off after the preview: the confirmation is refused, naming only them.
        await MirroredAsync(api, viewer);
        var (stale, refused) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?ifPreview=" + (string?)dry!["PreviewToken"]);
        var (none, _) = await api.JsonAsync(HttpMethod.Post, ReleasePath);
        Assert.False(kid.EnableUserPreferenceAccess);
        Assert.Empty(api.Audit.Read(0, 10, null, AuditActions.LocksReleased, null).Items);
        var (_, again) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?dryRun=true");
        var (status, released) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?ifPreview=" + (string?)again!["PreviewToken"]);
        var (_, after) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?dryRun=true");

        Assert.Equal((403, 200, 409, 409, 200), ((int)user.StatusCode, dryStatus, stale, none, status));
        var row = Assert.Single(dry["Released"]!.AsArray())!;
        Assert.Equal(("kid", true, true, false), ((string?)row["Name"], (bool)row["PreferenceAccess"]!, (bool)row["Excluded"]!, (bool)dry["RestoreLibraries"]!));
        Assert.Null(row["Libraries"]);
        Assert.Equal("Changed", (string?)refused!["Code"]);
        Assert.Equal(["viewer"], refused["Users"]!.AsArray().Select(u => (string?)u!["Name"]));

        // Both flags are back on, the records gone; kid (managed) is Excluded with the role and the ledger kept; viewer, never
        // assigned, is only given the flag back; the library access stays as it is.
        Assert.False((bool)released!["DryRun"]!);
        Assert.Equal([("kid", "TurnedOn", true), ("viewer", "TurnedOn", false)], released["Released"]!.AsArray().Select(r => ((string?)r!["Name"], (string?)r["PreferenceOutcome"], (bool)r["Excluded"]!)));
        Assert.True(kid.EnableUserPreferenceAccess);
        Assert.True(viewer.EnableUserPreferenceAccess);
        Assert.True(kid.HasPermission(PermissionKind.EnableAllFolders));
        Assert.Empty(api.Store.Current.PreferenceMirror);
        var assignment = api.Store.Current.Assignments.Single();
        Assert.Equal((kid.Id, true, role), (assignment.UserId, assignment.State.Is(AccessRolesAssignmentState.Excluded), assignment.RoleIds.Single()));
        Assert.True(api.Store.Current.Ledger.ContainsKey(kid.Id));
        Assert.Empty(after!["Released"]!.AsArray());

        var line = Assert.Single(api.Audit.Read(0, 10, null, AuditActions.LocksReleased, null).Items);
        var users = JsonNode.Parse(JsonSerializer.Serialize(line.Details!["Users"]))!.AsArray();
        Assert.Equal(["kid", "viewer"], users.Select(u => (string?)u!["Name"]));
        Assert.Equal("true", JsonSerializer.Serialize(line.Details["Everyone"]));
        var restored = api.Audit.Read(0, 10, null, AuditActions.PreferenceAccessRestored, null).Items;
        Assert.Equal(2, restored.Count);
        Assert.All(restored, l => Assert.Equal("ReleaseLocks", l.Details!["ReleasedFor"]!.ToString()));
    }

    // The preview's token holds each user's native flag, so a flag turned back on after the
    // preview is a 409 Changed naming them, and nothing is written.
    [Fact]
    public async Task ReleasingTheLocks_AfterAFlagWasTurnedOnSinceThePreview_IsChanged()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        await MirroredAsync(api, kid);
        var (_, dry) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?dryRun=true");

        kid.EnableUserPreferenceAccess = true;
        var (status, refused) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?ifPreview=" + (string?)dry!["PreviewToken"]);

        Assert.Equal((409, "Changed"), (status, (string?)refused!["Code"]));
        Assert.Equal(["kid"], refused["Users"]!.AsArray().Select(u => (string?)u!["Name"]));
        Assert.Equal([kid.Id], api.Store.Current.PreferenceMirror.Keys);
        Assert.Empty(api.Audit.Read(0, 10, null, AuditActions.LocksReleased, null).Items);
    }

    [Fact]
    public async Task RestoringTheLibraries_WritesTheLedgersOriginal_WithoutADeletedLibrary_NorOneCreatedAgain()
    {
        // Delete, create again, then ReleaseLocks with restoreLibraries: the library created again isn't granted.
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var viewer = User(api, "viewer");
        viewer.SetPreference(PreferenceKind.EnabledFolders, [Movies.Id, Shows.Id, Horror.Id]);
        var role = await RolesApiTests.CreateKidAsync(api);
        await api.JsonAsync(HttpMethod.Put, UserPath(viewer), body: new { RoleIds = new[] { role } });
        Assert.Equal([Movies.Id, Shows.Id, Horror.Id], api.Store.Current.Ledger[viewer.Id].OriginalEnabledFolders);

        // The sync's write, standing in: the Kid role's library. Then Shows is deleted, and Horror created again under its id.
        viewer.SetPreference(PreferenceKind.EnabledFolders, [Kids.Id]);
        api.Libraries.Remove(Shows);
        await api.Store.UpdateAsync(
            d => d with { KnownLibraries = [.. d.KnownLibraries.Where(k => !k.Id.Equals(Horror.Id)), new KnownLibrary { Id = Horror.Id, Name = "Horror", Locations = Horror.Locations, CreatedUtc = Now.AddDays(-9), RecreatedUtc = Now, FirstSeenUtc = Now.AddDays(-9) }] },
            TestContext.Current.CancellationToken);

        var (_, flagsOnly) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?dryRun=true");
        var (dryStatus, dry) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?dryRun=true&restoreLibraries=true");
        var (otherQuery, _) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?ifPreview=" + (string?)dry!["PreviewToken"]);
        var (status, released) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?restoreLibraries=true&ifPreview=" + (string?)dry["PreviewToken"]);
        var (_, after) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?dryRun=true&restoreLibraries=true");

        Assert.Empty(flagsOnly!["Released"]!.AsArray());
        Assert.Equal((200, 409, 200), (dryStatus, otherQuery, status));
        var row = Assert.Single(dry["Released"]!.AsArray())!;
        Assert.Equal(("viewer", false, true), ((string?)row["Name"], (bool)row["PreferenceAccess"]!, (bool)row["Excluded"]!));
        Assert.False((bool)row["Libraries"]!["EnableAllFolders"]!);
        Assert.Equal([Movies.Id.ToString("N")], row["Libraries"]!["EnabledFolders"]!.AsArray().Select(f => (string?)f));
        Assert.Equal([Horror.Id.ToString("N")], row["RecreatedLeftOut"]!.AsArray().Select(f => (string?)f));

        Assert.Equal("Written", (string?)released!["Released"]![0]!["LibrariesOutcome"]);
        Assert.False(viewer.HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal([Movies.Id], viewer.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.True(api.Store.Current.Assignments.Single().State.Is(AccessRolesAssignmentState.Excluded));
        Assert.True(api.Store.Current.Ledger.ContainsKey(viewer.Id));
        Assert.Empty(after!["Released"]!.AsArray());
        var line = Assert.Single(api.Audit.Read(0, 10, null, AuditActions.LocksReleased, null).Items);
        Assert.Equal("true", JsonSerializer.Serialize(line.Details!["RestoreLibraries"]));
        Assert.Equal("Written", JsonNode.Parse(JsonSerializer.Serialize(line.Details["Users"]))![0]!["Libraries"]!.GetValue<string>());
    }

    [Fact]
    public async Task RestoringTheOnlyFullAdministratorsNarrowerOriginal_IsRefused_UntilConfirmed()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var admin = User(api, "admin");
        await api.Store.UpdateAsync(d => d with { Ledger = new Dictionary<Guid, LedgerEntry> { [admin.Id] = Ledger(false, Movies) } }, TestContext.Current.CancellationToken);

        var (_, dry) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?dryRun=true&restoreLibraries=true");
        var (refusedStatus, _) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?restoreLibraries=true&ifPreview=" + (string?)dry!["PreviewToken"]);
        Assert.True(admin.HasPermission(PermissionKind.EnableAllFolders));
        api.VerifyNoUserWritten();
        var (_, confirmed) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?dryRun=true&restoreLibraries=true&confirmNoFullAdmin=true");
        var (status, _) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?restoreLibraries=true&confirmNoFullAdmin=true&ifPreview=" + (string?)confirmed!["PreviewToken"]);

        var row = Assert.Single(dry["Released"]!.AsArray())!;
        Assert.Equal(("admin", "NoFullAdministrator", false), ((string?)row["Name"], (string?)row["LibrariesSkipped"], (bool)row["Excluded"]!));
        Assert.Null(row["Libraries"]);
        Assert.Equal((200, 200), (refusedStatus, status));
        Assert.Equal([Movies.Id.ToString("N")], confirmed["Released"]![0]!["Libraries"]!["EnabledFolders"]!.AsArray().Select(f => (string?)f));
        Assert.False(admin.HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal([Movies.Id], admin.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task ReleasingTheLocksOfAnUnknownUser_IsNotFound_NamingIt_EvenAfterAnEmptyId()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var unknownId = Guid.NewGuid();

        var (unknown, problem) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?dryRun=true", body: new { UserIds = new[] { Guid.Empty, unknownId } });
        var (dryStatus, dry) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?dryRun=true&restoreLibraries=true", body: new { UserIds = new[] { kid.Id } });

        Assert.Equal((404, "UnknownUser"), (unknown, (string?)problem!["Code"]));
        Assert.Contains(unknownId.ToString("N"), (string?)problem["detail"], StringComparison.Ordinal);
        Assert.Equal(200, dryStatus);

        // A user asked for is listed whatever there is to release: kid has no ledger entry.
        Assert.Equal("NoLedgerEntry", (string?)dry!["Released"]![0]!["LibrariesSkipped"]);
        api.VerifyNoUserWritten();
    }

    // A read-only store releases too. The mirror's records come from Degraded mode's evidence (the
    // file's lenient read), the flags go back on, nobody becomes Excluded (the file can't be written), and the file is left
    // as it is; the records are forgotten, so nothing is listed again.
    [Fact]
    public async Task ReleasingTheLocksOfAReadOnlyStore_PutsBackTheFlagsItsEvidenceRecords_ExcludingNobody()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var viewer = User(api, "viewer");
        var file = Path.Combine(api.DataPath, "accessroles", "accessroles.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var broken = "{\"Version\":1,\"Roles\":\"corrupt\",\"Assignments\":[{\"UserId\":\"" + kid.Id.ToString("N") + "\",\"State\":\"Managed\"}],\"PreferenceMirror\":{\"" + kid.Id.ToString("N") + "\":{},\"" + viewer.Id.ToString("N") + "\":{}}}";
        await File.WriteAllTextAsync(file, broken, TestContext.Current.CancellationToken);
        kid.EnableUserPreferenceAccess = false;

        var (dryStatus, dry) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?dryRun=true");
        var (status, released) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?ifPreview=" + (string?)dry!["PreviewToken"]);
        var (_, after) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?dryRun=true");

        Assert.Equal(StoreStatus.ReadOnlyDataUnreadable, api.Store.Status);
        Assert.Equal((200, 200), (dryStatus, status));
        Assert.True((bool)dry["StoreReadOnly"]!);

        // The viewer's flag is on: nothing to release for them.
        var row = Assert.Single(dry["Released"]!.AsArray())!;
        Assert.Equal(("kid", true, false), ((string?)row["Name"], (bool)row["PreferenceAccess"]!, (bool)row["Excluded"]!));
        Assert.Equal([("kid", "TurnedOn")], released!["Released"]!.AsArray().Select(r => ((string?)r!["Name"], (string?)r["PreferenceOutcome"])));
        Assert.True(kid.EnableUserPreferenceAccess);
        Assert.Equal(broken, await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(kid.Id, api.Store.Degraded.MirrorEvidence);
        Assert.Empty(after!["Released"]!.AsArray());
        Assert.Single(api.Audit.Read(0, 10, null, AuditActions.LocksReleased, null).Items);
    }

    // A release on a read-only store whose own assignments are read (a newer schema version's): the mirror's record is the
    // file's and the user is managed in it, yet nobody becomes Excluded, since that needs the file written; the flag goes
    // back on and the file is left as it is.
    [Fact]
    public async Task ReleasingTheLocksOfANewerVersionsStore_ExcludesNobody_ItsManagedUserKept()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = User(api, "kid");
        var file = Path.Combine(api.DataPath, "accessroles", "accessroles.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var newer = "{\"Version\":2,\"Assignments\":[{\"UserId\":\"" + kid.Id.ToString() + "\",\"State\":\"Managed\",\"UpdatedUtc\":\"2026-10-19T12:00:00Z\"}],\"PreferenceMirror\":{\"" + kid.Id.ToString() + "\":{}}}";
        await File.WriteAllTextAsync(file, newer, TestContext.Current.CancellationToken);
        kid.EnableUserPreferenceAccess = false;

        var (dryStatus, dry) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?dryRun=true");
        var (status, released) = await api.JsonAsync(HttpMethod.Post, ReleasePath + "?ifPreview=" + (string?)dry!["PreviewToken"]);

        Assert.Equal(StoreStatus.ReadOnlyNewerVersion, api.Store.Status);
        Assert.Null(api.Store.Degraded.Current);
        Assert.Equal((200, 200), (dryStatus, status));
        Assert.True((bool)dry["StoreReadOnly"]!);
        var row = Assert.Single(dry["Released"]!.AsArray())!;
        Assert.Equal(("kid", true, false), ((string?)row["Name"], (bool)row["PreferenceAccess"]!, (bool)row["Excluded"]!));
        var done = Assert.Single(released!["Released"]!.AsArray())!;
        Assert.Equal(("kid", "TurnedOn", false), ((string?)done["Name"], (string?)done["PreferenceOutcome"], (bool)done["Excluded"]!));
        Assert.True(kid.EnableUserPreferenceAccess);
        Assert.True(Assert.Single(api.Store.Current.Assignments).State.Is(AccessRolesAssignmentState.Managed));
        Assert.Equal(newer, await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
    }

    // A marker that can't be deleted is a 503 with its code, nothing changed.
    [Fact]
    public async Task EndingDegradedMode_WhenTheMarkerCantBeDeleted_IsUnavailable_WithACode()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var kid = api.UserManager.Object.AllUsers().Single(u => u.Username == "kid").Id;
        WriteMarker(api, kid);
        var (_, dry) = await api.JsonAsync(HttpMethod.Post, ResolvePath + "?dryRun=true");

        int status;
        JsonNode? problem;
        using (new FileStream(api.Store.Degraded.MarkerPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            (status, problem) = await api.JsonAsync(HttpMethod.Post, ResolvePath + "?ifPreview=" + (string?)dry!["PreviewToken"]);
        }

        Assert.Equal((503, "MarkerNotDeleted"), (status, (string?)problem!["Code"]));
        Assert.NotNull(api.Store.Degraded.Current);
        Assert.Empty(api.Audit.Read(0, 10, null, AuditActions.DegradedModeEnded, null).Items);
    }

    private static string UserPath(User user) => "/AccessRoles/Admin/Users/" + user.Id.ToString("N");

    private static User User(AccessRolesApi api, string name) => api.UserManager.Object.AllUsers().Single(u => u.Username == name);

    // A flag the mirror turned off: off natively, and its record.
    private static async Task MirroredAsync(AccessRolesApi api, User user)
    {
        user.EnableUserPreferenceAccess = false;
        await api.Store.UpdateAsync(d => d with { PreferenceMirror = new Dictionary<Guid, MirroredPreference>(d.PreferenceMirror) { [user.Id] = new MirroredPreference { WrittenUtc = Now } } }, TestContext.Current.CancellationToken);
    }

    private static void WriteMarker(AccessRolesApi api, Guid userId)
    {
        var folder = Path.Combine(api.DataPath, "accessroles");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, DegradedMode.MarkerFileName),
            "{\"Version\":1,\"Because\":\"ReadOnlyDataUnreadable\",\"SinceUtc\":\"2026-10-19T12:00:00Z\",\"Source\":\"accessroles.json\",\"EveryNonAdmin\":false,\"Users\":[\"" + userId.ToString("N") + "\"]}");
    }
}
