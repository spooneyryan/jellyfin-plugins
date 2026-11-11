using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Access;

/// <summary>
/// <see cref="ChangeAnalysis"/>: what a change does per user,
/// every way of leaving library management with each <c>onUnmanage</c> choice, the accidental empty result, the lockout
/// case, and the ledger kept for a saved change.
/// </summary>
public sealed class ChangeAnalysisTests
{
    private static readonly AccessRole Kid = Role(1, "Kid", AccessPresets.Locks(AccessRolesPreset.Kid), AccessRolesRoleLibraryMode.Only, Kids);
    private static readonly AccessRole Family = Role(2, "Family", [AccessRolesLock.Playback], AccessRolesRoleLibraryMode.Only, Movies, Kids);
    private static readonly AccessRole LocksOnly = Role(5, "Locks only", [AccessRolesLock.Profile]);

    private static readonly ServerUser KidUser = User(1, "kid", native: Folders(Kids, Movies));

    public static TheoryData<string> LeavingTriggers => ["LastRoleRemoved", "RoleDeleted", "RoleNotManaged", "Excluded", "Unmanaged", "OverridesCleared"];

    [Theory]
    [MemberData(nameof(LeavingTriggers))]
    public void EveryWayOfLeavingLibraryManagement_NeedsAChoice(string trigger)
    {
        // Leaving library management: removing the last library role, deleting the role, switching it to Not managed, Excluded, Unmanage
        // and clearing the only overrides of a user without a library role.
        var (before, after) = Leaving(trigger);

        var report = ChangeAnalysis.Analyze(before, after, Catalog, [KidUser], choice: null);

        var change = Assert.Single(report.ChoiceRequired);
        Assert.Same(KidUser, change.User);
        Assert.True(change.Before.Libraries.Managed);
        Assert.False(change.After.Libraries.Managed);
        Assert.Null(change.Leaving!.Choice);
        Assert.Contains(change, report.Affected);
    }

    [Theory]
    [MemberData(nameof(LeavingTriggers))]
    public void Restore_GivesTheOriginalAccessFromTheLedger(string trigger)
    {
        var (before, after) = Leaving(trigger);

        var leaving = Assert.Single(ChangeAnalysis.Analyze(before, after, Catalog, [KidUser], AccessRolesUnmanageChoice.Restore).Users).Leaving!;

        Assert.Equal(AccessRolesUnmanageChoice.Restore, leaving.Choice);
        Assert.False(leaving.NoLedgerEntry);
        Assert.False(leaving.EnableAllFolders);
        Assert.Equal(Catalog.Order([Movies.Id, Shows.Id]), leaving.EnabledFolders);
    }

    [Fact]
    public void Restore_DropsADeletedLibrary_AndIsRefusedWithoutALedgerEntry()
    {
        var (before, after) = Leaving("LastRoleRemoved");
        var catalog = new LibraryCatalog(Catalog.Libraries.Where(l => l.Id != Shows.Id));

        var restored = Assert.Single(ChangeAnalysis.Analyze(before, after, catalog, [KidUser], AccessRolesUnmanageChoice.Restore).Users).Leaving!;
        var unrecorded = ChangeAnalysis.Analyze(before with { Ledger = new Dictionary<Guid, LedgerEntry>() }, after, Catalog, [KidUser], AccessRolesUnmanageChoice.Restore);

        Assert.Equal([Movies.Id], restored.EnabledFolders);
        Assert.True(Assert.Single(unrecorded.NoLedgerEntry).Leaving!.NoLedgerEntry);
    }

    [Fact]
    public void Keep_FreezesTheAccessAsItIs_AndNamesItsLibraries()
    {
        var (before, after) = Leaving("RoleDeleted");

        var change = Assert.Single(ChangeAnalysis.Analyze(before, after, Catalog, [KidUser], AccessRolesUnmanageChoice.Keep).Users);

        Assert.Equal(["Kids", "Movies"], change.Leaving!.FrozenAt);
        Assert.False(change.PolicyWrite);
        Assert.Equal(new HashSet<Guid> { Kids.Id, Movies.Id }, change.AfterEnabledFolders.ToHashSet());
    }

    [Fact]
    public void GrantAll_GivesEveryLibrary_AndIsAPendingWrite()
    {
        var (before, after) = Leaving("Excluded");

        var change = Assert.Single(ChangeAnalysis.Analyze(before, after, Catalog, [KidUser], AccessRolesUnmanageChoice.GrantAll).Users);

        Assert.True(change.Leaving!.EnableAllFolders);
        Assert.True(change.AfterEnableAllFolders);
        Assert.True(change.PolicyWrite);
        Assert.Equal(["Collections", "Horror", "Movies", "Shows"], Names(change.LibrariesAdded));
    }

    [Fact]
    public void AChangeOfLocksOnly_TakesNoOneOutOfLibraryManagement()
    {
        var before = Document([LocksOnly], [Assign(KidUser, [LocksOnly])]);
        var after = Document([LocksOnly], []);

        var report = ChangeAnalysis.Analyze(before, after, Catalog, [KidUser], choice: null);

        var change = Assert.Single(report.Affected);
        Assert.Null(change.Leaving);
        Assert.Empty(report.ChoiceRequired);
        Assert.Equal([AccessRolesLock.Profile], change.LocksRemoved);
        Assert.Equal(AccessRolesUserState.NotManaged, change.After.State);
    }

    [Fact]
    public void TheEffect_IsMeasuredFromWhatTheUserShouldHaveBefore_AndPolicyWriteFromTheNativeAccess()
    {
        // Kid (Only Kids) → Family (Movies, Kids): Movies is added; the native access (Kids, Movies) already matches.
        var before = Document([Kid, Family], [Assign(KidUser, [Kid])]);
        var after = Document([Kid, Family], [Assign(KidUser, [Family])]);

        var change = Assert.Single(ChangeAnalysis.Analyze(before, after, Catalog, [KidUser], choice: null).Affected);

        Assert.Equal(["Movies"], Names(change.LibrariesAdded));
        Assert.Empty(change.LibrariesRemoved);
        Assert.False(change.PolicyWrite);
        Assert.Contains(AccessRolesLock.Playback, change.LocksAdded);
        Assert.Contains(AccessRolesLock.Profile, change.LocksRemoved);
        Assert.True(change.AssignmentChanged);
    }

    [Fact]
    public void AnAccidentalEmptyResult_IsRefused_TheExplicitChoiceIsNot_AndAnAlreadyEmptyUserDoesntBlockOtherChanges()
    {
        var user = User(1, "kid");
        var before = Document([Kid], [Assign(user, [Kid])]);
        var emptied = Document([Kid], [Assign(user, [Kid], remove: [Kids])]);
        var chosen = Document([Kid], [Assign(user, mode: AccessRolesUserLibraryMode.Only, confirmNoLibraries: true)]);
        var renamed = emptied with { Roles = [Kid with { Name = "Kid 2" }] };

        Assert.Same(user, Assert.Single(ChangeAnalysis.Analyze(before, emptied, Catalog, [user], null).NoLibraries).User);
        Assert.Empty(ChangeAnalysis.Analyze(before, chosen, Catalog, [user], null).NoLibraries);
        Assert.Empty(ChangeAnalysis.Analyze(emptied, renamed, Catalog, [user], null).NoLibraries);
    }

    [Fact]
    public void LockoutGuard_FlagsAChangeThatLeavesNoEnabledAdministratorWithEveryLibrary()
    {
        var admin = User(11, "admin", admin: true);
        var second = User(12, "admin2", admin: true);
        var disabled = User(13, "off", admin: true, disabled: true);
        var before = Document([Kid], []);
        var narrowed = Document([Kid], [Assign(admin, [Kid])]);

        Assert.True(ChangeAnalysis.Analyze(before, narrowed, Catalog, [admin, disabled], null).NoFullAdministrator);
        Assert.False(ChangeAnalysis.Analyze(before, narrowed, Catalog, [admin, second], null).NoFullAdministrator);
        Assert.False(ChangeAnalysis.Analyze(before, Document([Kid], [Assign(KidUser, [Kid])]), Catalog, [admin, KidUser], null).NoFullAdministrator);
    }

    [Fact]
    public void KeepLedger_RecordsTheOriginalAccess_WhenAUserBecomesManaged_AndOnlyThen()
    {
        var user = User(1, "kid", native: Folders(Movies, Shows) with { EnableUserPreferenceAccess = false });
        var before = Document([Kid, LocksOnly], []);
        var managed = Document([Kid, LocksOnly], [Assign(user, [Kid])]);
        var locksOnly = Document([Kid, LocksOnly], [Assign(user, [LocksOnly])]);

        var kept = ChangeAnalysis.KeepLedger(managed, ChangeAnalysis.Analyze(before, managed, Catalog, [user], null), Now);
        var untouched = ChangeAnalysis.KeepLedger(locksOnly, ChangeAnalysis.Analyze(before, locksOnly, Catalog, [user], null), Now);

        var entry = kept.Ledger[user.Id];
        Assert.False(entry.OriginalEnableAllFolders);
        Assert.Equal([Movies.Id, Shows.Id], entry.OriginalEnabledFolders);
        Assert.False(entry.OriginalEnableUserPreferenceAccess);
        Assert.Equal(Now, entry.CapturedUtc);
        Assert.Null(entry.LastWrite);
        Assert.Same(locksOnly, untouched);
    }

    [Theory]
    [MemberData(nameof(LeavingTriggers))]
    public void KeepLedger_LeavesARestoreOrGrantAllToWrite_WithTheNativeAccessItWasComputedAgainst(string trigger)
    {
        var (before, after) = Leaving(trigger);

        AccessRolesDocument KeepFor(AccessRolesUnmanageChoice choice)
            => ChangeAnalysis.KeepLedger(after, ChangeAnalysis.Analyze(before, after, Catalog, [KidUser], choice), Now);

        var restored = KeepFor(AccessRolesUnmanageChoice.Restore).Ledger[KidUser.Id];
        var granted = KeepFor(AccessRolesUnmanageChoice.GrantAll).Ledger[KidUser.Id].Release!;

        Assert.True(restored.Release!.Choice.Is(AccessRolesUnmanageChoice.Restore));
        Assert.Equal(Now, restored.Release.RequestedUtc);
        Assert.Equal(Catalog.Order([Movies.Id, Shows.Id]), restored.Release.EnabledFolders);
        Assert.Equal([Movies.Id, Shows.Id], restored.OriginalEnabledFolders);
        Assert.True(granted.EnableAllFolders);
        foreach (var release in new[] { restored.Release, granted })
        {
            // The baseline is the native access the choice was computed against (KidUser natively sees Kids and Movies).
            Assert.False(release.Baseline!.EnableAllFolders);
            Assert.Equal(new HashSet<Guid> { Kids.Id, Movies.Id }, release.Baseline.EnabledFolders.ToHashSet());
        }
    }

    [Theory]
    [InlineData("LastRoleRemoved", true)]
    [InlineData("RoleDeleted", true)]
    [InlineData("RoleNotManaged", true)]
    [InlineData("Excluded", true)]
    [InlineData("OverridesCleared", true)]
    [InlineData("Unmanaged", false)]
    public void KeepLedger_KeepsTheEntryWhileTheUserKeepsAnAssignment_AndDropsItWithTheAssignment(string trigger, bool assignmentStays)
    {
        // Leaving library management with Keep (or a Restore that changes nothing) while the user keeps an
        // assignment is a pause, so the original access stays recorded; the entry goes with the assignment.
        var (before, after) = Leaving(trigger);
        var matching = KidUser with { Native = Folders(Movies, Shows) };

        var kept = ChangeAnalysis.KeepLedger(after, ChangeAnalysis.Analyze(before, after, Catalog, [KidUser], AccessRolesUnmanageChoice.Keep), Now);
        var restored = ChangeAnalysis.KeepLedger(after, ChangeAnalysis.Analyze(before, after, Catalog, [matching], AccessRolesUnmanageChoice.Restore), Now);

        foreach (var document in new[] { kept, restored })
        {
            Assert.Equal(assignmentStays, document.Ledger.ContainsKey(KidUser.Id));
            if (assignmentStays)
            {
                Assert.Equal([Movies.Id, Shows.Id], document.Ledger[KidUser.Id].OriginalEnabledFolders);
                Assert.Null(document.Ledger[KidUser.Id].Release);
            }
        }
    }

    [Fact]
    public void APause_KeepsTheRealOriginalAccess_AfterThePluginWroteTheUsers()
    {
        // The review's sandbox case: natively Movies + Shows; given Kid, so the ledger records Movies + Shows; the library
        // sync then writes Kids; Excluded with Keep; back to Managed; Unmanage with Restore must give Movies + Shows
        // back, never the Kids the plugin wrote.
        var user = User(1, "solo", native: Folders(Movies, Shows));
        var written = user with { Native = Folders(Kids) };
        var empty = Document([Kid], []);
        var managed = empty with { Assignments = [Assign(user, [Kid])] };
        var excluded = empty with { Assignments = [Assign(user, [Kid], state: AccessRolesAssignmentState.Excluded)] };

        var captured = Save(empty, managed, user, null);
        var paused = Save(captured, excluded with { Ledger = captured.Ledger }, written, AccessRolesUnmanageChoice.Keep);
        var again = Save(paused, managed with { Ledger = paused.Ledger }, written, null);
        var report = ChangeAnalysis.Analyze(again, empty with { Ledger = again.Ledger }, Catalog, [written], AccessRolesUnmanageChoice.Restore);
        var released = ChangeAnalysis.KeepLedger(empty with { Ledger = again.Ledger }, report, Now);

        Assert.Equal([Movies.Id, Shows.Id], paused.Ledger[user.Id].OriginalEnabledFolders);
        Assert.Equal([Movies.Id, Shows.Id], again.Ledger[user.Id].OriginalEnabledFolders);
        var leaving = Assert.Single(report.Users).Leaving!;
        Assert.Equal(Catalog.Order([Movies.Id, Shows.Id]), leaving.EnabledFolders);
        var release = released.Ledger[user.Id].Release!;
        Assert.Equal(Catalog.Order([Movies.Id, Shows.Id]), release.EnabledFolders);
        Assert.Equal([Kids.Id], release.Baseline!.EnabledFolders);

        static AccessRolesDocument Save(AccessRolesDocument from, AccessRolesDocument to, ServerUser who, AccessRolesUnmanageChoice? choice)
            => ChangeAnalysis.KeepLedger(to, ChangeAnalysis.Analyze(from, to, Catalog, [who], choice), Now);
    }

    [Fact]
    public void KeepLedger_DropsAnUnmanagedUsersEntryWithTheirAssignment_ButNotAReleaseStillToWrite()
    {
        // An excluded user (libraries not managed) whose assignment goes: nothing is left to write, so the entry goes, unless
        // a release is still pending; other users' entries are left alone.
        var other = User(2, "other", native: Folders(Movies));
        var release = new LedgerRelease { Choice = AccessRolesUnmanageChoice.GrantAll, EnableAllFolders = true, RequestedUtc = Now };
        var ledger = new Dictionary<Guid, LedgerEntry> { [KidUser.Id] = Ledger(false, Movies, Shows), [other.Id] = Ledger(false, Movies) };
        var before = Document([Kid], [Assign(KidUser, [Kid], state: AccessRolesAssignmentState.Excluded)], ledger);
        var after = before with { Assignments = [] };
        var pending = before with { Ledger = new Dictionary<Guid, LedgerEntry>(ledger) { [KidUser.Id] = ledger[KidUser.Id] with { Release = release } } };

        var dropped = ChangeAnalysis.KeepLedger(after, ChangeAnalysis.Analyze(before, after, Catalog, [KidUser, other], AccessRolesUnmanageChoice.Keep), Now);
        var waiting = ChangeAnalysis.KeepLedger(pending with { Assignments = [] }, ChangeAnalysis.Analyze(pending, pending with { Assignments = [] }, Catalog, [KidUser, other], AccessRolesUnmanageChoice.Keep), Now);

        Assert.False(dropped.Ledger.ContainsKey(KidUser.Id));
        Assert.True(dropped.Ledger.ContainsKey(other.Id));
        Assert.Same(release, waiting.Ledger[KidUser.Id].Release);
    }

    [Fact]
    public void KeepLedger_ClearsAPendingRelease_WhenTheUserIsManagedAgain()
    {
        var released = Ledger(false, Movies) with { Release = new LedgerRelease { Choice = AccessRolesUnmanageChoice.GrantAll, EnableAllFolders = true, RequestedUtc = Now } };
        var before = Document([Kid], [], new Dictionary<Guid, LedgerEntry> { [KidUser.Id] = released });
        var after = before with { Assignments = [Assign(KidUser, [Kid])] };

        var kept = ChangeAnalysis.KeepLedger(after, ChangeAnalysis.Analyze(before, after, Catalog, [KidUser], null), Now);

        Assert.Null(kept.Ledger[KidUser.Id].Release);
        Assert.Equal([Movies.Id], kept.Ledger[KidUser.Id].OriginalEnabledFolders);
    }

    [Fact]
    public void SameAssignment_IgnoresUpdatedUtc_AndComparesEverythingElse()
    {
        var a = Assign(KidUser, [Kid]);

        Assert.True(ChangeAnalysis.SameAssignment(a, a with { UpdatedUtc = Now }));
        Assert.False(ChangeAnalysis.SameAssignment(a, a with { RoleIds = [Family.Id] }));
        Assert.False(ChangeAnalysis.SameAssignment(a, null));
        Assert.True(ChangeAnalysis.SameAssignment(null, null));
    }

    [Theory]
    [InlineData("LastRoleRemoved")]
    [InlineData("RoleDeleted")]
    [InlineData("RoleNotManaged")]
    [InlineData("Excluded")]
    [InlineData("OverridesCleared")]
    public void KeepLedger_ForgetsTheLastWrite_WhenTheLibrariesLeaveManagement_WhateverTheChoice(string trigger)
    {
        // While the libraries aren't managed their native access is the admin's (or a
        // release's), so the plugin's last write says nothing about drift when they are managed again. The original access
        // and the release are kept as the roles API's contract says.
        var (before, after) = Leaving(trigger);
        var written = WithLastWrite(before, Kids);

        foreach (var choice in new[] { AccessRolesUnmanageChoice.Keep, AccessRolesUnmanageChoice.Restore, AccessRolesUnmanageChoice.GrantAll })
        {
            var kept = ChangeAnalysis.KeepLedger(after with { Ledger = written.Ledger }, ChangeAnalysis.Analyze(written, after with { Ledger = written.Ledger }, Catalog, [KidUser], choice), Now);

            var entry = kept.Ledger[KidUser.Id];
            Assert.Null(entry.LastWrite);
            Assert.Equal([Movies.Id, Shows.Id], entry.OriginalEnabledFolders);
            Assert.Equal(choice != AccessRolesUnmanageChoice.Keep, entry.Release is not null);
        }
    }

    [Fact]
    public void KeepLedger_ForgetsALastWriteKeptThroughAPause_WhenTheUserIsManagedAgain_AndKeepsAManagedUsersOne()
    {
        // A pause saved by 0.9.3.0 kept the last write; managed again, it goes. A user who stays managed keeps theirs.
        var paused = WithLastWrite(Document([Kid], [Assign(KidUser, [Kid], state: AccessRolesAssignmentState.Excluded)], new Dictionary<Guid, LedgerEntry> { [KidUser.Id] = Ledger(false, Movies, Shows) }), Kids);
        var managed = paused with { Assignments = [Assign(KidUser, [Kid])] };
        var other = WithLastWrite(managed, Kids) with { Roles = [Kid, Family] };

        var again = ChangeAnalysis.KeepLedger(managed, ChangeAnalysis.Analyze(paused, managed, Catalog, [KidUser], null), Now);
        var stays = ChangeAnalysis.KeepLedger(other with { Assignments = [Assign(KidUser, [Kid, Family])] }, ChangeAnalysis.Analyze(other, other with { Assignments = [Assign(KidUser, [Kid, Family])] }, Catalog, [KidUser], null), Now);

        Assert.Null(again.Ledger[KidUser.Id].LastWrite);
        Assert.Equal([Movies.Id, Shows.Id], again.Ledger[KidUser.Id].OriginalEnabledFolders);
        Assert.Equal([Kids.Id], stays.Ledger[KidUser.Id].LastWrite!.EnabledFolders);
    }

    private static AccessRolesDocument WithLastWrite(AccessRolesDocument document, params CatalogLibrary[] written)
        => document with
        {
            Ledger = document.Ledger.ToDictionary(p => p.Key, p => p.Value with { LastWrite = new LedgerWrite { Utc = Now.AddHours(-1), EnabledFolders = [.. written.Select(l => l.Id)] } })
        };

    private static (AccessRolesDocument Before, AccessRolesDocument After) Leaving(string trigger)
    {
        // The kid is managed for libraries by Kid; the ledger holds their original Movies + Shows.
        var ledger = new Dictionary<Guid, LedgerEntry> { [KidUser.Id] = Ledger(false, Movies, Shows) };
        var before = Document([Kid, LocksOnly], [Assign(KidUser, [Kid, LocksOnly])], ledger);
        return trigger switch
        {
            "LastRoleRemoved" => (before, before with { Assignments = [Assign(KidUser, [LocksOnly])] }),
            "RoleDeleted" => (before, before with { Roles = [LocksOnly], Assignments = [Assign(KidUser, [LocksOnly])] }),
            "RoleNotManaged" => (before, before with { Roles = [Kid with { Libraries = new RoleLibraries() }, LocksOnly] }),
            "Excluded" => (before, before with { Assignments = [Assign(KidUser, [Kid, LocksOnly], state: AccessRolesAssignmentState.Excluded)] }),
            "Unmanaged" => (before, before with { Assignments = [] }),
            "OverridesCleared" => (
                Document([LocksOnly], [Assign(KidUser, [LocksOnly], remove: [Horror])], ledger),
                Document([LocksOnly], [Assign(KidUser, [LocksOnly])], ledger)),
            _ => throw new ArgumentOutOfRangeException(nameof(trigger))
        };
    }
}
