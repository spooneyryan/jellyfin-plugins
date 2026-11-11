using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Sync;

/// <summary>
/// Every status the kit's JSON store comes up with, as the library sync reads it:
/// fail closed. A store the kit restored from a snapshot at load holds the sync until an admin
/// confirms (the rule after a restore), across a restart; a missing file none of whose snapshots can be restored is
/// read-only like a newer version's; the other statuses keep their earlier mapping.
/// </summary>
public sealed class StoreStatusTests : IDisposable
{
    private readonly SyncHarness _h = new();

    [Fact]
    public void EveryStoreStatus_IsOneThisBuildMaps()
    {
        // A status the kit adds later must be looked at here and in LibrarySync.PauseReason before this passes.
        Assert.Equal(
            [StoreStatus.Ok, StoreStatus.Migrated, StoreStatus.RecoveredFromBackup, StoreStatus.RecoveredEmpty, StoreStatus.RestoredFromSnapshot, StoreStatus.ReadOnlyNewerVersion, StoreStatus.ReadOnlyMigrationFailed, StoreStatus.ReadOnlyDataMissing, StoreStatus.ReadOnlyDataUnreadable],
            Enum.GetValues<StoreStatus>());
    }

    [Fact]
    public async Task AStoreRestoredFromASnapshotAtLoad_HoldsTheSync_UntilAnAdminConfirms_AndADryRunShowsWhatItWouldWrite()
    {
        var kid = _h.Server.Add("kid", allFolders: true);
        await SnapshotThenLoseTheFileAsync(kid.Id);

        Assert.Equal(StoreStatus.RestoredFromSnapshot, _h.Store.Status);
        Assert.False(_h.Store.IsReadOnly);
        var held = await _h.RunAsync(SyncTrigger.Startup);
        var dry = await _h.RunAsync(SyncTrigger.Manual, r => r with { DryRun = true, ConfirmRestore = true });

        Assert.Equal(SyncReasons.StoreRestored, held.Paused);
        Assert.Equal(SyncReasons.StoreRestored, dry.Paused);
        Assert.Equal(SyncActionKind.Write, Assert.Single(dry.Users).Kind);
        Assert.Empty(_h.Server.Writes);
        Assert.True(File.Exists(_h.Paths.RestoreHoldFile));
        Assert.Contains(SyncReasons.StoreRestored, await File.ReadAllTextAsync(_h.Paths.RestoreHoldFile, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(SyncReasons.StoreRestored, SyncHarness.Detail(Assert.Single(_h.Lines(AuditActions.LibrarySyncPaused)), "Reason"));

        var confirmed = await _h.RunAsync(SyncTrigger.Manual, r => r with { Actor = new AuditActor(AuditActor.AdminKind, Guid.NewGuid(), "admin"), ConfirmRestore = true });

        Assert.Null(confirmed.Paused);
        Assert.True(Assert.Single(confirmed.Users).Wrote);
        Assert.False(_h.Server.Cached(kid.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.False(File.Exists(_h.Paths.RestoreHoldFile));
        var resumed = Assert.Single(_h.Lines(AuditActions.LibrarySyncResumed));
        Assert.Equal((SyncReasons.StoreRestored, "admin"), (SyncHarness.Detail(resumed, "Reason"), resumed.Actor.Name));
        Assert.Null(_h.Sync.PauseReason());
    }

    [Fact]
    public async Task AConfirmationBeforeTheHoldsDryRun_IsRefused409_AndTheSyncStaysHeld_ThenTheDryRunAndTheConfirmationLiftIt()
    {
        // "Until the admin has seen the sync dry run": confirmRestore=true alone used to release the hold.
        var kid = _h.Server.Add("kid", allFolders: true);
        await SnapshotThenLoseTheFileAsync(kid.Id);
        Assert.Equal(SyncReasons.StoreRestored, (await _h.RunAsync(SyncTrigger.Startup)).Paused);
        var admin = new AccessRolesAdminService(_h.Store, _h.Catalog, new JellyfinServerUsers(_h.Server.Manager.Object), _h.Audit, () => throw new NotSupportedException(), () => _h.Sync, NullLogger.Instance, _h.Clock);
        var caller = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", Guid.NewGuid().ToString("N")), new Claim(ClaimTypes.Role, "Administrator")], "Test"));
        var token = TestContext.Current.CancellationToken;

        var refused = await Assert.ThrowsAsync<AccessRolesException>(() => admin.SyncAsync(new AccessRolesSyncOptions { ConfirmRestore = true }, caller, token));
        Assert.Equal(AccessRolesException.DryRunRequired, refused.Code);
        Assert.True(_h.Sync.RestoreNeedsDryRun());
        Assert.Equal(SyncReasons.StoreRestored, _h.Sync.PauseReason());
        Assert.Empty(_h.Server.Writes);

        // The sync itself never lifts the hold without the dry run either (the service's check aside).
        Assert.Equal(SyncReasons.StoreRestored, (await _h.RunAsync(SyncTrigger.Manual, r => r with { ConfirmRestore = true })).Paused);
        Assert.True(File.Exists(_h.Paths.RestoreHoldFile));

        var dry = await admin.SyncAsync(new AccessRolesSyncOptions { DryRun = true }, caller, token);
        Assert.Equal((SyncReasons.StoreRestored, "Write"), (dry.Paused, Assert.Single(dry.Users).Action));
        Assert.False(_h.Sync.RestoreNeedsDryRun());

        // The confirmation names the plan seen (the pre-1.0 review): without ifPlan it is refused, and the hold stays.
        var noPlan = await Assert.ThrowsAsync<AccessRolesException>(() => admin.SyncAsync(new AccessRolesSyncOptions { ConfirmRestore = true }, caller, token));
        Assert.Equal(AccessRolesException.IfPlanRequired, noPlan.Code);
        Assert.True(File.Exists(_h.Paths.RestoreHoldFile));
        var confirmed = await admin.SyncAsync(new AccessRolesSyncOptions { ConfirmRestore = true, IfPlan = dry.PlanToken }, caller, token);

        Assert.Null(confirmed.Paused);
        Assert.Equal(1, confirmed.Written);
        Assert.False(File.Exists(_h.Paths.RestoreHoldFile));
        Assert.False(_h.Server.Cached(kid.Id).HasPermission(PermissionKind.EnableAllFolders));
    }

    [Fact]
    public async Task ADryRunForOneUser_IsNotTheHoldsDryRun_SoTheConfirmationIsStillRefused_AndARunForOneUserWritesNothingWhileHeld()
    {
        // The per-user Revert's dry run shows the admin one user, not what the confirmed run would write.
        var kid = _h.Server.Add("kid", allFolders: true);
        await SnapshotThenLoseTheFileAsync(kid.Id);
        await _h.RunAsync(SyncTrigger.Startup);
        var admin = new AccessRolesAdminService(_h.Store, _h.Catalog, new JellyfinServerUsers(_h.Server.Manager.Object), _h.Audit, () => throw new NotSupportedException(), () => _h.Sync, NullLogger.Instance, _h.Clock);
        var caller = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", Guid.NewGuid().ToString("N")), new Claim(ClaimTypes.Role, "Administrator")], "Test"));
        var token = TestContext.Current.CancellationToken;

        var dry = await admin.SyncAsync(new AccessRolesSyncOptions { DryRun = true, UserId = kid.Id }, caller, token);
        var run = await admin.SyncAsync(new AccessRolesSyncOptions { ReapplyDrift = true, UserId = kid.Id, IfPlan = dry.PlanToken }, caller, token);
        var refused = await Assert.ThrowsAsync<AccessRolesException>(() => admin.SyncAsync(new AccessRolesSyncOptions { ConfirmRestore = true }, caller, token));

        Assert.Equal((SyncReasons.StoreRestored, SyncReasons.StoreRestored), (dry.Paused, run.Paused));
        Assert.Equal(AccessRolesException.DryRunRequired, refused.Code);
        Assert.True(_h.Sync.RestoreNeedsDryRun());
        Assert.True(File.Exists(_h.Paths.RestoreHoldFile));
        Assert.Empty(_h.Server.Writes);
    }

    [Fact]
    public async Task ConfirmingTheHold_WithAStaleIfPlan_ChangesNothingAtAll_AndTheHoldStays_ThenTheCurrentPlanLiftsIt()
    {
        // Resume says "It writes what the preview showed", so it sends the hold's dry
        // run's PlanToken; the hold was lifted (and the lifecycle saved and audited) before the token was checked.
        var kid = _h.Server.Add("kid", allFolders: true);
        await SnapshotThenLoseTheFileAsync(kid.Id);
        await _h.RunAsync(SyncTrigger.Startup);
        var admin = new AccessRolesAdminService(_h.Store, _h.Catalog, new JellyfinServerUsers(_h.Server.Manager.Object), _h.Audit, () => throw new NotSupportedException(), () => _h.Sync, NullLogger.Instance, _h.Clock);
        var caller = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", Guid.NewGuid().ToString("N")), new Claim(ClaimTypes.Role, "Administrator")], "Test"));
        var token = TestContext.Current.CancellationToken;
        var dry = await admin.SyncAsync(new AccessRolesSyncOptions { DryRun = true }, caller, token);

        // Someone else is given the role after the preview: the run would write them too.
        var viewer = _h.Server.Add("viewer", allFolders: true);
        var role = _h.Store.Current.Roles.Single().Id;
        await _h.SaveAsync(d => d with { Assignments = [.. d.Assignments, new RoleAssignment { UserId = viewer.Id, RoleIds = [role], UpdatedUtc = _h.Now }] });
        var stored = _h.Store.Current;
        var lines = _h.Lines().Count;

        var refused = await Assert.ThrowsAsync<AccessRolesException>(() => admin.SyncAsync(new AccessRolesSyncOptions { ConfirmRestore = true, IfPlan = dry.PlanToken }, caller, token));

        Assert.Equal(AccessRolesException.Changed, refused.Code);
        Assert.Equal(["viewer"], refused.Users.Select(u => u.Name));
        Assert.True(File.Exists(_h.Paths.RestoreHoldFile), "the hold stays");
        Assert.Equal(SyncReasons.StoreRestored, _h.Sync.PauseReason());
        Assert.Same(stored, _h.Store.Current);
        Assert.Empty(stored.KnownLibraries);
        Assert.Equal(lines, _h.Lines().Count);
        Assert.Empty(_h.Server.Writes);

        // The negative control: the dry run seen now, confirmed, lifts the hold and writes both.
        var now = await admin.SyncAsync(new AccessRolesSyncOptions { DryRun = true }, caller, token);
        var confirmed = await admin.SyncAsync(new AccessRolesSyncOptions { ConfirmRestore = true, IfPlan = now.PlanToken }, caller, token);

        Assert.NotEqual(dry.PlanToken, now.PlanToken);
        Assert.Null(confirmed.Paused);
        Assert.Equal(2, confirmed.Written);
        Assert.False(File.Exists(_h.Paths.RestoreHoldFile));
        Assert.NotEmpty(_h.Store.Current.KnownLibraries);
        Assert.Single(_h.Lines(AuditActions.LibrarySyncResumed));
    }

    [Fact]
    public async Task ConfirmingTheHold_AfterANativeChangeOfAUserItWrites_Is409NamingThem_AndTheHoldStays()
    {
        // For the hold's Resume: the preview showed what the run writes over the kid's access
        // then; another administrator's native change since would make it write over access the admin never saw.
        var kid = _h.Server.Add("kid", allFolders: true);
        await SnapshotThenLoseTheFileAsync(kid.Id);
        await _h.RunAsync(SyncTrigger.Startup);
        var admin = new AccessRolesAdminService(_h.Store, _h.Catalog, new JellyfinServerUsers(_h.Server.Manager.Object), _h.Audit, () => throw new NotSupportedException(), () => _h.Sync, NullLogger.Instance, _h.Clock);
        var caller = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", Guid.NewGuid().ToString("N")), new Claim(ClaimTypes.Role, "Administrator")], "Test"));
        var token = TestContext.Current.CancellationToken;
        var dry = await admin.SyncAsync(new AccessRolesSyncOptions { DryRun = true }, caller, token);
        _h.Server.AdminPost(kid.Id, p =>
        {
            p.EnableAllFolders = false;
            p.EnabledFolders = [Movies.Id];
        });
        var writes = _h.Server.Writes.Count;

        var refused = await Assert.ThrowsAsync<AccessRolesException>(() => admin.SyncAsync(new AccessRolesSyncOptions { ConfirmRestore = true, IfPlan = dry.PlanToken }, caller, token));

        Assert.Equal(AccessRolesException.Changed, refused.Code);
        Assert.Equal(["kid"], refused.Users.Select(u => u.Name));
        Assert.True(File.Exists(_h.Paths.RestoreHoldFile), "the hold stays");
        Assert.Equal(writes, _h.Server.Writes.Count);
        Assert.Equal([Movies.Id], _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));

        // Look again: the preview seen now, confirmed, lifts the hold and writes the kid.
        var now = await admin.SyncAsync(new AccessRolesSyncOptions { DryRun = true }, caller, token);
        var confirmed = await admin.SyncAsync(new AccessRolesSyncOptions { ConfirmRestore = true, IfPlan = now.PlanToken }, caller, token);

        Assert.Null(confirmed.Paused);
        Assert.Equal(1, confirmed.Written);
        Assert.Equal([Kids.Id], _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task WhileHeld_GetLibraries_ListsWhoOpensEachNatively_MarkingWhatTheHoldKeepsUnwritten()
    {
        // The hold after a restore writes nothing, so the kid still opens every library natively.
        var kid = _h.Server.Add("kid", allFolders: true);
        await SnapshotThenLoseTheFileAsync(kid.Id);
        await _h.RunAsync(SyncTrigger.Startup);
        var admin = new AccessRolesAdminService(_h.Store, _h.Catalog, new JellyfinServerUsers(_h.Server.Manager.Object), _h.Audit, () => throw new NotSupportedException(), () => _h.Sync, NullLogger.Instance, _h.Clock);

        var libraries = admin.GetLibraries();

        var kids = libraries.Single(l => l.Id.Equals(Kids.Id)).Users.Single(u => u.UserId.Equals(kid.Id));
        Assert.Equal(["Role:Kid"], kids.Sources);
        Assert.Null(kids.Mismatch);
        var movies = libraries.Single(l => l.Id.Equals(Movies.Id)).Users.Single(u => u.UserId.Equals(kid.Id));
        Assert.Empty(movies.Sources);
        Assert.Equal(SyncReasons.StoreRestored, movies.Mismatch);
    }

    [Fact]
    public async Task TheHoldsDryRun_IsNotedInItsFile_SoARestartStillKnows_AndANewHoldNeedsItsOwn()
    {
        var kid = _h.Server.Add("kid", allFolders: true);
        await SnapshotThenLoseTheFileAsync(kid.Id);
        await _h.RunAsync(SyncTrigger.Startup);
        await _h.RunAsync(SyncTrigger.Manual, r => r with { DryRun = true });
        Assert.Contains("DryRunUtc", await File.ReadAllTextAsync(_h.Paths.RestoreHoldFile, TestContext.Current.CancellationToken), StringComparison.Ordinal);

        // The next start: the store loads Ok, this process saw no dry run; the hold file says one was made.
        using var restarted = AccessRolesStore.Create(_h.Store.Documents.FilePath, NullLogger.Instance, _h.Clock);
        Assert.False(restarted.RestoreDryRunSeen);
        Assert.True(RestoreHold.DryRunSeen(_h.Paths, restarted));
        Assert.Equal(RestoreHold.ReleaseOutcome.Released, RestoreHold.Release(_h.Paths, restarted, RestoreHold.Current(_h.Paths, restarted)!));

        // A hold that begins later (its file again) needs a dry run of its own.
        await File.WriteAllTextAsync(_h.Paths.RestoreHoldFile, "{\"Reason\":\"StoreRestored\"}", TestContext.Current.CancellationToken);
        Assert.True(RestoreHold.IsHeld(_h.Paths, restarted));
        Assert.False(RestoreHold.DryRunSeen(_h.Paths, restarted));
    }

    [Fact]
    public async Task TheConfirmation_IsAudited_EvenWhenNoRunSawTheHoldBefore()
    {
        // A hold file of an earlier process, a dry run and the confirmation, and no real run in between: the confirmation
        // still writes LibrarySyncResumed, the admin as its actor (as a sandbox run showed).
        await _h.RunAsync();
        Directory.CreateDirectory(_h.Paths.Root);
        await File.WriteAllTextAsync(_h.Paths.RestoreHoldFile, "{\"Reason\":\"StoreRestored\"}", TestContext.Current.CancellationToken);
        await _h.RunAsync(SyncTrigger.Manual, r => r with { DryRun = true });
        var admin = new AuditActor(AuditActor.AdminKind, Guid.NewGuid(), "admin");

        var confirmed = await _h.RunAsync(SyncTrigger.Manual, r => r with { Actor = admin, ConfirmRestore = true });

        Assert.Null(confirmed.Paused);
        var resumed = Assert.Single(_h.Lines(AuditActions.LibrarySyncResumed));
        Assert.Equal((SyncReasons.StoreRestored, "admin"), (SyncHarness.Detail(resumed, "Reason"), resumed.Actor.Name));
    }

    [Fact]
    public async Task TheHold_OutlivesARestart_WhoseStoreLoadsOk_UntilConfirmed()
    {
        var kid = _h.Server.Add("kid", allFolders: true);
        await SnapshotThenLoseTheFileAsync(kid.Id);
        Assert.Equal(SyncReasons.StoreRestored, (await _h.RunAsync(SyncTrigger.Startup)).Paused);

        // The next start: the restored file was saved, so the store loads Ok; the hold file is what keeps the sync held.
        using var restarted = AccessRolesStore.Create(_h.Store.Documents.FilePath, NullLogger.Instance, _h.Clock);
        Assert.Equal(StoreStatus.Ok, restarted.Status);
        Assert.Single(restarted.Current.Assignments);
        Assert.Equal(SyncReasons.StoreRestored, LibrarySync.PauseReason(_h.Configuration, _h.Paths, restarted));

        Assert.Equal(RestoreHold.ReleaseOutcome.Released, RestoreHold.Release(_h.Paths, restarted, RestoreHold.Current(_h.Paths, restarted)!));
        Assert.Null(LibrarySync.PauseReason(_h.Configuration, _h.Paths, restarted));
        Assert.Null(RestoreHold.Current(_h.Paths, restarted));
    }

    [Fact]
    public async Task AMissingFile_NoneOfWhoseSnapshotsCanBeRestored_IsReadOnly_AndPausesTheSync()
    {
        // ReadOnlyDataMissing (the kit's restore at load): a snapshot from a newer schema may hold the data, so nothing is written.
        var kid = _h.Server.Add("kid", allFolders: true);
        Directory.CreateDirectory(_h.Paths.SnapshotsDirectory);
        await File.WriteAllTextAsync(Path.Combine(_h.Paths.SnapshotsDirectory, "accessroles-20261018-031000.json"), """{"Version":2,"Roles":[{"Id":"0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f","Name":"Newer"}]}""", TestContext.Current.CancellationToken);

        Assert.Equal(StoreStatus.ReadOnlyDataMissing, _h.Store.Status);
        Assert.True(_h.Store.IsReadOnly);
        var report = await _h.RunAsync();

        Assert.Equal(SyncReasons.StoreReadOnly, report.Paused);
        Assert.Empty(_h.Server.Writes);
        Assert.False(File.Exists(_h.Store.Documents.FilePath));
        Assert.False(File.Exists(_h.Paths.RestoreHoldFile));
        Assert.True(_h.Server.Cached(kid.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.Throws<Jellyfin.Plugin.Kit.Api.PluginException>(() => _h.Store.Documents.ThrowIfReadOnly());
    }

    [Theory]
    [InlineData("not json", nameof(StoreStatus.ReadOnlyDataUnreadable), SyncReasons.StoreReadOnly)]
    // A store recovered empty with no evidence of managed users locks nobody and doesn't pause the sync since 0.9.13.
    [InlineData("  ", nameof(StoreStatus.RecoveredEmpty), null)]
    [InlineData("""{"Version":2,"Roles":[]}""", nameof(StoreStatus.ReadOnlyNewerVersion), SyncReasons.StoreReadOnly)]
    [InlineData("""{"Version":1,"Roles":[]}""", nameof(StoreStatus.Ok), null)]
    public async Task TheOtherStatuses_KeepTheirMapping(string file, string status, string? paused)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_h.Store.Documents.FilePath)!);
        await File.WriteAllTextAsync(_h.Store.Documents.FilePath, file, TestContext.Current.CancellationToken);

        Assert.Equal(status, _h.Store.Status.ToString());
        Assert.Equal(paused, _h.Sync.PauseReason());
        Assert.False(File.Exists(_h.Paths.RestoreHoldFile));
    }

    public void Dispose() => _h.Dispose();

    // A store that held kid's Kid role in a daily snapshot, whose file and .bak are then gone: the kit restores the snapshot
    // when the harness's store first loads (RestoredFromSnapshot).
    private async Task SnapshotThenLoseTheFileAsync(Guid kidId)
    {
        var file = _h.Store.Documents.FilePath;
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        using (var earlier = AccessRolesStore.Create(file, NullLogger.Instance, _h.Clock))
        {
            await earlier.UpdateAsync(
                d => d with
                {
                    Roles = [role],
                    Assignments = [new RoleAssignment { UserId = kidId, RoleIds = [role.Id], UpdatedUtc = _h.Now }],
                    Ledger = new System.Collections.Generic.Dictionary<Guid, LedgerEntry> { [kidId] = new LedgerEntry { OriginalEnableAllFolders = true, OriginalEnableUserPreferenceAccess = true, CapturedUtc = _h.Now } }
                },
                TestContext.Current.CancellationToken);
            Assert.NotNull(await earlier.Documents.SnapshotAsync(_h.Paths.SnapshotsDirectory, 7, TestContext.Current.CancellationToken));
        }

        File.Delete(file);
        File.Delete(file + ".bak");
        _h.Clock.Advance(TimeSpan.FromHours(1));
    }
}
