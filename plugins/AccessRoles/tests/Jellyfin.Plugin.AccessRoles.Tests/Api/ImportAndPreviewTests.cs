using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Api;

/// <summary>
/// <i>Import current setup</i> (the <c>ImportExisting</c> action; it must show "no change" on an existing household's
/// layout, here a synthetic one: 9 users, 3 administrators, 8 libraries, 7 users restricted natively) and the roles API's
/// previews resolved as the sync resolves (a pending rename's new library withheld from Effective and the Users list).
/// </summary>
public sealed class ImportAndPreviewTests : IDisposable
{
    private static readonly CatalogLibrary Music = Library(6, "Music", "music");
    private static readonly CatalogLibrary KidsShows = Library(7, "Kids Shows", "tvshows");
    private static readonly CatalogLibrary HomeVideos = Library(8, "Home Videos", "homevideos");

    private readonly SyncHarness _h = new();
    private readonly AccessRolesAdminService _admin;
    private readonly ClaimsPrincipal _caller = new(new ClaimsIdentity([new Claim("Jellyfin-UserId", Guid.NewGuid().ToString("N")), new Claim(ClaimTypes.Role, "Administrator")], "Test"));

    public ImportAndPreviewTests()
    {
        _h.Catalog.Libraries.AddRange([Music, KidsShows, HomeVideos]);
        _admin = new AccessRolesAdminService(_h.Store, _h.Catalog, new JellyfinServerUsers(_h.Server.Manager.Object), _h.Audit, () => throw new NotSupportedException(), () => _h.Sync, NullLogger.Instance, _h.Clock);
    }

    [Fact]
    public async Task TheImport_OnAHouseholdLayout_ProposesOneRolePerDistinctAccess_WithNoChangeForAnyone_AndAppliesWithoutAWrite()
    {
        var users = HouseholdLayout();

        var dry = await _admin.ImportExistingAsync(new AccessRolesWriteOptions { DryRun = true }, false, _caller, TestContext.Current.CancellationToken);

        Assert.True(dry.DryRun);
        Assert.Empty(_h.Store.Current.Roles);
        Assert.Equal(
            ["Home Videos", "Kids + Kids Shows", "Kids + Kids Shows + Movies + Music + Shows", "Kids + Movies + Shows", "Movies"],
            dry.ProposedRoles.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.All(dry.ProposedRoles, r => Assert.Equal(AccessRolesRoleLibraryMode.Only, r.Mode));
        Assert.Equal(["kid1", "kid2"], dry.ProposedRoles.Single(r => r.Name == "Kids + Kids Shows").Members.Select(m => m.Name));
        Assert.Equal(6, dry.Assignments.Count);
        Assert.Equal(6, dry.NoChange);
        Assert.All(dry.EffectiveChanges, c => Assert.True(c.LibrariesAdded.Count == 0 && c.LibrariesRemoved.Count == 0 && !c.PolicyWrite, c.Name));
        Assert.Equal(["admin2", "admin3", "parent"], dry.Warnings.Where(w => w.Code == AccessRolesImport.Administrator).Select(w => w.Name).Order(StringComparer.Ordinal));
        // EnableLiveTvManagement is on for every new user before 10.11.7 (the double's defaults too): one warning, not six.
        var liveTv = Assert.Single(dry.Warnings, w => w.Code == AccessRolesImport.LiveTvManagement);
        Assert.Equal(["family", "guest", "kid1", "kid2", "teen", "viewer"], liveTv.Users.Select(u => u.Name));
        Assert.Equal((true, (Guid?)null), (liveTv.Imported, liveTv.UserId));
        Assert.Empty(_h.Lines());

        var applied = await _admin.ImportExistingAsync(new AccessRolesWriteOptions(), false, _caller, TestContext.Current.CancellationToken);

        Assert.False(applied.DryRun);
        Assert.Equal(5, _h.Store.Current.Roles.Count);
        Assert.Equal(6, _h.Store.Current.Assignments.Count);
        Assert.All(applied.ProposedRoles, r => Assert.NotNull(r.Id));
        Assert.Single(_h.Lines(AuditActions.ImportApplied));
        Assert.Equal(5, _h.Lines(AuditActions.RoleCreated).Count);
        Assert.Equal(6, _h.Lines(AuditActions.AssignmentChanged).Count);

        // Every imported user's ledger holds their access as it is, and the sync writes nobody ("no policy is written,
        // because nothing differs"); a second import is refused.
        var report = await _h.RunAsync(SyncTrigger.StoreChanged);
        Assert.Empty(report.Users);
        Assert.Empty(_h.Server.Writes);
        Assert.Equal(6, report.ManagedUsers);
        foreach (var user in users.Where(u => !u.HasPermission(PermissionKind.IsAdministrator)))
        {
            Assert.NotNull(_h.Store.Current.Ledger[user.Id].LastWrite);
        }

        var again = await Assert.ThrowsAsync<AccessRolesException>(() => _admin.ImportExistingAsync(new AccessRolesWriteOptions { DryRun = true }, false, _caller, TestContext.Current.CancellationToken));
        Assert.Equal((PluginErrorKind.Conflict, AccessRolesException.AlreadyManaging), (again.Kind, again.Code));
    }

    [Fact]
    public async Task TheImport_LeavesOutWhoCantBeImportedWithoutAChange_AndSaysWhy()
    {
        _h.Server.Add("everything", allFolders: true);
        var none = _h.Server.Add("nothing", allFolders: false, folders: []);
        var blocked = _h.Server.Add("blocked", allFolders: false, folders: [Movies.Id]);
        blocked.SetPreference(PreferenceKind.BlockedMediaFolders, [Horror.Id]);
        var stale = _h.Server.Add("stale", allFolders: false, folders: [Movies.Id, Library(9, "Gone").Id]);
        var recreated = _h.Server.Add("recreated", allFolders: false, folders: [Shows.Id]);
        await _h.SaveAsync(d => d with
        {
            Roles = [Role(1, "Movies")],
            KnownLibraries = [new KnownLibrary { Id = Shows.Id, Name = "Shows", Locations = Shows.Locations, CreatedUtc = _h.Now.AddDays(-9), RecreatedUtc = _h.Now, FirstSeenUtc = _h.Now.AddDays(-9) }]
        });

        var dry = await _admin.ImportExistingAsync(new AccessRolesWriteOptions { DryRun = true }, false, _caller, TestContext.Current.CancellationToken);

        // Every library: left unmanaged, in one warning. Movies: taken by an existing role's name, so
        // "Movies (2)".
        Assert.Equal(["Movies (2)"], dry.ProposedRoles.Select(r => r.Name));
        Assert.Equal(["stale"], dry.ProposedRoles[0].Members.Select(m => m.Name));
        var every = Assert.Single(dry.Warnings, w => w.Code == AccessRolesImport.EveryLibrary);
        Assert.Equal(["everything"], every.Users.Select(u => u.Name));
        Assert.False(every.Imported);
        Assert.Contains("Also manage users who see every library", every.Message, StringComparison.Ordinal);
        Assert.Equal((AccessRolesImport.NoLibraries, false), Code(dry, none));
        Assert.Equal((AccessRolesImport.CannotManage, false), Code(dry, blocked));
        Assert.Equal((AccessRolesImport.AccessWouldChange, false), Code(dry, recreated));
        Assert.Contains(dry.Warnings, w => w.Code == AccessRolesImport.DeletedLibraries && w.UserId.Equals(stale.Id) && w.Imported);
        var staleRow = dry.EffectiveChanges.Single(c => c.UserId.Equals(stale.Id));
        Assert.True(staleRow.PolicyWrite);
        Assert.Empty(staleRow.LibrariesRemoved);
        Assert.Equal(0, dry.NoChange);
    }

    [Fact]
    public async Task OptedIn_UsersWithEveryLibrary_GetTheAllLibrariesRole_WithOneWarning_AndTheLiveTvUsersAreOneWarning()
    {
        // "Also manage users who see every library" gives them an "All libraries" role (their
        // later stock-page edits are then overridden); the users who may manage Live TV are listed in one warning.
        var first = _h.Server.Add("everything", allFolders: true);
        var second = _h.Server.Add("everyone", allFolders: true);
        var kid = _h.Server.Add("kid", allFolders: false, folders: [Kids.Id]);
        _h.Server.AdminPost(second.Id, p => p.EnableLiveTvManagement = false);

        var dry = await _admin.ImportExistingAsync(new AccessRolesWriteOptions { DryRun = true }, true, _caller, TestContext.Current.CancellationToken);

        Assert.Equal(["All libraries", "Kids"], dry.ProposedRoles.Select(r => r.Name));
        Assert.Equal(AccessRolesRoleLibraryMode.All, dry.ProposedRoles[0].Mode);
        Assert.Equal(["everyone", "everything"], dry.ProposedRoles[0].Members.Select(m => m.Name));
        Assert.Equal(3, dry.NoChange);
        Assert.DoesNotContain(dry.Warnings, w => w.Code == AccessRolesImport.EveryLibrary);
        var managed = Assert.Single(dry.Warnings, w => w.Code == AccessRolesImport.EveryLibraryManaged);
        Assert.Equal(["everyone", "everything"], managed.Users.Select(u => u.Name));
        Assert.True(managed.Imported);
        Assert.Contains("overridden", managed.Message, StringComparison.Ordinal);
        var liveTv = Assert.Single(dry.Warnings, w => w.Code == AccessRolesImport.LiveTvManagement);
        Assert.Equal(["everything", "kid"], liveTv.Users.Select(u => u.Name));
        Assert.Contains("10.11.7", liveTv.Message, StringComparison.Ordinal);

        // Applied with the box: the audit says so, and the sync writes nobody.
        await _admin.ImportExistingAsync(new AccessRolesWriteOptions(), true, _caller, TestContext.Current.CancellationToken);
        Assert.Equal("true", SyncHarness.Detail(Assert.Single(_h.Lines(AuditActions.ImportApplied)), "ManageUsersWithEveryLibrary"));
        Assert.Empty((await _h.RunAsync(SyncTrigger.StoreChanged)).Users);
        Assert.Empty(_h.Server.Writes);
    }

    [Fact]
    public async Task ThePreviews_WithholdAPendingRenamesNewLibrary_AsTheSyncDoes()
    {
        // Role AllExcept [Kids] and a user's Remove [Kids]; Kids is renamed to Secret2, whose item isn't there yet. The sync
        // withholds Secret2 until the rename is re-pointed; Effective, the Users list and Preview now
        // resolve the same way, so none shows it as granted.
        var b = _h.Server.Add("b", allFolders: true);
        var c = _h.Server.Add("c", allFolders: true);
        var only = _h.Server.Add("only", allFolders: true);
        var allBut = Role(1, "AllBut", mode: AccessRolesRoleLibraryMode.AllExcept, refs: [Kids]);
        var everything = Role(2, "All", mode: AccessRolesRoleLibraryMode.All);
        var kidsOnly = Role(3, "KidsOnly", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await _h.SaveAsync(d => d with
        {
            Roles = [allBut, everything, kidsOnly],
            Assignments = [Assign(SyncHarness.Read(b), [allBut]), Assign(SyncHarness.Read(c), [everything], remove: [Kids]), Assign(SyncHarness.Read(only), [kidsOnly])],
            Ledger = new Dictionary<Guid, LedgerEntry> { [b.Id] = Ledger(true), [c.Id] = Ledger(true), [only.Id] = Ledger(true) }
        });
        Assert.Equal(3, (await _h.RunAsync()).Written);
        var renamed = Library(17, "Secret2") with { Locations = Kids.Locations };
        await _h.Sync.RecordRenameAsync(Kids.Id, "Kids", "Secret2", TestContext.Current.CancellationToken);
        _h.Catalog.Libraries.RemoveAll(l => l.Id.Equals(Kids.Id));
        _h.Catalog.Libraries.Add(renamed with { HasItem = false });

        foreach (var user in new[] { b, c })
        {
            var effective = _admin.GetEffective(user.Id, null);
            Assert.DoesNotContain(effective.Libraries.Visible, v => v.Id.Equals(renamed.Id));
            Assert.False(effective.Libraries.EnableAllFolders);
            Assert.DoesNotContain(renamed.Id, effective.Libraries.PendingWrite?.Add ?? []);
            Assert.False(_admin.GetUsers(null, null).Single(u => u.UserId.Equals(user.Id)).PendingWrite);
        }

        // The Only user keeps the old id while the rename waits.
        Assert.Contains(_admin.GetEffective(only.Id, null).Libraries.Visible, v => v.Id.Equals(Kids.Id));
        var preview = _admin.Preview(new AccessRolesPreviewRequest { Assignments = [new AccessRolesUserRequest { UserId = c.Id, RoleIds = [everything.Id], Libraries = new AccessRolesUserLibrariesRequest { Remove = [Kids.Id, Movies.Id] } }] });
        var row = Assert.Single(preview.Users);
        Assert.DoesNotContain(row.LibrariesAdded, l => l.Id.Equals(renamed.Id));
        Assert.Equal([Movies.Id], row.LibrariesRemoved.Select(l => l.Id));
    }

    [Fact]
    public async Task ImportExisting_OverHttp_AnswersTheProposal_SavesItWithoutDryRun_AndIs409AlreadyManagingAfterwards()
    {
        await using var api = await AccessRolesApi.StartAsync();
        api.AddUser("admin", admin: true);
        var kid = api.AddUser("kid", allFolders: false, folders: [Kids]);

        var everything = api.AddUser("everything", allFolders: true);
        var (dryStatus, dry) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/ImportExisting?dryRun=true");
        Assert.Equal(200, dryStatus);
        Assert.True((bool)dry!["DryRun"]!);
        Assert.Equal("Kids", (string?)Assert.Single(dry["ProposedRoles"]!.AsArray())!["Name"]);
        Assert.Equal(1, (int)dry["NoChange"]!);
        Assert.Empty(api.Store.Current.Roles);
        var (optedStatus, opted) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/ImportExisting?dryRun=true&manageUsersWithEveryLibrary=true");
        Assert.Equal(200, optedStatus);
        Assert.Equal(["All libraries", "Kids"], opted!["ProposedRoles"]!.AsArray().Select(r => (string?)r!["Name"]));
        Assert.Equal(everything.Id, Guid.Parse((string)opted["ProposedRoles"]![0]!["Members"]![0]!["UserId"]!));

        var (status, saved) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/ImportExisting");
        Assert.Equal(200, status);
        Assert.False((bool)saved!["DryRun"]!);
        Assert.Equal(kid.Id, Assert.Single(api.Store.Current.Assignments).UserId);
        Assert.NotEmpty(saved["AuditSeq"]!.AsArray());

        var (again, problem) = await api.JsonAsync(HttpMethod.Post, "/AccessRoles/Admin/ImportExisting?dryRun=true");
        Assert.Equal(409, again);
        Assert.Equal(AccessRolesException.AlreadyManaging, (string?)problem!["Code"]);
        api.VerifyNoUserWritten();
    }

    [Fact]
    public async Task ApplyingTheImport_WithIfPreview_IsRefusedWhenSomeonesAccessChangedSinceItsDryRun_AndSavesNothing()
    {
        // Apply sends the dry run's PreviewToken; the import proposes roles from each user's
        // access as it is at the save, so a user whose access changed since would get another role than the preview said.
        var viewer = _h.Server.Add("viewer", allFolders: false, folders: [Movies.Id]);
        _h.Server.Add("kid", allFolders: false, folders: [Kids.Id]);
        var token = TestContext.Current.CancellationToken;
        var dry = await _admin.ImportExistingAsync(new AccessRolesWriteOptions { DryRun = true }, false, _caller, token);
        viewer.SetPreference(PreferenceKind.EnabledFolders, new[] { Movies.Id, Shows.Id });

        var refused = await Assert.ThrowsAsync<AccessRolesException>(() => _admin.ImportExistingAsync(new AccessRolesWriteOptions { IfPreview = dry.PreviewToken }, false, _caller, token));

        Assert.False(string.IsNullOrEmpty(dry.PreviewToken));
        Assert.Equal(["Kids", "Movies"], dry.ProposedRoles.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.Equal(AccessRolesException.Changed, refused.Code);
        Assert.Equal(["viewer"], refused.Users.Select(u => u.Name));
        Assert.Empty(_h.Store.Current.Roles);
        Assert.Empty(_h.Store.Current.Assignments);
        Assert.Empty(_h.Lines());

        // The negative control: the dry run seen now applies.
        var now = await _admin.ImportExistingAsync(new AccessRolesWriteOptions { DryRun = true }, false, _caller, token);
        var applied = await _admin.ImportExistingAsync(new AccessRolesWriteOptions { IfPreview = now.PreviewToken }, false, _caller, token);

        Assert.NotEqual(dry.PreviewToken, now.PreviewToken);
        Assert.Equal(["Kids", "Movies + Shows"], applied.ProposedRoles.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.Equal(2, _h.Store.Current.Assignments.Count);
    }

    public void Dispose() => _h.Dispose();

    private static (string Code, bool Imported) Code(AccessRolesImportDto import, User user)
        => import.Warnings.Single(w => w.UserId.Equals(user.Id)) is var w ? (w.Code, w.Imported) : default;

    // A household server's layout (9 users, 3 administrators, 8 libraries, 7 users restricted
    // natively), with synthetic names and library sets.
    private List<User> HouseholdLayout()
    {
        var users = new List<User>
        {
            _h.Server.Add("parent", admin: true, allFolders: true),
            _h.Server.Add("admin2", admin: true, allFolders: true),
            _h.Server.Add("admin3", admin: true, allFolders: false, folders: [Movies.Id, Shows.Id]),
            _h.Server.Add("kid1", allFolders: false, folders: [Kids.Id, KidsShows.Id]),
            _h.Server.Add("kid2", allFolders: false, folders: [KidsShows.Id, Kids.Id]),
            _h.Server.Add("teen", allFolders: false, folders: [Movies.Id, Shows.Id, Kids.Id]),
            _h.Server.Add("guest", allFolders: false, folders: [Movies.Id]),
            _h.Server.Add("family", allFolders: false, folders: [Movies.Id, Shows.Id, Kids.Id, KidsShows.Id, Music.Id]),
            _h.Server.Add("viewer", allFolders: false, folders: [HomeVideos.Id])
        };
        _h.Server.AdminPost(users[7].Id, p => p.EnableLiveTvManagement = true);
        Assert.Equal(7, users.Count(u => !u.HasPermission(PermissionKind.EnableAllFolders)));
        return users;
    }
}
