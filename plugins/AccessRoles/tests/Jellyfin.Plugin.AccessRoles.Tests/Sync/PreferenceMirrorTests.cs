using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;
using Jellyfin.Plugin.Kit.TestSupport;

namespace Jellyfin.Plugin.AccessRoles.Tests.Sync;

/// <summary>
/// The optional native mirror (<c>MirrorPreferenceLockToPolicy</c>, off by default): native
/// <c>EnableUserPreferenceAccess=false</c> for a managed non-admin whose roles lock all five server-stored sections, written
/// by the policy writer with nothing else changed, checked again at every run, and put back on only where the mirror turned
/// it off.
/// </summary>
public sealed class PreferenceMirrorTests : IDisposable
{
    private static readonly AccessRolesLock[] Five = [AccessRolesLock.Profile, AccessRolesLock.DisplayAccount, AccessRolesLock.Home, AccessRolesLock.Playback, AccessRolesLock.Subtitles];

    private readonly SyncHarness _sync = new();

    [Fact]
    public async Task AManagedNonAdminWithAllFiveLocked_LosesTheFlag_RecordedAndAudited_AndNothingElseOfTheirsChanges()
    {
        _sync.Configuration.MirrorPreferenceLockToPolicy = true;
        var kid = _sync.Server.Add("kid");
        var before = FakeJellyfinUsers.Dump(_sync.Server.Stored(kid.Id));
        await AssignAsync(kid, Five);

        await _sync.RunAsync();

        Assert.False(_sync.Server.Stored(kid.Id).EnableUserPreferenceAccess);
        Assert.False(_sync.Server.Cached(kid.Id).EnableUserPreferenceAccess);
        // A new user's unset bitrate limit is stored as 0 by any full-policy write, the library sync's too (LibrarySyncTests).
        Assert.Equal(["User.EnableUserPreferenceAccess", "User.RemoteClientBitrateLimit", "User.RowVersion"], FakeJellyfinUsers.Differences(before, FakeJellyfinUsers.Dump(_sync.Server.Stored(kid.Id))));
        Assert.True(_sync.Store.Current.PreferenceMirror[kid.Id].OriginalEnableUserPreferenceAccess);
        var line = Assert.Single(_sync.Lines(AuditActions.PreferenceAccessMirrored));
        Assert.Equal(("AllSectionsLocked", "true"), (SyncHarness.Detail(line, "Reason"), SyncHarness.Detail(line, "Holds")));

        // A second run has nothing to do.
        var writes = _sync.Server.Writes.Count;
        await _sync.RunAsync();
        Assert.Equal(writes, _sync.Server.Writes.Count);
    }

    // The negative controls of the rule: four of the five, an administrator, the setting off, an excluded user, a dry run.
    [Theory]
    [InlineData("four")]
    [InlineData("admin")]
    [InlineData("off")]
    [InlineData("excluded")]
    [InlineData("dryRun")]
    public async Task OnlyAllFiveLocked_ForAManagedNonAdmin_WithTheSettingOn_InARealRun_TurnsTheFlagOff(string variant)
    {
        _sync.Configuration.MirrorPreferenceLockToPolicy = variant != "off";
        var user = _sync.Server.Add("user", admin: variant == "admin");
        await AssignAsync(user, variant == "four" ? Five.Where(l => l != AccessRolesLock.Subtitles) : Five, variant == "excluded" ? AccessRolesAssignmentState.Excluded : AccessRolesAssignmentState.Managed);

        await _sync.RunAsync(configure: r => r with { DryRun = variant == "dryRun" });

        Assert.True(_sync.Server.Stored(user.Id).EnableUserPreferenceAccess);
        Assert.Empty(_sync.Store.Current.PreferenceMirror);
        Assert.Empty(_sync.Lines(AuditActions.PreferenceAccessMirrored));
    }

    // The mirror's own administrator check (a mutation test's survivor): the resolver never locks an administrator's
    // sections (administrators are never restricted), so the runs above can't tell whether the mirror checks too. This pins the second check.
    [Fact]
    public void Wanted_IsNeverTrueForAnAdministrator_EvenWithAllFiveLocked()
    {
        var configuration = new PluginConfiguration { MirrorPreferenceLockToPolicy = true };
        var kid = AccessFixtures.User(1, "kid");
        var five = Role(1, "Five", Five);
        var effective = EffectiveRulesResolver.Resolve(Document([five], [Assign(kid, [five])]), Catalog, kid);
        Assert.True(PreferenceMirrorSync.Wanted(configuration, effective));

        var admin = effective with { User = effective.User with { IsAdministrator = true } };

        Assert.True(admin.Locked.IsSupersetOf(PreferenceMirrorSync.MirroredLocks));
        Assert.False(PreferenceMirrorSync.Wanted(configuration, admin));
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("setting")]
    [InlineData("unmanaged")]
    public async Task WhenItIsNoLongerWanted_TheFlagIsWrittenBackOn_AndTheRecordGoes(string variant)
    {
        _sync.Configuration.MirrorPreferenceLockToPolicy = true;
        var kid = _sync.Server.Add("kid");
        await AssignAsync(kid, Five);
        await _sync.RunAsync();
        Assert.False(_sync.Server.Stored(kid.Id).EnableUserPreferenceAccess);

        if (variant == "setting")
        {
            _sync.Configuration.MirrorPreferenceLockToPolicy = false;
        }
        else if (variant == "lock")
        {
            await AssignAsync(kid, [AccessRolesLock.Profile]);
        }
        else
        {
            await _sync.SaveAsync(d => d with { Assignments = [] });
        }

        await _sync.RunAsync();

        Assert.True(_sync.Server.Stored(kid.Id).EnableUserPreferenceAccess);
        Assert.Empty(_sync.Store.Current.PreferenceMirror);
        var line = Assert.Single(_sync.Lines(AuditActions.PreferenceAccessRestored));
        Assert.Equal(variant switch { "setting" => "MirrorOff", "lock" => "NotAllSectionsLocked", _ => "NotManaged" }, SyncHarness.Detail(line, "Reason"));
    }

    [Fact]
    public async Task AFlagSomeoneElseTurnedOff_IsNeverTurnedOn()
    {
        _sync.Configuration.MirrorPreferenceLockToPolicy = true;
        var kid = _sync.Server.Add("kid");
        _sync.Server.AdminPost(kid.Id, p => p.EnableUserPreferenceAccess = false);
        await AssignAsync(kid, Five);
        await _sync.RunAsync();
        await AssignAsync(kid, [AccessRolesLock.Profile]);

        await _sync.RunAsync();

        Assert.False(_sync.Server.Stored(kid.Id).EnableUserPreferenceAccess);
        Assert.Empty(_sync.Store.Current.PreferenceMirror);
        Assert.Empty(_sync.Lines(AuditActions.PreferenceAccessRestored));
    }

    // The 10.11.5 sign-in race puts the stale object back into the cache: the next run writes the flag again.
    [Fact]
    public async Task AStaleUserPutBackBySignIn_IsWrittenAgainAtTheNextRun()
    {
        _sync.Configuration.MirrorPreferenceLockToPolicy = true;
        var kid = _sync.Server.Add("kid");
        await AssignAsync(kid, Five);
        await _sync.RunAsync();
        _sync.Server.SignInRace(kid.Id);
        Assert.True(_sync.Server.Cached(kid.Id).EnableUserPreferenceAccess);

        await _sync.RunAsync();

        Assert.False(_sync.Server.Cached(kid.Id).EnableUserPreferenceAccess);
        Assert.False(_sync.Server.Stored(kid.Id).EnableUserPreferenceAccess);
        Assert.Equal(["AllSectionsLocked", "WrittenBackOn"], _sync.Lines(AuditActions.PreferenceAccessMirrored).Select(l => SyncHarness.Detail(l, "Reason")));
    }

    [Theory]
    [InlineData("safeMode", true)]
    [InlineData("enforcementOff", true)]
    [InlineData("librarySyncOff", false)]
    public async Task TheSafetyPausesStopTheMirror_TheLibrarySyncSwitchDoesnt(string pause, bool paused)
    {
        _sync.Configuration.MirrorPreferenceLockToPolicy = true;
        var kid = _sync.Server.Add("kid");
        await AssignAsync(kid, Five);
        switch (pause)
        {
            case "safeMode":
                await File.WriteAllTextAsync(_sync.Paths.SafeModeFile, string.Empty, TestContext.Current.CancellationToken);
                break;
            case "enforcementOff":
                _sync.Configuration.EnforcementEnabled = false;
                break;
            default:
                _sync.Configuration.LibrarySyncEnabled = false;
                break;
        }

        await _sync.RunAsync();

        Assert.Equal(paused, _sync.Server.Stored(kid.Id).EnableUserPreferenceAccess);
    }

    [Fact]
    public async Task ReleaseAndTheUninstallStop_PutTheRecordedFlagsBackOn_AndNoRunTurnsThemOffAgain()
    {
        _sync.Configuration.MirrorPreferenceLockToPolicy = true;
        var kid = _sync.Server.Add("kid");
        var other = _sync.Server.Add("other");
        await AssignAsync(kid, Five);
        await AssignAsync(other, Five, keep: true);
        await _sync.RunAsync();
        Assert.Equal(2, _sync.Store.Current.PreferenceMirror.Count);

        _sync.Mirror.StopForUninstall();
        var released = await _sync.Mirror.ReleaseAsync(AuditActor.System, null, "Uninstalling", TestContext.Current.CancellationToken);
        await _sync.RunAsync();

        Assert.Equal([MirrorAction.TurnedOn, MirrorAction.TurnedOn], released.Select(r => r.Action));
        Assert.True(_sync.Server.Stored(kid.Id).EnableUserPreferenceAccess);
        Assert.True(_sync.Server.Stored(other.Id).EnableUserPreferenceAccess);
        Assert.Empty(_sync.Store.Current.PreferenceMirror);
        Assert.All(_sync.Lines(AuditActions.PreferenceAccessRestored), l => Assert.Equal("Uninstalling", SyncHarness.Detail(l, "ReleasedFor")));
        Assert.Equal(PreferenceMirrorSync.Uninstalling, _sync.Mirror.PauseReason());
    }

    [Fact]
    public async Task ARecordOfAUserTheServerNoLongerHas_Goes()
    {
        _sync.Configuration.MirrorPreferenceLockToPolicy = true;
        var kid = _sync.Server.Add("kid");
        await AssignAsync(kid, Five);
        await _sync.RunAsync();
        _sync.Server.Delete(kid.Id);

        await _sync.RunAsync();

        Assert.Empty(_sync.Store.Current.PreferenceMirror);
    }

    // The record before the flag. A data file that can't be saved (its temporary file held, as a
    // full disk, an antivirus or a backup tool would make it) writes no flag without a record: each user's failure is
    // audited WriteAborted, the other users go on (the kid, whose record is kept, is written off again), and the next run
    // writes both the records and the flags.
    [Fact]
    public async Task ADataFileThatCantBeSaved_LeavesNoFlagOffWithoutARecord_AuditsEachUser_AndTheOthersGoOn()
    {
        _sync.Configuration.MirrorPreferenceLockToPolicy = true;
        var guest = _sync.Server.Add("guest");
        var teen = _sync.Server.Add("teen");
        var kid = _sync.Server.Add("kid");
        await AssignAsync(kid, Five);
        await _sync.RunAsync();
        _sync.Server.AdminPost(kid.Id, p => p.EnableUserPreferenceAccess = true);
        await AssignAsync(guest, Five);
        await AssignAsync(teen, Five);

        using (HoldTheStoresTemporaryFile())
        {
            await _sync.RunAsync();
        }

        Assert.True(_sync.Server.Stored(guest.Id).EnableUserPreferenceAccess);
        Assert.True(_sync.Server.Stored(teen.Id).EnableUserPreferenceAccess);
        Assert.False(_sync.Server.Stored(kid.Id).EnableUserPreferenceAccess);
        Assert.Equal([kid.Id], _sync.Store.Current.PreferenceMirror.Keys);
        var aborted = _sync.Lines(AuditActions.WriteAborted);
        Assert.Equal(new[] { guest.Id, teen.Id }.Order(), aborted.Select(l => l.TargetUserId!.Value).Order());
        Assert.All(aborted, l => Assert.Equal(("PreferenceMirrorFailed", "TurnOff"), (SyncHarness.Detail(l, "Reason"), SyncHarness.Detail(l, "Mirror"))));
        Assert.Equal(["AllSectionsLocked", "WrittenBackOn"], _sync.Lines(AuditActions.PreferenceAccessMirrored).Select(l => SyncHarness.Detail(l, "Reason")));

        await _sync.RunAsync();

        Assert.False(_sync.Server.Stored(guest.Id).EnableUserPreferenceAccess);
        Assert.False(_sync.Server.Stored(teen.Id).EnableUserPreferenceAccess);
        Assert.Equal(new[] { guest.Id, teen.Id, kid.Id }.Order(), _sync.Store.Current.PreferenceMirror.Keys.Order());
    }

    // A write that didn't happen drops the record made for it: a flag someone else turned off between the users' read and
    // the write (Unchanged) is never the mirror's. A save that failed keeps it: the server's cache may hold the write.
    [Theory]
    [InlineData("offMeanwhile")]
    [InlineData("saveFailed")]
    public async Task AWriteThatDidntHappen_DropsTheRecordMadeForIt_ButAFailedSaveKeepsIt(string variant)
    {
        _sync.Configuration.MirrorPreferenceLockToPolicy = true;
        var kid = _sync.Server.Add("kid");
        await AssignAsync(kid, Five);
        var changed = false;
        if (variant == "offMeanwhile")
        {
            _sync.Server.BeforeGetUserById = (id, _) =>
            {
                if (id.Equals(kid.Id) && !changed)
                {
                    changed = true;
                    _sync.Server.AdminPost(kid.Id, p => p.EnableUserPreferenceAccess = false);
                }
            };
        }
        else
        {
            _sync.Server.FailingSaves = 1;
        }

        var results = await _sync.Mirror.RunAsync(AuditActor.System, null, TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        if (variant == "offMeanwhile")
        {
            Assert.Equal((MirrorAction.None, "Unchanged"), (result.Action, result.Reason));
            Assert.Empty(_sync.Store.Current.PreferenceMirror);
            Assert.False(_sync.Server.Stored(kid.Id).EnableUserPreferenceAccess);
        }
        else
        {
            Assert.Equal(MirrorAction.Failed, result.Action);
            Assert.True(result.Write!.SaveFailed);
            Assert.Equal([kid.Id], _sync.Store.Current.PreferenceMirror.Keys);
        }

        Assert.Empty(_sync.Lines(AuditActions.PreferenceAccessMirrored));
    }

    // A user Degraded mode locks (the store lost their assignment) keeps the flag the mirror turned
    // off until an admin ends it; then the next run, which the end asks for, puts it back on.
    [Fact]
    public async Task AFlagOfAUserDegradedModeLocks_StaysOff_UntilAnAdminEndsIt_ThenGoesBackOn()
    {
        _sync.Configuration.MirrorPreferenceLockToPolicy = true;
        var kid = _sync.Server.Add("kid");
        await AssignAsync(kid, Five);
        await _sync.RunAsync();
        Assert.False(_sync.Server.Stored(kid.Id).EnableUserPreferenceAccess);

        // An earlier degraded start's marker lists the kid; the data file now doesn't manage them.
        await File.WriteAllTextAsync(_sync.Store.Degraded.MarkerPath, "{\"Because\":\"ReadOnlyDataUnreadable\",\"Users\":[\"" + kid.Id.ToString("N") + "\"]}", TestContext.Current.CancellationToken);
        await _sync.SaveAsync(d => d with { Assignments = [] });
        await _sync.RunAsync();

        Assert.True(_sync.Store.Degraded.Current!.Applies(kid.Id));
        Assert.False(_sync.Server.Stored(kid.Id).EnableUserPreferenceAccess);
        Assert.Equal([kid.Id], _sync.Store.Current.PreferenceMirror.Keys);
        Assert.Empty(_sync.Lines(AuditActions.PreferenceAccessRestored));

        var resolved = 0;
        _sync.Store.Degraded.Resolved += (_, _) => resolved++;
        _sync.Store.Degraded.Resolve();
        await _sync.RunAsync();

        Assert.Equal(1, resolved);
        Assert.True(_sync.Server.Stored(kid.Id).EnableUserPreferenceAccess);
        Assert.Empty(_sync.Store.Current.PreferenceMirror);
        Assert.Equal("NotManaged", SyncHarness.Detail(Assert.Single(_sync.Lines(AuditActions.PreferenceAccessRestored)), "Reason"));
    }

    public void Dispose() => _sync.Dispose();

    // An exclusive handle on the store's temporary file: the kit's next save of the data file fails (503) until it closes.
    private FileStream HoldTheStoresTemporaryFile()
        => new(_sync.Store.Documents.FilePath + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private async Task AssignAsync(User user, IEnumerable<AccessRolesLock> locks, AccessRolesAssignmentState state = AccessRolesAssignmentState.Managed, bool keep = false)
    {
        var number = 100 + _sync.Server.Manager.Object.AllUsers().ToList().FindIndex(u => u.Id.Equals(user.Id));
        var role = Role(number, "Role " + number, locks);
        await _sync.SaveAsync(d => d with
        {
            Roles = [.. d.Roles.Where(r => !r.Id.Equals(role.Id)), role],
            Assignments = [.. d.Assignments.Where(a => !a.UserId.Equals(user.Id)), Assign(SyncHarness.Read(user), [role], state: state)]
        });
        _ = keep;
    }
}
