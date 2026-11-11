using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using MediaBrowser.Model.Users;
using Moq;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Sync;

/// <summary>
/// The library sync (<c>LibrarySyncService</c>) over the real store
/// and audit log and the 10.11.5 user manager's double: managed users' first writes and rule changes, users who aren't
/// managed never written, drift re-applied or only reported, the no-libraries refusal and a deletion that empties a user,
/// re-created libraries until granted again, the lockout guard, the pauses (read-only store, SAFE-MODE, the switches), the
/// releases with matching and stale baselines, the ledger, dry runs, an aborted write, and the post-write check.
/// </summary>
public sealed class LibrarySyncTests : IDisposable
{
    private readonly SyncHarness _h = new();

    [Fact]
    public async Task AManagedUsersFirstWrite_IsTheRoleResult_OnlyTheLibraryFields_AuditedAndRecordedInTheLedger()
    {
        var kid = _h.Server.Add("kid", allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids, Movies]);
        await SaveAsync([role], [Assign(SyncHarness.Read(kid), [role])], captureLedgerFor: [kid]);
        var stored = FakeJellyfinUsers.Dump(_h.Server.Stored(kid.Id));

        var report = await _h.RunAsync(SyncTrigger.StoreChanged);

        var write = Assert.Single(report.Users);
        Assert.Equal((SyncActionKind.Write, SyncReasons.FirstWrite, PolicyWriteOutcome.Written), (write.Kind, write.Reason, write.Outcome!.Value));
        var cached = _h.Server.Cached(kid.Id);
        Assert.False(cached.HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal(new[] { Kids.Id, Movies.Id }.Order(), cached.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).Order());
        // A user no full-policy write ever saved has no bitrate limit in its row (null); GetUserDto gives 0 for it
        // (UserManager.cs:349), which the write stores, as any Dashboard save does. Both mean "no limit"
        // (MediaInfoHelper.cs:498 reads `?? 0`).
        Assert.Equal(["Permission.EnableAllFolders", "Preference.EnabledFolders", "User.RemoteClientBitrateLimit", "User.RowVersion"], FakeJellyfinUsers.Differences(stored, FakeJellyfinUsers.Dump(_h.Server.Stored(kid.Id))));
        Assert.Equal(("", "0"), (stored["User.RemoteClientBitrateLimit"], FakeJellyfinUsers.Dump(_h.Server.Stored(kid.Id))["User.RemoteClientBitrateLimit"]));
        var line = Assert.Single(_h.Lines(AuditActions.PolicySynced));
        Assert.Equal((kid.Id, "kid", AuditActor.SystemKind), (line.TargetUserId!.Value, line.TargetName, line.Actor.Kind));
        Assert.Equal(SyncReasons.FirstWrite, SyncHarness.Detail(line, "Reason"));
        Assert.Equal("StoreChanged", SyncHarness.Detail(line, "Trigger"));
        var entry = _h.Store.Current.Ledger[kid.Id];
        Assert.True(entry.OriginalEnableAllFolders);
        Assert.Equal(new[] { Kids.Id, Movies.Id }.Order(), entry.LastWrite!.EnabledFolders.Order());
        Assert.Equal(1, report.ManagedUsers);
    }

    [Fact]
    public async Task UsersWhoseLibrariesArentManaged_AreNeverWritten()
    {
        // Not managed, a lock-only role, excluded, a legacy BlockedMediaFolders list (CannotManage), a value a newer build
        // wrote (UnknownValue): none of them is written, whatever the libraries say.
        var plain = _h.Server.Add("plain", allFolders: false, folders: [Movies.Id]);
        var locked = _h.Server.Add("locked", allFolders: true);
        var excluded = _h.Server.Add("excluded", allFolders: true);
        var blocked = _h.Server.Add("blocked", allFolders: true);
        blocked.SetPreference(PreferenceKind.BlockedMediaFolders, [Horror.Id]);
        var newer = _h.Server.Add("newer", allFolders: true);
        var lockOnly = Role(1, "Locks", locks: [AccessRolesLock.Profile]);
        var kidRole = Role(2, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await SaveAsync(
            [lockOnly, kidRole],
            [
                Assign(SyncHarness.Read(locked), [lockOnly]),
                Assign(SyncHarness.Read(excluded), [kidRole], state: AccessRolesAssignmentState.Excluded),
                Assign(SyncHarness.Read(blocked), [kidRole]),
                Assign(SyncHarness.Read(newer), [kidRole]) with { State = LenientEnum<AccessRolesAssignmentState>.Parse("Paused") }
            ]);

        var report = await _h.RunAsync();

        Assert.Empty(report.Users);
        Assert.Empty(_h.Server.Writes);
        _h.Server.Manager.Verify(m => m.UpdatePolicyAsync(It.IsAny<Guid>(), It.IsAny<UserPolicy>()), Times.Never);
        Assert.Equal([Movies.Id], _h.Server.Cached(plain.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task AUserInSync_IsNotWrittenAgain_AndTheirAccessIsRecordedAsTheLastWrite()
    {
        var kid = _h.Server.Add("kid", allFolders: false, folders: [Kids.Id]);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await SaveAsync([role], [Assign(SyncHarness.Read(kid), [role])], captureLedgerFor: [kid]);

        var first = await _h.RunAsync();
        var second = await _h.RunAsync();

        Assert.Empty(first.Users);
        Assert.Empty(second.Users);
        Assert.Empty(_h.Server.Writes);
        Assert.Equal([Kids.Id], _h.Store.Current.Ledger[kid.Id].LastWrite!.EnabledFolders);
    }

    [Fact]
    public async Task Drift_WithReapply_IsWrittenAgain_AndTheLineNamesTheAccessItReplaced()
    {
        var kid = await ManagedKidAsync();
        _h.Server.AdminPost(kid.Id, p => p.EnableAllFolders = true);

        var report = await _h.RunAsync();

        var write = Assert.Single(report.Users);
        Assert.Equal((SyncActionKind.Write, SyncReasons.Drift, true), (write.Kind, write.Reason, write.Wrote));
        Assert.False(_h.Server.Cached(kid.Id).HasPermission(PermissionKind.EnableAllFolders));
        var line = _h.Lines(AuditActions.PolicySynced).Last();
        Assert.Equal(SyncReasons.Drift, SyncHarness.Detail(line, "Reason"));
        Assert.Contains("\"EnableAllFolders\":true", SyncHarness.Detail(line, "Native"), StringComparison.Ordinal);
        Assert.Equal(1, report.Drifted);
    }

    [Fact]
    public async Task Drift_WithReportOnly_IsReportedOnce_AndLeft_UntilTheAdminRevertsIt()
    {
        var kid = await ManagedKidAsync();
        _h.Configuration.DriftPolicy = "reportonly";
        _h.Server.AdminPost(kid.Id, p => p.EnabledFolders = [Movies.Id]);

        var first = await _h.RunAsync();
        var second = await _h.RunAsync();

        Assert.Equal((SyncActionKind.Drift, SyncReasons.ReportOnly), (Assert.Single(first.Users).Kind, first.Users[0].Reason));
        Assert.Equal([Movies.Id], _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Single(_h.Lines(AuditActions.DriftDetected));
        Assert.Single(second.Users);
        Assert.True(EffectiveRulesResolver.Resolve(_h.Store.Current, _h.Catalog.Read(), SyncHarness.Read(_h.Server.Cached(kid.Id))).Drift);
        Assert.Equal(1, Describe()["DriftCount"]);

        var revert = await _h.RunAsync(SyncTrigger.Manual, r => r with { ReapplyDrift = true });

        Assert.True(Assert.Single(revert.Users).Wrote);
        Assert.Equal([Kids.Id], _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Equal(0, Describe()["DriftCount"]);
    }

    [Fact]
    public async Task ARoleChange_IsWrittenAsRulesChanged_AlsoWithReportOnly()
    {
        var kid = await ManagedKidAsync();
        _h.Configuration.DriftPolicy = "ReportOnly";
        await _h.SaveAsync(d => d with { Roles = [d.Roles[0] with { Libraries = d.Roles[0].Libraries with { Refs = [.. d.Roles[0].Libraries.Refs, new LibraryReference { Id = Shows.Id }] } }] });

        var report = await _h.RunAsync(SyncTrigger.StoreChanged);

        Assert.Equal((SyncReasons.RulesChanged, true), (Assert.Single(report.Users).Reason, report.Users[0].Wrote));
        Assert.Equal(new[] { Kids.Id, Shows.Id }.Order(), _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).Order());
    }

    [Fact]
    public async Task AResultWithNoLibrary_IsRefused_WhileTheUserStillHasLibraries_AndWrittenWhenConfirmed()
    {
        var kept = _h.Server.Add("kept", allFolders: false, folders: [Movies.Id]);
        var chosen = _h.Server.Add("chosen", allFolders: true);
        var gone = Library(9, "Gone");
        var stale = Role(1, "Stale", mode: AccessRolesRoleLibraryMode.Only, refs: [gone]);
        await SaveAsync(
            [stale],
            [Assign(SyncHarness.Read(kept), [stale]), Assign(SyncHarness.Read(chosen), mode: AccessRolesUserLibraryMode.Only, confirmNoLibraries: true)],
            captureLedgerFor: [kept, chosen]);

        var report = await _h.RunAsync();

        var skip = report.Users.Single(u => u.User.Id.Equals(kept.Id));
        Assert.Equal((SyncActionKind.Skip, SyncReasons.NoLibraries), (skip.Kind, skip.Reason));
        Assert.Equal([Movies.Id], _h.Server.Cached(kept.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.True(report.Users.Single(u => u.User.Id.Equals(chosen.Id)).Wrote);
        Assert.Empty(_h.Server.Cached(chosen.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.False(_h.Server.Cached(chosen.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal(SyncReasons.NoLibraries, SyncHarness.Detail(Assert.Single(_h.Lines(AuditActions.SyncSkipped)), "Reason"));
    }

    [Fact]
    public async Task ADeletionThatEmptiesAUser_RemovesTheGoneId_AndIsAuditedAsUsersWithoutLibraries()
    {
        // A deleted library: the deleted library's id goes from the user's native access at once, so a library created
        // again under the same name can't revive the grant (as seen on 10.11.5).
        var kid = await ManagedKidAsync();
        _h.Catalog.Libraries.RemoveAll(l => l.Id.Equals(Kids.Id));
        _h.Sync.RecordRemoval(Kids.Id);

        var report = await _h.RunAsync(SyncTrigger.LibraryChanged);

        Assert.Equal(LifecycleEventKind.Removed, Assert.Single(report.Libraries).Kind);
        var write = Assert.Single(report.Users);
        Assert.Equal((SyncReasons.NoLibrariesLeft, true), (write.Reason, write.Wrote));
        Assert.Empty(_h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Single(_h.Lines(AuditActions.LibraryRemoved));
        Assert.Contains(kid.Id.ToString("N"), SyncHarness.Detail(Assert.Single(_h.Lines(AuditActions.UsersWithoutLibraries)), "Users"), StringComparison.Ordinal);
        Assert.True(_h.Store.Current.Roles[0].Libraries.Refs[0].Orphaned);
    }

    [Fact]
    public async Task ARunRefusedForAStaleIfPlan_ForgetsNoRemovalItSaw_SoTheNextRunStillKnowsTheLibraryWasDeleted()
    {
        // "Nothing written" holds for the whole store and the sync's own state too. The removals the
        // library structure filter saw were taken (and forgotten) before the plan was checked; a refused run lost them, and
        // the next run knew the deletion only from the backstop's diff (where a library with the same folders is a rename).
        var kid = await ManagedKidAsync();
        var dry = await _h.RunAsync(SyncTrigger.Manual, r => r with { DryRun = true });
        var token = SyncPlanToken.Of(dry.Users);
        _h.Catalog.Libraries.RemoveAll(l => l.Id.Equals(Kids.Id));
        _h.Sync.RecordRemoval(Kids.Id);
        var stored = _h.Store.Current;

        var refused = await Assert.ThrowsAsync<AccessRolesException>(() => _h.RunAsync(SyncTrigger.Manual, r => r with { IfPlan = token }));

        Assert.Equal(AccessRolesException.Changed, refused.Code);
        Assert.Equal(["kid"], refused.Users.Select(u => u.Name));
        Assert.Same(stored, _h.Store.Current);
        Assert.Empty(_h.Lines(AuditActions.LibraryRemoved));

        // The negative control: the plan seen now runs, and the removal is still known.
        var now = await _h.RunAsync(SyncTrigger.Manual, r => r with { DryRun = true });
        var report = await _h.RunAsync(SyncTrigger.Manual, r => r with { IfPlan = SyncPlanToken.Of(now.Users) });

        var removed = Assert.Single(report.Libraries);
        Assert.Equal((LifecycleEventKind.Removed, true), (removed.Kind, removed.Exact));
        Assert.Single(_h.Lines(AuditActions.LibraryRemoved));
        Assert.Empty(_h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task ALibraryCreatedAgain_IsTakenAwayAtTheNextSync_UntilTheAdminGrantsItAgain()
    {
        // The re-created library (a newer folder under the same id) is new; a stale grant would revive
        // it at once (as seen on 10.11.5), so the sync removes it, and "grant again" gives it back.
        var created = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        SetCreated(Kids.Id, created);
        SetCreated(Movies.Id, created);
        var kid = _h.Server.Add("kid", allFolders: false, folders: [Kids.Id, Movies.Id]);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids, Movies]);
        await SaveAsync([role], [Assign(SyncHarness.Read(kid), [role])], captureLedgerFor: [kid]);
        Assert.Empty((await _h.RunAsync()).Users);
        SetCreated(Kids.Id, created.AddDays(10));

        var report = await _h.RunAsync();

        Assert.Equal(LifecycleEventKind.Recreated, Assert.Single(report.Libraries).Kind);
        Assert.Equal((SyncReasons.RulesChanged, true), (Assert.Single(report.Users).Reason, report.Users[0].Wrote));
        Assert.Equal([Movies.Id], _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));

        await _h.SaveAsync(d => LibraryLifecycle.GrantAgain(d, _h.Catalog.Read(), Kids.Id, _h.Now)!);
        var again = await _h.RunAsync(SyncTrigger.StoreChanged);

        Assert.True(Assert.Single(again.Users).Wrote);
        Assert.Equal(new[] { Kids.Id, Movies.Id }.Order(), _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).Order());
    }

    [Fact]
    public async Task TheLockoutGuard_RefusesToNarrowTheOnlyFullAdministrator_UntilConfirmed()
    {
        var admin = _h.Server.Add("admin", admin: true, allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await SaveAsync([role], [Assign(SyncHarness.Read(admin), [role])], captureLedgerFor: [admin]);

        var refused = await _h.RunAsync();

        Assert.Equal((SyncActionKind.Skip, SyncReasons.NoFullAdministrator), (Assert.Single(refused.Users).Kind, refused.Users[0].Reason));
        Assert.True(_h.Server.Cached(admin.Id).HasPermission(PermissionKind.EnableAllFolders));

        var confirmed = await _h.RunAsync(SyncTrigger.Manual, r => r with { ConfirmNoFullAdmin = true });

        Assert.True(Assert.Single(confirmed.Users).Wrote);
        Assert.Equal([Kids.Id], _h.Server.Cached(admin.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.NotNull(_h.Store.Current.Assignments[0].ConfirmedNoFullAdminUtc);

        // The confirmation stays on record: the admin's own edit on the Dashboard is re-applied.
        _h.Server.AdminPost(admin.Id, p => p.EnableAllFolders = true);
        Assert.Equal(SyncReasons.Drift, Assert.Single((await _h.RunAsync()).Users).Reason);
        Assert.False(_h.Server.Cached(admin.Id).HasPermission(PermissionKind.EnableAllFolders));
    }

    [Fact]
    public async Task LibraryRolesApplyToAdministrators_WhileAnotherAdministratorKeepsEveryLibrary()
    {
        var narrowed = _h.Server.Add("admin2", admin: true, allFolders: true);
        _h.Server.Add("admin", admin: true, allFolders: true);
        _h.Server.Add("disabled admin", admin: true, allFolders: true, disabled: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await SaveAsync([role], [Assign(SyncHarness.Read(narrowed), [role])], captureLedgerFor: [narrowed]);

        var report = await _h.RunAsync();

        Assert.True(Assert.Single(report.Users).Wrote);
        Assert.Equal([Kids.Id], _h.Server.Cached(narrowed.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task AReadOnlyStore_PausesTheSync_NothingWrittenOrSaved()
    {
        // A file a newer version wrote: the store is read-only, so the sync can't record what it writes, and the plugin
        // leaves library access as last synced.
        var file = _h.Store.Documents.FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, """{"Version":2,"Roles":[]}""", TestContext.Current.CancellationToken);
        var user = _h.Server.Add("kid", allFolders: true);

        var report = await _h.RunAsync();

        Assert.Equal(SyncReasons.StoreReadOnly, report.Paused);
        Assert.Empty(_h.Server.Writes);
        Assert.Equal("""{"Version":2,"Roles":[]}""", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        Assert.Equal(SyncReasons.StoreReadOnly, SyncHarness.Detail(Assert.Single(_h.Lines(AuditActions.LibrarySyncPaused)), "Reason"));
        Assert.True(_h.Server.Cached(user.Id).HasPermission(PermissionKind.EnableAllFolders));
    }

    [Fact]
    public async Task SafeMode_AndTheTwoSwitches_PauseTheSync_AndAResumeIsAudited()
    {
        var kid = _h.Server.Add("kid", allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await SaveAsync([role], [Assign(SyncHarness.Read(kid), [role])], captureLedgerFor: [kid]);
        await File.WriteAllTextAsync(_h.Paths.SafeModeFile, string.Empty, TestContext.Current.CancellationToken);

        var safe = await _h.RunAsync();
        File.Delete(_h.Paths.SafeModeFile);
        _h.Configuration.EnforcementEnabled = false;
        var enforcementOff = await _h.RunAsync();
        _h.Configuration.EnforcementEnabled = true;
        _h.Configuration.LibrarySyncEnabled = false;
        var syncOff = await _h.RunAsync();
        Assert.Empty(_h.Server.Writes);
        _h.Configuration.LibrarySyncEnabled = true;
        var resumed = await _h.RunAsync();

        Assert.Equal((SyncReasons.SafeMode, SyncReasons.EnforcementDisabled, SyncReasons.LibrarySyncDisabled, (string?)null), (safe.Paused, enforcementOff.Paused, syncOff.Paused, resumed.Paused));
        Assert.Equal(SyncActionKind.Write, Assert.Single(safe.Users).Kind);
        Assert.Null(safe.Users[0].Outcome);
        Assert.True(Assert.Single(resumed.Users).Wrote);
        Assert.Equal(3, _h.Lines(AuditActions.LibrarySyncPaused).Count);
        Assert.Equal(SyncReasons.LibrarySyncDisabled, SyncHarness.Detail(Assert.Single(_h.Lines(AuditActions.LibrarySyncResumed)), "Reason"));
    }

    [Fact]
    public async Task AReleaseWhoseBaselineHolds_IsWritten_FilteredAgainstTheLibrariesNow_AndTheEntryGoes()
    {
        var solo = _h.Server.Add("solo", allFolders: false, folders: [Kids.Id]);
        var entry = Ledger(false, Movies, Shows, Library(9, "Gone")) with
        {
            Release = new LedgerRelease { Choice = AccessRolesUnmanageChoice.Restore, RequestedUtc = _h.Now, EnabledFolders = [Movies.Id, Shows.Id], Baseline = new LedgerAccess { EnabledFolders = [Kids.Id] } }
        };
        await _h.SaveAsync(d => d with { Ledger = new Dictionary<Guid, LedgerEntry> { [solo.Id] = entry } });

        var report = await _h.RunAsync();

        var release = Assert.Single(report.Users);
        Assert.Equal((SyncActionKind.Release, "Restore", true), (release.Kind, release.Reason, release.Wrote));
        Assert.Equal(new[] { Movies.Id, Shows.Id }.Order(), _h.Server.Cached(solo.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).Order());
        Assert.Empty(_h.Store.Current.Ledger);
        var line = Assert.Single(_h.Lines(AuditActions.LibraryAccessRestored));
        Assert.Equal(("Restore", "true"), (SyncHarness.Detail(line, "Choice"), SyncHarness.Detail(line, "PolicyWritten")));
    }

    [Fact]
    public async Task AReleaseWhoseBaselineChanged_OrThatHasNone_IsDropped_WithoutAWrite()
    {
        // Storage/LedgerRelease's contract: an admin's native edit of the now unmanaged user is never overwritten.
        var edited = _h.Server.Add("edited", allFolders: false, folders: [Horror.Id]);
        var old = _h.Server.Add("old", allFolders: false, folders: [Kids.Id]);
        LedgerEntry Pending(LedgerAccess? baseline) => Ledger(false, Movies) with
        {
            Release = new LedgerRelease { Choice = AccessRolesUnmanageChoice.Restore, RequestedUtc = _h.Now, EnabledFolders = [Movies.Id], Baseline = baseline }
        };
        await _h.SaveAsync(d => d with
        {
            Ledger = new Dictionary<Guid, LedgerEntry>
            {
                [edited.Id] = Pending(new LedgerAccess { EnabledFolders = [Kids.Id] }),
                [old.Id] = Pending(null)
            }
        });

        var report = await _h.RunAsync();

        Assert.Equal(
            [(SyncActionKind.DropRelease, SyncReasons.BaselineChanged), (SyncActionKind.DropRelease, SyncReasons.NoBaseline)],
            report.Users.OrderBy(u => u.User.Name, StringComparer.Ordinal).Select(u => (u.Kind, u.Reason)));
        Assert.Empty(_h.Server.Writes);
        Assert.Equal([Horror.Id], _h.Server.Cached(edited.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Empty(_h.Store.Current.Ledger);
        Assert.Equal(2, _h.Lines(AuditActions.ReleaseDropped).Count);
        Assert.Contains("\"EnabledFolders\":[\"" + Kids.Id.ToString("N") + "\"]", SyncHarness.Detail(_h.Lines(AuditActions.ReleaseDropped).Single(l => l.TargetUserId == edited.Id), "Baseline"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReleaseAlreadyApplied_IsDropped_AGrantAllIsWritten_AndAnExcludedUserKeepsTheirEntry()
    {
        var applied = _h.Server.Add("applied", allFolders: false, folders: [Movies.Id]);
        var all = _h.Server.Add("all", allFolders: false, folders: [Kids.Id]);
        var paused = _h.Server.Add("paused", allFolders: false, folders: [Kids.Id]);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        LedgerEntry Pending(AccessRolesUnmanageChoice choice, Guid baseline) => Ledger(false, Movies) with
        {
            Release = new LedgerRelease { Choice = choice, RequestedUtc = _h.Now, EnableAllFolders = choice == AccessRolesUnmanageChoice.GrantAll, EnabledFolders = [Movies.Id], Baseline = new LedgerAccess { EnabledFolders = [baseline] } }
        };
        await _h.SaveAsync(d => d with
        {
            Roles = [role],
            Assignments = [Assign(SyncHarness.Read(paused), [role], state: AccessRolesAssignmentState.Excluded)],
            Ledger = new Dictionary<Guid, LedgerEntry>
            {
                [applied.Id] = Pending(AccessRolesUnmanageChoice.Restore, Kids.Id),
                [all.Id] = Pending(AccessRolesUnmanageChoice.GrantAll, Kids.Id),
                [paused.Id] = Pending(AccessRolesUnmanageChoice.Restore, Kids.Id)
            }
        });

        var report = await _h.RunAsync();

        Assert.Equal((SyncActionKind.DropRelease, SyncReasons.AlreadyApplied), (report.Users.Single(u => u.User.Id.Equals(applied.Id)).Kind, report.Users.Single(u => u.User.Id.Equals(applied.Id)).Reason));
        Assert.True(report.Users.Single(u => u.User.Id.Equals(all.Id)).Wrote);
        Assert.True(_h.Server.Cached(all.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.True(report.Users.Single(u => u.User.Id.Equals(paused.Id)).Wrote);
        Assert.Equal([paused.Id], _h.Store.Current.Ledger.Keys);
        Assert.Null(_h.Store.Current.Ledger[paused.Id].Release);
        Assert.Equal(3, _h.Lines(AuditActions.LibraryAccessRestored).Count);
    }

    [Fact]
    public async Task AManagedUserWithoutALedgerEntry_GetsTheirOriginalCaptured_BeforeTheFirstWrite()
    {
        var kid = _h.Server.Add("kid", allFolders: false, folders: [Shows.Id, Horror.Id]);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await SaveAsync([role], [Assign(SyncHarness.Read(kid), [role])]);
        Assert.Empty(_h.Store.Current.Ledger);

        await _h.RunAsync();

        var entry = _h.Store.Current.Ledger[kid.Id];
        Assert.Equal((false, true), (entry.OriginalEnableAllFolders, entry.OriginalEnableUserPreferenceAccess));
        Assert.Equal(new[] { Horror.Id, Shows.Id }.Order(), entry.OriginalEnabledFolders.Order());
        Assert.Equal([Kids.Id], entry.LastWrite!.EnabledFolders);
    }

    [Fact]
    public async Task ADryRun_ReportsTheWrites_AndWritesOrSavesNothing()
    {
        var kid = _h.Server.Add("kid", allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await SaveAsync([role], [Assign(SyncHarness.Read(kid), [role])], captureLedgerFor: [kid]);
        var before = _h.Store.Current;

        var report = await _h.RunAsync(SyncTrigger.Manual, r => r with { DryRun = true });

        Assert.True(report.DryRun);
        Assert.Equal((SyncActionKind.Write, (PolicyWriteOutcome?)null), (Assert.Single(report.Users).Kind, report.Users[0].Outcome));
        Assert.Empty(_h.Server.Writes);
        Assert.Same(before, _h.Store.Current);
        Assert.Empty(_h.Lines());
    }

    [Fact]
    public async Task AConcurrentAdminEdit_AbortsTheWrite_AuditedWriteAborted_AndTheAdminsEditStays()
    {
        var kid = _h.Server.Add("kid", allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await SaveAsync([role], [Assign(SyncHarness.Read(kid), [role])], captureLedgerFor: [kid]);
        var calls = _h.Server.GetCalls;
        _h.Server.BeforeGetUserById = (id, call) =>
        {
            if (call == calls + 2)
            {
                _h.Server.AdminPost(id, p => p.IsDisabled = true);
            }
            else if (call == calls + 4)
            {
                _h.Server.AdminPost(id, p => p.MaxActiveSessions = 4);
            }
        };

        var report = await _h.RunAsync();

        var skip = Assert.Single(report.Users);
        Assert.Equal((SyncActionKind.Skip, SyncReasons.WriteAborted, PolicyWriteOutcome.Aborted), (skip.Kind, skip.Reason, skip.Outcome!.Value));
        Assert.Equal(["MaxActiveSessions"], skip.Differences);
        Assert.True(_h.Server.Cached(kid.Id).HasPermission(PermissionKind.IsDisabled));
        Assert.True(_h.Server.Cached(kid.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.Contains("MaxActiveSessions", SyncHarness.Detail(Assert.Single(_h.Lines(AuditActions.WriteAborted)), "ChangedFields"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedSave_ThatTheCacheAlreadyShows_IsRewrittenForced_AtTheNextRun()
    {
        // Seen in a sandbox run: our save lost to a sign-in's; 10.11.5's cache already showed the access, the database didn't
        // hold it, and the writer's own forced retry failed too. The next run writes it again although the cache "agrees".
        var kid = _h.Server.Add("kid", allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await SaveAsync([role], [Assign(SyncHarness.Read(kid), [role])], captureLedgerFor: [kid]);
        _h.Server.FailingSaves = 2;
        var retries = 0;
        _h.Sync.RetryWanted += (_, _) => retries++;

        var failed = await _h.RunAsync();

        Assert.Equal((SyncActionKind.Skip, SyncReasons.WriteFailed), (Assert.Single(failed.Users).Kind, failed.Users[0].Reason));
        Assert.Equal([Kids.Id], _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.True(_h.Server.Stored(kid.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal(1, retries);

        var rewritten = await _h.RunAsync(SyncTrigger.Retry);

        var write = Assert.Single(rewritten.Users);
        Assert.Equal((SyncReasons.RewriteAfterFailedSave, true, true), (write.Reason, write.Force, write.Wrote));
        Assert.False(_h.Server.Stored(kid.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal([Kids.Id], _h.Server.Stored(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Empty((await _h.RunAsync()).Users);
        Assert.Equal(1, retries);
    }

    [Fact]
    public async Task ThePostWriteCheck_WritesAgainAfterTheSignInRacesRevert_OnceOnly()
    {
        // Verify after writing (measured on 10.11.5): about 5 s after the write the cache holds the old user again; the
        // check writes again through the same path and audits PolicySynced with RevertedAfterWrite; a second revert is
        // left to the next reconcile.
        var kid = await ManagedKidAsync();
        _h.Server.SignInRace(kid.Id);
        Assert.True(_h.Server.Cached(kid.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.False(await _h.Sync.VerifyDueAsync(TestContext.Current.CancellationToken));
        _h.Clock.Advance(PolicyWriter.VerifyDelay);

        var reconcile = await _h.Sync.VerifyDueAsync(TestContext.Current.CancellationToken);

        Assert.False(reconcile);
        Assert.False(_h.Server.Cached(kid.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal([Kids.Id], _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        var line = _h.Lines(AuditActions.PolicySynced).Last();
        Assert.Equal(SyncReasons.RevertedAfterWrite, SyncHarness.Detail(line, "Reason"));
        Assert.Equal(1, ((IDictionary<string, object?>)Describe()["Totals"]!)["RevertedAfterWrite"]);

        _h.Server.SignInRace(kid.Id);
        _h.Clock.Advance(PolicyWriter.VerifyDelay);
        var writes = _h.Server.Writes.Count;

        Assert.True(await _h.Sync.VerifyDueAsync(TestContext.Current.CancellationToken));
        Assert.Equal(writes, _h.Server.Writes.Count);
        Assert.Equal(1, ((IDictionary<string, object?>)Describe()["Totals"]!)["RevertedAgain"]);
        Assert.Equal(SyncReasons.Drift, Assert.Single((await _h.RunAsync()).Users).Reason);
    }

    [Fact]
    public async Task ARenameTheFilterSaw_IsRePointed_AndTheNewIdWritten_OnlyOnceTheNewLibraryHasItsItem()
    {
        var kid = await ManagedKidAsync();
        var renamed = Library(7, "Kids Movies") with { Locations = Kids.Locations };
        await _h.Sync.RecordRenameAsync(Kids.Id, "Kids", "Kids Movies", TestContext.Current.CancellationToken);
        _h.Catalog.Libraries.RemoveAll(l => l.Id.Equals(Kids.Id));
        _h.Catalog.Libraries.Add(renamed with { HasItem = false });

        var waiting = await _h.RunAsync(SyncTrigger.LibraryChanged);

        Assert.Empty(waiting.Users);
        Assert.Equal([Kids.Id], waiting.WaitingLibraries);
        Assert.Equal([Kids.Id], _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));

        _h.Catalog.Libraries.RemoveAll(l => l.Id.Equals(renamed.Id));
        _h.Catalog.Libraries.Add(renamed);
        var done = await _h.RunAsync(SyncTrigger.LibraryChanged);

        Assert.Equal(LifecycleEventKind.Renamed, Assert.Single(done.Libraries).Kind);
        Assert.True(Assert.Single(done.Users).Wrote);
        Assert.Equal([renamed.Id], _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Equal(renamed.Id, _h.Store.Current.Roles[0].Libraries.Refs[0].Id);
        Assert.Single(_h.Lines(AuditActions.LibraryRenamed));
    }

    [Fact]
    public async Task ASkip_IsAuditedOnce_PerSituation()
    {
        var kept = _h.Server.Add("kept", allFolders: false, folders: [Movies.Id]);
        var stale = Role(1, "Stale", mode: AccessRolesRoleLibraryMode.Only, refs: [Library(9, "Gone")]);
        await SaveAsync([stale], [Assign(SyncHarness.Read(kept), [stale])], captureLedgerFor: [kept]);

        await _h.RunAsync();
        await _h.RunAsync();
        _h.Server.AdminPost(kept.Id, p => p.EnabledFolders = [Movies.Id, Shows.Id]);
        await _h.RunAsync();

        Assert.Equal(2, _h.Lines(AuditActions.SyncSkipped).Count);
    }

    [Fact]
    public async Task AnAdminsSaveThatWinsTheRace_KeepsItsFields_AndTheWriteAgainIsAuditedWithTheFailedSave()
    {
        // Through the sync: POST Sync racing a Dashboard policy save; the admin's commit lands
        // while ours is on its way, ours fails, the cache holds our stale entity. The write again comes from the committed row.
        var kid = _h.Server.Add("kid", allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await SaveAsync([role], [Assign(SyncHarness.Read(kid), [role])], captureLedgerFor: [kid]);
        var page = _h.Server.Load(kid.Id);
        var calls = 0;
        _h.Server.DuringSave = id =>
        {
            if (calls++ == 0)
            {
                _h.Server.AdminCommit(id, page, p =>
                {
                    p.IsDisabled = true;
                    p.MaxActiveSessions = 4;
                });
            }
        };

        var report = await _h.RunAsync(SyncTrigger.Manual);

        var write = Assert.Single(report.Users);
        Assert.Equal((SyncActionKind.Write, SyncReasons.FirstWrite, PolicyWriteOutcome.Written, true), (write.Kind, write.Reason, write.Outcome!.Value, write.Write!.RetriedAfterFailedSave));
        foreach (var user in new[] { _h.Server.Stored(kid.Id), _h.Server.Cached(kid.Id) })
        {
            Assert.True(user.HasPermission(PermissionKind.IsDisabled));
            Assert.Equal(4, user.MaxActiveSessions);
            Assert.False(user.HasPermission(PermissionKind.EnableAllFolders));
            Assert.Equal([Kids.Id], user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        }

        var line = Assert.Single(_h.Lines(AuditActions.PolicySynced));
        Assert.Equal(("true", "true"), (SyncHarness.Detail(line, "SaveFailed"), SyncHarness.Detail(line, "RetriedAfterFailedSave")));
        Assert.Contains("IsDisabled", SyncHarness.Detail(line, "CommittedFields"), StringComparison.Ordinal);
        Assert.Contains("MaxActiveSessions", SyncHarness.Detail(line, "CommittedFields"), StringComparison.Ordinal);
        Assert.Empty(_h.Writer.FailedSaves);
    }

    [Fact]
    public async Task TheNextRunsRewriteAfterAFailedSave_KeepsAnAdminsSaveCommittedMeanwhile()
    {
        // Both saves of the run fail (the admin's commit, then a sign-in's); between the runs the admin saves again. The
        // next run's forced write (RewriteAfterFailedSave) takes the committed fields, not the cache's.
        var kid = _h.Server.Add("kid", allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await SaveAsync([role], [Assign(SyncHarness.Read(kid), [role])], captureLedgerFor: [kid]);
        var page = _h.Server.Load(kid.Id);
        var calls = 0;
        _h.Server.DuringSave = id =>
        {
            if (calls++ == 0)
            {
                _h.Server.AdminCommit(id, page, p => p.IsDisabled = true);
            }
            else if (calls == 2)
            {
                _h.Server.FailingSaves = 1;
            }
        };

        var failed = await _h.RunAsync();

        Assert.Equal((SyncActionKind.Skip, SyncReasons.WriteFailed), (Assert.Single(failed.Users).Kind, failed.Users[0].Reason));
        Assert.True(_h.Writer.HasFailedSave(kid.Id));
        _h.Server.DuringSave = null;
        _h.Server.AdminCommit(kid.Id, page, p =>
        {
            p.IsDisabled = true;
            p.MaxActiveSessions = 6;
        });

        var rewritten = await _h.RunAsync(SyncTrigger.Retry);

        var write = Assert.Single(rewritten.Users);
        Assert.Equal((SyncReasons.RewriteAfterFailedSave, true), (write.Reason, write.Wrote));
        Assert.Contains("MaxActiveSessions", write.Write!.CommittedFields);
        foreach (var user in new[] { _h.Server.Stored(kid.Id), _h.Server.Cached(kid.Id) })
        {
            Assert.True(user.HasPermission(PermissionKind.IsDisabled));
            Assert.Equal(6, user.MaxActiveSessions);
            Assert.Equal([Kids.Id], user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhileARenameWaits_AnAllExceptRoleAndARemove_NeverGrantTheRenamedLibrary(bool refreshLibrary)
    {
        // Role AllExcept [Kids] and a user's Remove [Kids]; Kids is renamed. Until the rename is
        // re-pointed the new name's library is withheld from the rules' catalog, so neither user gets its new id, also when
        // its item appears while the sync is paused (refreshLibrary=false, then a scan).
        var b = _h.Server.Add("b", allFolders: true);
        var c = _h.Server.Add("c", allFolders: true);
        var allBut = Role(1, "AllBut", mode: AccessRolesRoleLibraryMode.AllExcept, refs: [Kids]);
        var all = Role(2, "All", mode: AccessRolesRoleLibraryMode.All);
        await SaveAsync([allBut, all], [Assign(SyncHarness.Read(b), [allBut]), Assign(SyncHarness.Read(c), [all], remove: [Kids])], captureLedgerFor: [b, c]);
        Assert.Equal(2, (await _h.RunAsync()).Written);
        var renamed = Library(7, "Secret2") with { Locations = Kids.Locations };
        await _h.Sync.RecordRenameAsync(Kids.Id, "Kids", "Secret2", TestContext.Current.CancellationToken);
        _h.Catalog.Libraries.RemoveAll(l => l.Id.Equals(Kids.Id));
        _h.Catalog.Libraries.Add(renamed with { HasItem = false });

        var waiting = await _h.RunAsync(SyncTrigger.LibraryChanged);

        Assert.Equal([renamed.Id], waiting.WithheldLibraries);
        Assert.Equal([Kids.Id], waiting.WaitingLibraries);
        Assert.Empty(waiting.Users);
        AssertNotGranted();
        _h.Catalog.Libraries.RemoveAll(l => l.Id.Equals(renamed.Id));
        _h.Catalog.Libraries.Add(renamed);
        if (!refreshLibrary)
        {
            // The scan gives the new library its item while the sync is paused: nothing runs, nobody has its id.
            _h.Configuration.LibrarySyncEnabled = false;
            Assert.Equal(SyncReasons.LibrarySyncDisabled, (await _h.RunAsync(SyncTrigger.LibraryChanged)).Paused);
            AssertNotGranted();
            _h.Configuration.LibrarySyncEnabled = true;
        }

        var done = await _h.RunAsync(SyncTrigger.LibraryChanged);

        Assert.Equal(LifecycleEventKind.Renamed, Assert.Single(done.Libraries).Kind);
        Assert.Empty(done.WithheldLibraries);
        AssertNotGranted();
        Assert.Equal(renamed.Id, _h.Store.Current.Roles.Single(r => r.Id.Equals(allBut.Id)).Libraries.Refs[0].Id);
        Assert.Equal([renamed.Id], _h.Store.Current.Assignments.Single(a => a.UserId.Equals(c.Id)).Libraries.Remove);

        void AssertNotGranted()
        {
            foreach (var user in new[] { b, c })
            {
                Assert.False(_h.Server.Cached(user.Id).HasPermission(PermissionKind.EnableAllFolders));
                Assert.DoesNotContain(renamed.Id, _h.Server.Cached(user.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
                Assert.DoesNotContain(renamed.Id, _h.Server.Stored(user.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
            }
        }
    }

    [Fact]
    public async Task ReportOnly_KeepsTheAdminsAdd_ButTheIdOfADeletedLibraryGoes_AndTheLibraryCreatedAgainIsntGranted()
    {
        // ReportOnly keeps the admin's edit, but the deletion and re-creation rules
        // still reach the drifted user.
        var created = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        SetCreated(Kids.Id, created);
        SetCreated(Movies.Id, created);
        var kid = _h.Server.Add("kid", allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids, Movies]);
        await SaveAsync([role], [Assign(SyncHarness.Read(kid), [role])], captureLedgerFor: [kid]);
        Assert.True(Assert.Single((await _h.RunAsync()).Users).Wrote);
        _h.Configuration.DriftPolicy = "ReportOnly";
        _h.Server.AdminPost(kid.Id, p => p.EnabledFolders = [Kids.Id, Movies.Id, Shows.Id]);

        var drift = Assert.Single((await _h.RunAsync()).Users);

        Assert.Equal((SyncActionKind.Drift, (PolicyWriteOutcome?)null), (drift.Kind, drift.Outcome));
        Assert.Null(drift.DriftDecision!.Write);
        Assert.Equal([Shows.Id], drift.DriftDecision.KeptAdded);

        // Movies is deleted (the filter saw it): its id leaves the drifted user's access; the admin's Shows stays.
        _h.Catalog.Libraries.RemoveAll(l => l.Id.Equals(Movies.Id));
        _h.Sync.RecordRemoval(Movies.Id);
        var deleted = Assert.Single((await _h.RunAsync(SyncTrigger.LibraryChanged)).Users);

        Assert.Equal((SyncActionKind.Drift, SyncReasons.ReportOnly, true), (deleted.Kind, deleted.Reason, deleted.Wrote));
        Assert.Equal([Movies.Id], deleted.DriftDecision!.Deleted);
        Assert.Equal(new[] { Kids.Id, Shows.Id }.Order(), _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).Order());
        Assert.Equal(new[] { Kids.Id, Movies.Id }.Order(), _h.Store.Current.Ledger[kid.Id].LastWrite!.EnabledFolders.Order());
        var line = _h.Lines(AuditActions.DriftDetected).Last();
        Assert.Contains(Movies.Id.ToString("N"), SyncHarness.Detail(line, "Applied"), StringComparison.Ordinal);
        Assert.Contains(Shows.Id.ToString("N"), SyncHarness.Detail(line, "Kept"), StringComparison.Ordinal);

        // Movies is created again under the same name (the same id): a new library, not visible to the drifted user.
        _h.Catalog.Libraries.Add(Movies with { CreatedUtc = created.AddDays(10) });
        var again = await _h.RunAsync(SyncTrigger.LibraryChanged);

        Assert.Equal(LifecycleEventKind.Recreated, Assert.Single(again.Libraries).Kind);
        Assert.DoesNotContain(Movies.Id, _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Equal(2, _h.Lines(AuditActions.DriftDetected).Count);
    }

    [Fact]
    public async Task ReportOnly_StripsALibraryCreatedAgainThatNothingGrants_AndOneTheRulesNoLongerGive_ButReportsAnAddition()
    {
        var created = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        SetCreated(Kids.Id, created);
        SetCreated(Movies.Id, created);
        SetCreated(Horror.Id, created);
        SetCreated(Collections.Id, created);
        var kid = _h.Server.Add("kid", allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids, Movies, Collections]);
        await SaveAsync([role], [Assign(SyncHarness.Read(kid), [role])], captureLedgerFor: [kid]);
        Assert.True(Assert.Single((await _h.RunAsync()).Users).Wrote);
        _h.Configuration.DriftPolicy = "ReportOnly";
        _h.Server.AdminPost(kid.Id, p => p.EnabledFolders = [Kids.Id, Movies.Id, Collections.Id, Shows.Id]);

        // The role now gives Kids and Horror (Collections no longer); Movies was deleted and created again unseen.
        await _h.SaveAsync(d => d with { Roles = [d.Roles[0] with { Libraries = d.Roles[0].Libraries with { Refs = [new LibraryReference { Id = Kids.Id }, new LibraryReference { Id = Movies.Id }, new LibraryReference { Id = Horror.Id }] } }] });
        SetCreated(Movies.Id, created.AddDays(10));
        var report = await _h.RunAsync();

        var drift = Assert.Single(report.Users);
        Assert.Equal((SyncActionKind.Drift, true), (drift.Kind, drift.Wrote));
        Assert.Equal([Movies.Id], drift.DriftDecision!.Recreated);
        Assert.Equal([Collections.Id], drift.DriftDecision.NoLongerGranted);
        Assert.Equal([Horror.Id], drift.DriftDecision.NotApplied);
        Assert.Equal([Shows.Id], drift.DriftDecision.KeptAdded);
        Assert.Equal(new[] { Kids.Id, Shows.Id }.Order(), _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).Order());

        // The same drift next run: nothing more to apply, and no second line.
        var lines = _h.Lines(AuditActions.DriftDetected).Count;
        var next = Assert.Single((await _h.RunAsync()).Users);
        Assert.Equal((SyncActionKind.Drift, false), (next.Kind, next.Wrote));
        Assert.Equal(lines, _h.Lines(AuditActions.DriftDetected).Count);
    }

    [Fact]
    public async Task ReportOnly_HoldsBackARemovalThatWouldLeaveTheUserNoLibrary()
    {
        var kid = _h.Server.Add("kid", allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids, Movies]);
        await SaveAsync([role], [Assign(SyncHarness.Read(kid), [role])], captureLedgerFor: [kid]);
        Assert.True(Assert.Single((await _h.RunAsync()).Users).Wrote);
        _h.Configuration.DriftPolicy = "ReportOnly";
        _h.Server.AdminPost(kid.Id, p => p.EnabledFolders = [Movies.Id]);
        await _h.SaveAsync(d => d with { Roles = [d.Roles[0] with { Libraries = d.Roles[0].Libraries with { Refs = [new LibraryReference { Id = Kids.Id }] } }] });

        var drift = Assert.Single((await _h.RunAsync()).Users);

        Assert.Equal((SyncActionKind.Drift, false), (drift.Kind, drift.Wrote));
        Assert.Equal([Movies.Id], drift.DriftDecision!.HeldBack);
        Assert.Equal([Kids.Id], drift.DriftDecision.KeptRemoved);
        Assert.Equal([Movies.Id], _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Theory]
    [InlineData("Reapply")]
    [InlineData("ReportOnly")]
    public async Task AUserManagedAgainAfterAPause_IsAFirstWrite_NotDrift(string policy)
    {
        // Excluded with Keep, the native access edited while unmanaged, managed again: the
        // last write from before the pause says nothing any more (ChangeAnalysis.KeepLedger clears it both ways).
        var kid = await ManagedKidAsync();
        _h.Configuration.DriftPolicy = policy;
        await SaveThroughRolesApiAsync(a => a with { State = AccessRolesAssignmentState.Excluded }, AccessRolesUnmanageChoice.Keep);
        Assert.Null(_h.Store.Current.Ledger[kid.Id].LastWrite);
        _h.Server.AdminPost(kid.Id, p => p.EnabledFolders = [Movies.Id, Shows.Id, Horror.Id]);
        Assert.Empty((await _h.RunAsync()).Users);

        await SaveThroughRolesApiAsync(a => a with { State = AccessRolesAssignmentState.Managed }, null);
        var dry = Assert.Single((await _h.RunAsync(SyncTrigger.Manual, r => r with { DryRun = true })).Users);
        var report = await _h.RunAsync(SyncTrigger.StoreChanged);

        Assert.Equal((SyncActionKind.Write, SyncReasons.FirstWrite), (dry.Kind, dry.Reason));
        Assert.Equal((SyncActionKind.Write, SyncReasons.FirstWrite, true), (Assert.Single(report.Users).Kind, report.Users[0].Reason, report.Users[0].Wrote));
        Assert.Equal([Kids.Id], _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Empty(_h.Lines(AuditActions.DriftDetected));
        Assert.Equal([Kids.Id], _h.Store.Current.Ledger[kid.Id].LastWrite!.EnabledFolders);
    }

    [Fact]
    public async Task ThePostWriteCheck_DoesntWriteOverAUserWhoLeftManagement_NorAnAdminsEditBackToTheOldAccess()
    {
        // Unmanage with Keep right after the write, then an admin's native edit back to the
        // access from before the write. At the check the server shows the old access, and so does the database: an edit,
        // not the sign-in race, so nothing is written.
        var kid = await ManagedKidAsync();
        await _h.SaveAsync(d => d with { Assignments = [], Ledger = new Dictionary<Guid, LedgerEntry>() });
        _h.Server.AdminPost(kid.Id, p => p.EnableAllFolders = true);
        var writes = _h.Server.Writes.Count;
        _h.Clock.Advance(PolicyWriter.VerifyDelay);

        Assert.True(await _h.Sync.VerifyDueAsync(TestContext.Current.CancellationToken));

        Assert.Equal(writes, _h.Server.Writes.Count);
        Assert.True(_h.Server.Cached(kid.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.DoesNotContain(_h.Lines(AuditActions.PolicySynced), l => SyncHarness.Detail(l, "Reason") == SyncReasons.RevertedAfterWrite);
    }

    [Fact]
    public async Task ThePostWriteCheck_HealsTheRaceForAUserWhoLeftManagementWithKeep_FromTheDatabase()
    {
        // The race after the user left management with Keep: the database holds the write Keep froze; the cache the old
        // object. Writing the committed row back can't overwrite anything saved.
        var kid = await ManagedKidAsync();
        await _h.SaveAsync(d => d with { Assignments = [], Ledger = new Dictionary<Guid, LedgerEntry>() });
        _h.Server.SignInRace(kid.Id);
        _h.Clock.Advance(PolicyWriter.VerifyDelay);

        Assert.False(await _h.Sync.VerifyDueAsync(TestContext.Current.CancellationToken));

        Assert.Equal([Kids.Id], _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        var line = _h.Lines(AuditActions.PolicySynced).Last();
        Assert.Equal((SyncReasons.RevertedAfterWrite, "true", "Resynced"), (SyncHarness.Detail(line, "Reason"), SyncHarness.Detail(line, "FromDatabase"), SyncHarness.Detail(line, "Outcome")));
    }

    [Fact]
    public async Task ThePostWriteCheck_WithoutTheCommittedRow_WritesAgainOnlyWhileTheRulesStillGiveWhatWasWritten()
    {
        var kid = await ManagedKidAsync();
        _h.Server.CommittedUnavailable = true;
        _h.Server.SignInRace(kid.Id);
        await _h.SaveAsync(d => d with { Roles = [d.Roles[0] with { Libraries = d.Roles[0].Libraries with { Refs = [new LibraryReference { Id = Movies.Id }] } }] });
        var writes = _h.Server.Writes.Count;
        _h.Clock.Advance(PolicyWriter.VerifyDelay);

        Assert.True(await _h.Sync.VerifyDueAsync(TestContext.Current.CancellationToken));
        Assert.Equal(writes, _h.Server.Writes.Count);

        // The rules give what was written: the write is made again (the cache's old object, written from the cache).
        await _h.SaveAsync(d => d with { Roles = [d.Roles[0] with { Libraries = d.Roles[0].Libraries with { Refs = [new LibraryReference { Id = Kids.Id }] } }] });
        var other = await ManagedUserAsync("other");
        _h.Server.SignInRace(other.Id);
        _h.Clock.Advance(PolicyWriter.VerifyDelay);

        Assert.False(await _h.Sync.VerifyDueAsync(TestContext.Current.CancellationToken));
        Assert.Equal([Kids.Id], _h.Server.Cached(other.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task ANativeEditBetweenTheRunsReadAndItsWrite_IsntWrittenOver_ItIsReportedAsDrift()
    {
        // The writer checks under the user's lock that the native access still is the one the
        // run planned against (here: ReportOnly's "not drifted, the rules changed").
        var kid = await ManagedKidAsync();
        _h.Configuration.DriftPolicy = "ReportOnly";
        await _h.SaveAsync(d => d with { Roles = [d.Roles[0] with { Libraries = d.Roles[0].Libraries with { Refs = [.. d.Roles[0].Libraries.Refs, new LibraryReference { Id = Shows.Id }] } }] });
        var calls = _h.Server.GetCalls;
        _h.Server.BeforeGetUserById = (id, call) =>
        {
            if (call == calls + 1)
            {
                _h.Server.AdminPost(id, p => p.EnabledFolders = [Horror.Id]);
            }
        };

        var report = await _h.RunAsync(SyncTrigger.StoreChanged);

        var skip = Assert.Single(report.Users);
        Assert.Equal((SyncActionKind.Skip, SyncReasons.NativeChanged, PolicyWriteOutcome.Aborted), (skip.Kind, skip.Reason, skip.Outcome!.Value));
        Assert.Equal([Horror.Id], _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        var line = Assert.Single(_h.Lines(AuditActions.DriftDetected));
        Assert.Equal(SyncReasons.NativeChanged, SyncHarness.Detail(line, "Reason"));
        Assert.Contains(Horror.Id.ToString("N"), SyncHarness.Detail(line, "Found"), StringComparison.Ordinal);
        _h.Server.BeforeGetUserById = null;
        var next = Assert.Single((await _h.RunAsync()).Users);
        Assert.Equal((SyncActionKind.Drift, SyncReasons.ReportOnly), (next.Kind, next.Reason));
    }

    [Fact]
    public async Task ANativeEditBetweenTheRunsReadAndAReleasesWrite_DropsTheRelease_BaselineChanged()
    {
        var solo = _h.Server.Add("solo", allFolders: false, folders: [Kids.Id]);
        var entry = Ledger(false, Movies) with
        {
            Release = new LedgerRelease { Choice = AccessRolesUnmanageChoice.Restore, RequestedUtc = _h.Now, EnabledFolders = [Movies.Id], Baseline = new LedgerAccess { EnabledFolders = [Kids.Id] } }
        };
        await _h.SaveAsync(d => d with { Ledger = new Dictionary<Guid, LedgerEntry> { [solo.Id] = entry } });
        var calls = _h.Server.GetCalls;
        _h.Server.BeforeGetUserById = (id, call) =>
        {
            if (call == calls + 1)
            {
                _h.Server.AdminPost(id, p => p.EnabledFolders = [Horror.Id]);
            }
        };

        var report = await _h.RunAsync();

        var drop = Assert.Single(report.Users);
        Assert.Equal((SyncActionKind.DropRelease, SyncReasons.BaselineChanged), (drop.Kind, drop.Reason));
        Assert.Equal([Horror.Id], _h.Server.Cached(solo.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Empty(_h.Store.Current.Ledger);
        Assert.Contains(Horror.Id.ToString("N"), SyncHarness.Detail(Assert.Single(_h.Lines(AuditActions.ReleaseDropped)), "Found"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledRun_AuditsTheWritesItMade_AndStops()
    {
        // An aborted POST Sync or a shutdown after a write must not leave it unaudited.
        var first = _h.Server.Add("a-first", allFolders: true);
        var second = _h.Server.Add("b-second", allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await SaveAsync([role], [Assign(SyncHarness.Read(first), [role]), Assign(SyncHarness.Read(second), [role])], captureLedgerFor: [first, second]);
        using var cancel = new CancellationTokenSource();
        _h.Server.DuringSave = id =>
        {
            if (id.Equals(first.Id))
            {
                cancel.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _h.Sync.RunAsync(new SyncRequest(SyncTrigger.Manual, AuditActor.System), cancel.Token));

        Assert.Equal([Kids.Id], _h.Server.Stored(first.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.True(_h.Server.Stored(second.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal(first.Id, Assert.Single(_h.Lines(AuditActions.PolicySynced)).TargetUserId);
        Assert.Equal([Kids.Id], _h.Store.Current.Ledger[first.Id].LastWrite!.EnabledFolders);
    }

    [Fact]
    public async Task ACancelDuringTheWaitBeforeTheWriteAgain_KeepsTheFailedSave_AndTheNextRunWritesIt()
    {
        // The failed save stays recorded (the writer's), and its line is written.
        var kid = _h.Server.Add("kid", allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await SaveAsync([role], [Assign(SyncHarness.Read(kid), [role])], captureLedgerFor: [kid]);
        using var cancel = new CancellationTokenSource();
        _h.Server.FailingSaves = 1;
        _h.Server.DuringSave = _ => cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _h.Sync.RunAsync(new SyncRequest(SyncTrigger.Manual, AuditActor.System), cancel.Token));

        Assert.True(_h.Writer.HasFailedSave(kid.Id));
        var line = Assert.Single(_h.Lines(AuditActions.SyncSkipped));
        Assert.Equal(("true", "true"), (SyncHarness.Detail(line, "SaveFailed"), SyncHarness.Detail(line, "Cancelled")));
        _h.Server.DuringSave = null;

        var rewritten = await _h.RunAsync(SyncTrigger.Retry);

        Assert.Equal((SyncReasons.RewriteAfterFailedSave, true), (Assert.Single(rewritten.Users).Reason, rewritten.Users[0].Wrote));
        Assert.Equal([Kids.Id], _h.Server.Stored(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task AFailedSave_OfAUserNoLongerManaged_OrDeleted_IsForgotten_AndAsksForNoMoreRuns()
    {
        // The record kept a run every 30 s going until a restart.
        var kid = _h.Server.Add("kid", allFolders: true);
        var gone = _h.Server.Add("gone", allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await SaveAsync([role], [Assign(SyncHarness.Read(kid), [role]), Assign(SyncHarness.Read(gone), [role])], captureLedgerFor: [kid, gone]);
        _h.Server.FailingSaves = 4;
        await _h.RunAsync();
        Assert.Equal(2, _h.Writer.FailedSaves.Count);

        await _h.SaveAsync(d => d with { Assignments = [.. d.Assignments.Select(a => a.UserId.Equals(kid.Id) ? a with { State = AccessRolesAssignmentState.Excluded } : a)] });
        _h.Server.Delete(gone.Id);
        var retries = 0;
        _h.Sync.RetryWanted += (_, _) => retries++;

        await _h.RunAsync(SyncTrigger.Retry);

        Assert.Empty(_h.Writer.FailedSaves);
        Assert.Equal(0, retries);
    }

    private async Task SaveThroughRolesApiAsync(Func<RoleAssignment, RoleAssignment> change, AccessRolesUnmanageChoice? choice)
    {
        // As the roles API saves a change: the analysis, then the ledger kept (ChangeAnalysis.KeepLedger).
        var before = _h.Store.Current;
        var after = before with { Assignments = [.. before.Assignments.Select(change)] };
        var report = ChangeAnalysis.Analyze(before, after, _h.Catalog.Read(), [.. _h.Server.Manager.Object.Users.Select(JellyfinServerUsers.From)], choice);
        var saved = ChangeAnalysis.KeepLedger(after, report, _h.Now);
        await _h.SaveAsync(_ => saved);
    }

    private async Task<User> ManagedUserAsync(string name)
    {
        var user = _h.Server.Add(name, allFolders: true);
        await _h.SaveAsync(d => d with
        {
            Assignments = [.. d.Assignments, Assign(SyncHarness.Read(user), [d.Roles[0]])],
            Ledger = new Dictionary<Guid, LedgerEntry>(d.Ledger) { [user.Id] = new LedgerEntry { OriginalEnableAllFolders = true, OriginalEnableUserPreferenceAccess = true, CapturedUtc = _h.Now } }
        });
        Assert.True((await _h.RunAsync()).Users.Single(u => u.User.Id.Equals(user.Id)).Wrote);
        return user;
    }

    public void Dispose() => _h.Dispose();

    private async Task<User> ManagedKidAsync()
    {
        var kid = _h.Server.Add("kid", allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await SaveAsync([role], [Assign(SyncHarness.Read(kid), [role])], captureLedgerFor: [kid]);
        Assert.True(Assert.Single((await _h.RunAsync()).Users).Wrote);
        return kid;
    }

    private Task SaveAsync(IEnumerable<AccessRole> roles, IEnumerable<RoleAssignment> assignments, IEnumerable<User>? captureLedgerFor = null)
        => _h.SaveAsync(d => d with
        {
            Roles = [.. roles],
            Assignments = [.. assignments],
            Ledger = (captureLedgerFor ?? []).ToDictionary(u => u.Id, u => new LedgerEntry
            {
                OriginalEnableAllFolders = u.HasPermission(PermissionKind.EnableAllFolders),
                OriginalEnabledFolders = u.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders),
                OriginalEnableUserPreferenceAccess = true,
                CapturedUtc = _h.Now
            })
        });

    private void SetCreated(Guid id, DateTime created)
    {
        var index = _h.Catalog.Libraries.FindIndex(l => l.Id.Equals(id));
        _h.Catalog.Libraries[index] = _h.Catalog.Libraries[index] with { CreatedUtc = created };
    }

    private IDictionary<string, object?> Describe() => _h.Status.Describe(null, "Reapply", 5, 0, 0, 0);
}
