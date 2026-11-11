using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Enforcement;

/// <summary>
/// Degraded mode (limited to the newest snapshot's managed users; a store that
/// starts read-only, a cold start, fault injection): who it locks, read from where, durable across restarts until an
/// admin confirms, and locks that never weaken.
/// </summary>
public sealed class DegradedModeTests : IDisposable
{
    private static readonly Guid KidId = Guid.Parse("11111111111111111111111111111111");
    private static readonly Guid TeenId = Guid.Parse("22222222222222222222222222222222");
    private static readonly Guid OtherId = Guid.Parse("33333333333333333333333333333333");

    private readonly TempDirectory _directory = new();

    private string File_ => Path.Combine(_directory.Path, "accessroles.json");

    private string Snapshots => Path.Combine(_directory.Path, KitPathsSnapshots);

    private const string KitPathsSnapshots = "snapshots";

    [Fact]
    public void ACorruptFileAndBak_LockOnlyTheUsersTheFileListsAsManaged()
    {
        // The schema is broken elsewhere (Roles isn't a list): the store can't read it, the lenient read can.
        Write(File_, Broken(version: 1));
        Write(File_ + ".bak", "not json at all");
        using var store = Open();

        Assert.Equal(StoreStatus.ReadOnlyDataUnreadable, store.Status);
        var state = Assert.IsType<DegradedState>(store.Degraded.Current);
        Assert.False(state.EveryNonAdmin);
        Assert.Equal([KidId], state.Users.Order());
        Assert.Equal("accessroles.json", state.Source);
        Assert.True(store.Degraded.EnteredNow);
        Assert.False(store.Degraded.CanResolve);

        var snapshot = LockSnapshot.Build(store.Current, store.Status, state);
        Assert.Equal(EveryServerEnforced, snapshot.For(KidId).Order());
        Assert.Empty(snapshot.For(TeenId));
        Assert.Empty(snapshot.For(OtherId));
        Assert.True(snapshot.IsManaged(KidId));
        Assert.False(snapshot.IsManaged(OtherId));
        Assert.True(File.Exists(store.Degraded.MarkerPath));
    }

    [Fact]
    public void NothingReadable_LocksEveryNonAdmin_AServerWideBehaviour()
    {
        Write(File_, "not json");
        Write(File_ + ".bak", "{ also not");
        using var store = Open();

        Assert.Equal(StoreStatus.ReadOnlyDataUnreadable, store.Status);
        var state = Assert.IsType<DegradedState>(store.Degraded.Current);
        Assert.True(state.EveryNonAdmin);
        Assert.Null(state.Source);
        Assert.Equal(EveryServerEnforced, LockSnapshot.Build(store.Current, store.Status, state).For(OtherId).Order());
    }

    [Fact]
    public void ANewerVersionsFileThisBuildCantRead_LocksItsManagedUsers()
    {
        Write(File_, Broken(version: 2));
        using var store = Open();

        Assert.Equal(StoreStatus.ReadOnlyNewerVersion, store.Status);
        Assert.Empty(store.Current.Assignments);
        Assert.Equal([KidId], store.Degraded.Current!.Users.Order());
    }

    [Fact]
    public void AFailedMigration_LocksTheManagedUsersOfTheFileItCouldntUpgrade()
    {
        Write(File_, JsonSerializer.Serialize(Plain(version: 1)));
        using var store = AccessRolesStore.CreateWithMigrations(File_, NullLogger.Instance, 2, [new FailingMigration()]);

        Assert.Equal(StoreStatus.ReadOnlyMigrationFailed, store.Status);
        Assert.Empty(store.Current.Assignments);
        Assert.Equal([KidId], store.Degraded.Current!.Users.Order());
        Assert.Equal(EveryServerEnforced, LockSnapshot.Build(store.Current, store.Status, store.Degraded.Current).For(KidId).Order());
    }

    [Fact]
    public void AMissingFile_WhoseSnapshotsCantBeRestored_ReadsTheNewestSnapshot_ThenAPreUpgradeCopy()
    {
        Directory.CreateDirectory(Snapshots);
        Write(Path.Combine(Snapshots, "accessroles-20261018-031000.json"), Broken(version: 2, managed: TeenId));
        Write(Path.Combine(Snapshots, "accessroles-20261017-031000.json"), Broken(version: 2, managed: OtherId));
        using var store = Open();

        Assert.Equal(StoreStatus.ReadOnlyDataMissing, store.Status);
        var state = store.Degraded.Current!;
        Assert.Equal([TeenId], state.Users.Order());
        Assert.Equal(Path.Combine("snapshots", "accessroles-20261018-031000.json"), state.Source);
    }

    [Fact]
    public void ARecoveredEmptyStore_WithNoEvidence_LocksNobody_AndDoesntPauseTheSync()
    {
        Write(File_, "   ");
        Write(File_ + ".bak", string.Empty);
        using var store = Open();

        Assert.Equal(StoreStatus.RecoveredEmpty, store.Status);
        Assert.Null(store.Degraded.Current);
        Assert.False(File.Exists(store.Degraded.MarkerPath));
        Assert.Null(LibrarySync.PauseReason(new Configuration.PluginConfiguration(), AccessRolesPaths.ForRoot(_directory.Path), store));
        Assert.Empty(LockSnapshot.Build(store.Current, store.Status, store.Degraded.Current).For(KidId));
    }

    [Fact]
    public void AColdStart_RemembersTheLockedUsers_UntilAnAdminConfirms()
    {
        Write(File_, Broken(version: 1));
        Write(File_ + ".bak", "x");
        using (var first = Open())
        {
            Assert.Equal([KidId], first.Degraded.Current!.Users.Order());
        }

        // The admin deletes the broken files: the next start is a fresh, usable store with nobody managed.
        File.Delete(File_);
        File.Delete(File_ + ".bak");
        using (var second = Open())
        {
            Assert.Equal(StoreStatus.Ok, second.Status);
            var state = second.Degraded.Current!;
            Assert.True(state.FromEarlierStart);
            Assert.Equal([KidId], state.Users.Order());
            Assert.Equal(nameof(StoreStatus.ReadOnlyDataUnreadable), state.Because);
            Assert.False(second.Degraded.EnteredNow);
            Assert.True(second.Degraded.CanResolve);
            Assert.Equal(EveryServerEnforced, LockSnapshot.Build(second.Current, second.Status, state).For(KidId).Order());
            Assert.Equal(SyncReasonsNone, LibrarySync.PauseReason(new Configuration.PluginConfiguration(), AccessRolesPaths.ForRoot(_directory.Path), second));

            Assert.Same(state, second.Degraded.Resolve());
            Assert.Null(second.Degraded.Current);
            Assert.False(File.Exists(second.Degraded.MarkerPath));
        }

        using var third = Open();
        Assert.Null(third.Degraded.Current);
    }

    [Fact]
    public void AStartWhoseUsableFileManagesTheLockedUsersAgain_EndsIt()
    {
        Write(File_, Broken(version: 1));
        Write(File_ + ".bak", "x");
        using (var first = Open())
        {
            Assert.NotNull(first.Degraded.Current);
        }

        // A readable copy put back: it manages the kid.
        Write(File_, JsonSerializer.Serialize(Plain(version: 1)));
        using var second = Open();

        Assert.Equal(StoreStatus.Ok, second.Status);
        Assert.Null(second.Degraded.Current);
        Assert.Equal([KidId], second.Degraded.EndedNow!.Order());
        Assert.False(File.Exists(second.Degraded.MarkerPath));
    }

    [Fact]
    public void AMarkerThatCantBeRead_LocksEveryNonAdmin_FailClosed_UntilTheFileHoldsAssignments()
    {
        Write(Path.Combine(_directory.Path, DegradedMode.MarkerFileName), "{ broken");
        using (var store = Open())
        {
            Assert.Equal(StoreStatus.Ok, store.Status);
            Assert.True(store.Degraded.Current!.EveryNonAdmin);
            Assert.True(store.Degraded.CanResolve);
        }

        Write(File_, JsonSerializer.Serialize(Plain(version: 1)));
        using var again = Open();
        Assert.Null(again.Degraded.Current);
    }

    [Fact]
    public void ADegradedUser_KeepsTheirOwnLocksToo_LocksNeverWeaken()
    {
        var kid = User(1, "kid");
        var role = Role(1, "Kid", [AccessRolesLock.DisplayDevice, AccessRolesLock.Controls, AccessRolesLock.Home]);
        var document = Document([role], [Assign(kid, [role])]);
        var state = new DegradedState(nameof(StoreStatus.ReadOnlyNewerVersion), false, new HashSet<Guid> { kid.Id, OtherId }, null, Now, false);

        var snapshot = LockSnapshot.Build(document, StoreStatus.ReadOnlyNewerVersion, state);

        Assert.Equal(EveryServerEnforced.Append(AccessRolesLock.DisplayDevice).Append(AccessRolesLock.Controls).Order(), snapshot.For(kid.Id).Order());
        Assert.Superset(EffectiveRulesResolver.LockedForNonAdministrator(document, document.Assignments[0]).ToHashSet(), snapshot.Reported(kid.Id).ToHashSet());
        Assert.Equal(EveryServerEnforced, snapshot.For(OtherId).Order());
        Assert.Empty(snapshot.For(TeenId));
        Assert.Equal((1, 1), (snapshot.Managed, snapshot.Locked));
    }

    [Theory]
    [InlineData("""{"Assignments":[{"UserId":"11111111111111111111111111111111","State":"Managed"},{"UserId":"22222222-2222-2222-2222-222222222222","State":"Excluded"},{"UserId":"33333333333333333333333333333333","State":"SomethingNewer"}]}""", "1,3", "")]
    [InlineData("""{"assignments":[{"userId":"11111111111111111111111111111111"}]}""", "1", "")]
    [InlineData("""{"Assignments":"broken","Ledger":{"22222222222222222222222222222222":{}}}""", "2", "")]
    [InlineData("""{"Assignments":[{"User":"11111111111111111111111111111111"}],"Ledger":{"22222222222222222222222222222222":{}}}""", "2", "")]
    [InlineData("""{"Assignments":[]}""", "", "")]
    [InlineData("""{"Assignments":[{"UserId":"11111111111111111111111111111111"}],"PreferenceMirror":{"11111111111111111111111111111111":{},"33333333-3333-3333-3333-333333333333":{},"nope":{}}}""", "1", "1,3")]
    [InlineData("""{"Roles":5,"preferenceMirror":{"33333333333333333333333333333333":{}}}""", "3", "3")]
    [InlineData("""{"Roles":[]}""", null, null)]
    [InlineData("""[1,2]""", null, null)]
    [InlineData("""not json""", null, null)]
    public void TheLenientRead_TakesOnlyTheAssignmentsUserIdAndState_ElseTheLedger_ElseTheMirror_AndTheMirrorsRecords(string json, string? expected, string? mirrored)
    {
        var found = DegradedEvidence.Parse(System.Text.Encoding.UTF8.GetBytes(json));
        if (expected is null)
        {
            Assert.Null(found);
            return;
        }

        Assert.Equal(Ids(expected), found!.Users.Order());
        Assert.Equal(Ids(mirrored!), found.Mirrored.Order());
    }

    // A restore from a snapshot (the file and .bak unreadable) or from the .bak (the file alone)
    // used a copy older than the file moved aside. The users that file lists as managed and the copy used doesn't manage stay
    // locked, as an earlier start's marker locks them (resolvable), across a cold start; its mirror records the copy lacks
    // are the evidence the startup takes back.
    [Theory]
    [InlineData(nameof(StoreStatus.RestoredFromSnapshot))]
    [InlineData(nameof(StoreStatus.RecoveredFromBackup))]
    public void ARestore_KeepsLockingTheUsersOnlyTheFileMovedAsideManaged_UntilAnAdminConfirms_AndKeepsItsMirrorRecords(string status)
    {
        var expected = Enum.Parse<StoreStatus>(status);
        var older = JsonSerializer.Serialize(Plain(version: 1));
        if (expected == StoreStatus.RestoredFromSnapshot)
        {
            Write(Path.Combine(Snapshots, "accessroles-20261018-031000.json"), older);
            Write(File_ + ".bak", "x");
        }
        else
        {
            Write(File_ + ".bak", older);
        }

        Write(File_, Later(KidId, OtherId));
        using (var first = Open())
        {
            Assert.Equal(expected, first.Status);
            var state = Assert.IsType<DegradedState>(first.Degraded.Current);
            Assert.Equal([OtherId], state.Users.Order());
            Assert.False(state.EveryNonAdmin);
            Assert.True(state.FromEarlierStart);
            Assert.Equal(expected.ToString(), state.Because);
            Assert.StartsWith("accessroles.json.corrupt-", state.Source, StringComparison.Ordinal);
            Assert.True(first.Degraded.EnteredNow);
            Assert.True(first.Degraded.CanResolve);
            Assert.Equal(new[] { KidId, OtherId }.Order(), first.Degraded.MirrorEvidence.Order());
            var snapshot = LockSnapshot.Build(first.Current, first.Status, state);
            Assert.Equal(EveryServerEnforced, snapshot.For(OtherId).Order());
            Assert.Empty(snapshot.For(TeenId));
        }

        using (var second = Open())
        {
            // A restored snapshot was saved as the file; a .bak used is used again (the file is missing now).
            Assert.Equal(expected == StoreStatus.RestoredFromSnapshot ? StoreStatus.Ok : StoreStatus.RecoveredFromBackup, second.Status);
            var state = second.Degraded.Current!;
            Assert.Equal([OtherId], state.Users.Order());
            Assert.Equal((true, expected.ToString()), (state.FromEarlierStart, state.Because));
            Assert.False(second.Degraded.EnteredNow);
            Assert.Equal(new[] { KidId, OtherId }.Order(), second.Degraded.MirrorEvidence.Order());

            // The startup took the records back: the marker keeps only the users.
            second.Degraded.ForgetMirrorEvidence([KidId, OtherId]);
            Assert.Empty(second.Degraded.MirrorEvidence);
            Assert.Same(state, second.Degraded.Resolve());
            Assert.False(File.Exists(second.Degraded.MarkerPath));
        }

        using var third = Open();
        Assert.Null(third.Degraded.Current);
        Assert.Empty(third.Degraded.MirrorEvidence);
    }

    // A store recovered empty with evidence of managed users ("start empty") can
    // be ended by an admin, and the sync paused for it resumes.
    [Fact]
    public void ARecoveredEmptyStore_WithEvidence_LocksItsUsers_UntilAnAdminConfirmsStartEmpty()
    {
        Write(File_ + ".corrupt-20261001000000", Broken(version: 1));
        Write(File_, "   ");
        Write(File_ + ".bak", string.Empty);
        using var store = Open();
        var paths = AccessRolesPaths.ForRoot(_directory.Path);

        Assert.Equal(StoreStatus.RecoveredEmpty, store.Status);
        var state = store.Degraded.Current!;
        Assert.Equal([KidId], state.Users.Order());
        Assert.False(state.FromEarlierStart);
        Assert.True(store.Degraded.CanResolve);
        Assert.Equal(SyncReasons.StoreDegraded, LibrarySync.PauseReason(new Configuration.PluginConfiguration(), paths, store));

        Assert.Same(state, store.Degraded.Resolve());
        Assert.Null(LibrarySync.PauseReason(new Configuration.PluginConfiguration(), paths, store));
    }

    // N19b: the evidence reads a copy moved aside as unreadable at an earlier start, when the file itself can't be read.
    [Fact]
    public void AnUnreadableFile_ReadsTheManagedUsersFromACopyMovedAsideEarlier()
    {
        Write(File_ + ".corrupt-20261001000000", Broken(version: 1));
        Write(File_, "not json");
        Write(File_ + ".bak", "x");
        using var store = Open();

        Assert.Equal(StoreStatus.ReadOnlyDataUnreadable, store.Status);
        var state = store.Degraded.Current!;
        Assert.False(state.EveryNonAdmin);
        Assert.Equal([KidId], state.Users.Order());
        Assert.Equal("accessroles.json.corrupt-20261001000000", state.Source);
    }

    // The negative side: a restore whose copy manages everyone the file moved aside listed locks nobody and writes no marker.
    [Fact]
    public void ARestoreWhoseCopyManagesEveryoneTheFileListed_LocksNobody()
    {
        Write(File_ + ".bak", JsonSerializer.Serialize(Plain(version: 1)));
        Write(File_, Later(KidId));
        using var store = Open();

        Assert.Equal(StoreStatus.RecoveredFromBackup, store.Status);
        Assert.Null(store.Degraded.Current);
        Assert.False(store.Degraded.EnteredNow);
        Assert.Equal([KidId], store.Degraded.MirrorEvidence);
        Assert.True(File.Exists(store.Degraded.MarkerPath));
        store.Degraded.ForgetMirrorEvidence([KidId]);
        Assert.False(File.Exists(store.Degraded.MarkerPath));
    }

    // A newer version's file whose assignments this build parses is enforced as it is,
    // but users a lenient read names that the parse doesn't manage (a structure renamed) are locked too, fail closed, and an
    // admin can't end it while the file is read-only.
    [Fact]
    public void ANewerVersionsFile_WhoseParseLosesUsersTheLenientReadNames_LocksThem_NotResolvable()
    {
        Write(File_, """{"Version":2,"Assignments":[{"User":"11111111111111111111111111111111","State":"Managed"}],"Ledger":{"11111111111111111111111111111111":{}},"PreferenceMirror":{"11111111111111111111111111111111":{}}}""");
        using var store = Open();

        Assert.Equal(StoreStatus.ReadOnlyNewerVersion, store.Status);
        Assert.Single(store.Current.Assignments);
        var state = Assert.IsType<DegradedState>(store.Degraded.Current);
        Assert.Equal([KidId], state.Users.Order());
        Assert.Equal((nameof(StoreStatus.ReadOnlyNewerVersion), false, false), (state.Because, state.FromEarlierStart, store.Degraded.CanResolve));
        Assert.True(store.Degraded.EnteredNow);

        // The parse holds the mirror's records itself: nothing to take back.
        Assert.True(store.Current.PreferenceMirror.ContainsKey(KidId));
        Assert.Empty(store.Degraded.MirrorEvidence);
    }

    [Fact]
    public void ANewerVersionsFile_WhoseParseManagesEveryoneTheLenientReadNames_IsntDegraded()
    {
        Write(File_, """{"Version":2,"Assignments":[{"UserId":"11111111-1111-1111-1111-111111111111","State":"Managed","UpdatedUtc":"2026-10-19T12:00:00Z"}]}""");
        using var store = Open();

        Assert.Equal(StoreStatus.ReadOnlyNewerVersion, store.Status);
        Assert.Single(store.Current.Assignments);
        Assert.Null(store.Degraded.Current);
        Assert.False(File.Exists(store.Degraded.MarkerPath));
    }

    // The review's degraded-after-resolve: an unreadable file's mirror records go into the marker, so a store started over
    // after it still names the flags the mirror turned off.
    [Fact]
    public void AnUnreadableFilesMirrorRecords_OutliveIt_InTheMarker()
    {
        Write(File_, Later(KidId));
        Write(File_ + ".bak", "x");
        using (var first = Open())
        {
            Assert.Equal(StoreStatus.ReadOnlyDataUnreadable, first.Status);
            Assert.Equal([KidId], first.Degraded.MirrorEvidence);
        }

        File.Delete(File_);
        File.Delete(File_ + ".bak");
        using var second = Open();
        Assert.Equal(StoreStatus.Ok, second.Status);
        Assert.True(second.Degraded.Current!.FromEarlierStart);
        Assert.Equal([KidId], second.Degraded.MirrorEvidence);
        Assert.Same(second.Degraded.Current, second.Degraded.Resolve());

        // Ended, the records stay for the startup to take back.
        Assert.True(File.Exists(second.Degraded.MarkerPath));
        Assert.Equal([KidId], second.Degraded.MirrorEvidence);
    }

    // A marker that can't be read, beside a usable document with assignments, ends it with a warning
    // and an audit reason, never silently.
    [Fact]
    public void AnUnreadableMarker_BesideAFileWithAssignments_EndsItSayingSo()
    {
        Write(Path.Combine(_directory.Path, DegradedMode.MarkerFileName), "{ broken");
        Write(File_, JsonSerializer.Serialize(Plain(version: 1)));
        using var store = Open();

        Assert.Null(store.Degraded.Current);
        Assert.Equal(("MarkerUnreadable", true), (store.Degraded.EndedBecause, store.Degraded.EndedEveryNonAdmin));
        Assert.Empty(store.Degraded.EndedNow!);
        Assert.False(File.Exists(store.Degraded.MarkerPath));
    }

    // The 503: a marker that can't be deleted leaves everything as it was.
    [Fact]
    public void AMarkerThatCantBeDeleted_FailsTheConfirmation_AndChangesNothing()
    {
        Write(Path.Combine(_directory.Path, DegradedMode.MarkerFileName), "{\"Because\":\"ReadOnlyDataUnreadable\",\"Users\":[\"" + KidId.ToString("N") + "\"]}");
        using var store = Open();
        Assert.NotNull(store.Degraded.Current);

        using (new FileStream(store.Degraded.MarkerPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => store.Degraded.Resolve());
        }

        Assert.Equal([KidId], store.Degraded.Current!.Users);
        Assert.True(File.Exists(store.Degraded.MarkerPath));
    }

    public void Dispose() => _directory.Dispose();

    private static string? SyncReasonsNone => null;

    private static List<Guid> Ids(string list)
    {
        var ids = new Dictionary<char, Guid> { ['1'] = KidId, ['2'] = TeenId, ['3'] = OtherId };
        return [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => ids[s[0]]).Order()];
    }

    // A later file than Plain's copy, broken elsewhere: these users managed, and the mirror's records of each.
    private static string Later(params Guid[] managed) => new JsonObject
    {
        ["Version"] = 1,
        ["Roles"] = "corrupt",
        ["Assignments"] = new JsonArray([.. managed.Select(id => (JsonNode)new JsonObject { ["UserId"] = id.ToString("N"), ["State"] = "Managed" })]),
        ["PreferenceMirror"] = new JsonObject(managed.Select(id => KeyValuePair.Create(id.ToString("N"), (JsonNode?)new JsonObject { ["WrittenUtc"] = "2026-10-19T12:00:00Z" })))
    }.ToJsonString();

    private static List<AccessRolesLock> EveryServerEnforced => [.. SettingsSections.ServerEnforced.Concat(SettingsSections.ServerEnforcedSurfaces).Order()];

    // A file whose Assignments a lenient read takes, but whose schema is broken elsewhere (Roles isn't a list).
    private static string Broken(int version, Guid? managed = null) => new JsonObject
    {
        ["Version"] = version,
        ["Roles"] = "corrupt",
        ["Assignments"] = new JsonArray(
            new JsonObject { ["UserId"] = (managed ?? KidId).ToString("N"), ["State"] = "Managed" },
            new JsonObject { ["UserId"] = TeenId.ToString("N"), ["State"] = "Excluded" })
    }.ToJsonString();

    private static object Plain(int version) => new
    {
        Version = version,
        Assignments = new[] { new { UserId = KidId, State = "Managed", UpdatedUtc = Now } }
    };

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private AccessRolesStore Open() => AccessRolesStore.Create(File_, NullLogger.Instance);

    private sealed class FailingMigration : IJsonMigration
    {
        public int FromVersion => 1;

        public void Apply(JsonObject root) => throw new InvalidOperationException("This migration fails (a test).");
    }
}
