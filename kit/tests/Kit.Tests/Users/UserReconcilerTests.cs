using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Jellyfin.Plugin.Kit.Users;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Users;

/// <summary>
/// The reconciler marks unknown ids as orphans and never deletes; its guard trips when more than one id or more than 10
/// % of the records would become orphans at once and then changes nothing; <i>Remap by name</i> dry run and apply; the
/// policy applies only on an explicit deletion (the event, or the admin's action).
/// </summary>
public sealed class UserReconcilerTests : IDisposable
{
    private static readonly Guid Admin = Guid.Parse("11111111-0000-4000-8000-000000000001");
    private static readonly Guid Viewer = Guid.Parse("22222222-0000-4000-8000-000000000002");
    private static readonly Guid Viewer2 = Guid.Parse("33333333-0000-4000-8000-000000000003");
    private static readonly Guid Gone = Guid.Parse("44444444-0000-4000-8000-000000000004");
    private static readonly Guid Gone2 = Guid.Parse("55555555-0000-4000-8000-000000000005");

    private readonly TempDirectory _data = new();
    private readonly ListLogger _logger = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 3, 10, 0, TimeSpan.Zero));
    private readonly Dictionary<Guid, string> _server = new() { [Admin] = "admin", [Viewer] = "viewer", [Viewer2] = "viewer2" };
    private readonly List<IDisposable> _disposables = [];
    private readonly Dictionary<UserReconciler, KitUserMarks> _marksOf = [];
    private DeletedUserPolicy _policy = DeletedUserPolicy.KeepAsOrphan;
    private Func<IReadOnlyDictionary<Guid, string>>? _serverUsers;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string MarksFile => _data.Combine(KitUsersDocument.FileName);

    public void Dispose()
    {
        _disposables.ForEach(d => d.Dispose());
        _data.Dispose();
    }

    [Fact]
    public async Task AnUnknownId_BecomesAnOrphan_AndNothingIsDeleted()
    {
        var owner = new FakeUserDataOwner().Add(Admin, 20, name: "admin").Add(Viewer, 20, name: "viewer").Add(Gone, 2, name: "gone");
        var records = owner.Records.ToList();
        var reconciler = Create(owner);

        var result = await reconciler.ReconcileAsync(Ct);

        Assert.False(result.GuardTripped);
        var orphan = Assert.Single(result.Orphans);
        Assert.Equal(Gone, orphan.UserId);
        Assert.Equal("gone", orphan.LastKnownName);
        Assert.Equal(2, orphan.Records);
        Assert.Equal(OrphanMarkSource.Reconciler, orphan.Mark!.Source);
        Assert.Equal([Gone], result.NewlyOrphaned);
        Assert.True(reconciler.IsOrphan(Gone));
        Assert.False(reconciler.IsOrphan(Admin));
        Assert.Equal(records, owner.Records);
        Assert.Empty(owner.Deletions);
        Assert.Empty(owner.Snapshots);
        Assert.Equal(1, owner.Refreshes);
        Assert.True(File.Exists(MarksFile));

        // The next run finds nothing new, and the mark survives a restart (a new reconciler over the same file).
        var again = await Create(owner).ReconcileAsync(Ct);
        Assert.False(again.GuardTripped);
        Assert.Empty(again.NewlyOrphaned);
        Assert.True(Create(owner).IsOrphan(Gone));
    }

    [Fact]
    public async Task Guard_TwoUnknownIds_ChangesNothing()
    {
        var owner = new FakeUserDataOwner().Add(Admin, 100, name: "admin").Add(Gone, 1, name: "gone").Add(Gone2, 1, name: "gone2");
        var known = owner.KnownUsers;
        var reconciler = Create(owner);

        var result = await reconciler.ReconcileAsync(Ct);

        Assert.True(result.GuardTripped);
        Assert.Equal(2, result.NewlyOrphaned.Count);
        Assert.All(result.Orphans, o => Assert.Null(o.Mark));
        Assert.False(reconciler.IsOrphan(Gone));
        Assert.Equal(0, owner.Refreshes);
        Assert.Same(known, owner.KnownUsers);
        Assert.False(File.Exists(MarksFile));
        Assert.Equal(102, owner.Records.Count);
        Assert.Single(_logger.At(LogLevel.Warning), e => e.Message.Contains("Nothing was changed", StringComparison.Ordinal));

        var described = reconciler.Describe();
        Assert.Equal(true, described["StoredUsersMismatch"]);
        Assert.Equal(UserReconciler.MismatchBanner, described["Banner"]);
        Assert.Equal("2 records belong to 2 unknown users.", described["Message"]);
    }

    [Theory]
    [InlineData(10, 90, false)]
    [InlineData(11, 89, true)]
    public async Task Guard_OneUnknownId_TripsAboveTenPercentOfTheRecords(int unknown, int known, bool trips)
    {
        var owner = new FakeUserDataOwner().Add(Admin, known).Add(Gone, unknown);

        var result = await Create(owner).ReconcileAsync(Ct);

        Assert.Equal(trips, result.GuardTripped);
        Assert.Equal(!trips, File.Exists(MarksFile));
    }

    [Fact]
    public async Task Guard_CountsOnlyIdsNotMarkedBefore_AcrossOwners()
    {
        var first = new FakeUserDataOwner("first").Add(Admin, 50).Add(Gone, 3);
        var second = new FakeUserDataOwner("second").Add(Admin, 40);
        Assert.False((await Create(first, second).ReconcileAsync(Ct)).GuardTripped);

        // A second deleted user shows up later (in another owner): one new id, 2 records of 100.
        second.Add(Gone2, 2);
        var result = await Create(first, second).ReconcileAsync(Ct);

        Assert.False(result.GuardTripped);
        Assert.Equal([Gone2], result.NewlyOrphaned);
        Assert.Equal(2, result.Orphans.Count);
    }

    [Fact]
    public async Task RestoredDataFolder_DeletesNothing_TripsTheGuard_AndRemapByNameRestoresOwnership()
    {
        // Every stored id is from another server; the names are this server's users'.
        var oldAdmin = Guid.NewGuid();
        var oldViewer = Guid.NewGuid();
        var oldViewer2 = Guid.NewGuid();
        var owner = new FakeUserDataOwner().Add(oldAdmin, 8, name: "Admin").Add(oldViewer, 3, name: "viewer").Add(oldViewer2, 1, hasFile: true, name: "viewer2");
        var reconciler = Create(owner);

        var run = await reconciler.ReconcileAsync(Ct);
        Assert.True(run.GuardTripped);
        Assert.Equal(12, owner.Records.Count);
        Assert.Empty(owner.Deletions);

        var dryRun = await reconciler.RemapByNameAsync(dryRun: true, Ct);
        Assert.True(dryRun.DryRun);
        Assert.Equal(
            [(oldAdmin, Admin, "admin", 8), (oldViewer, Viewer, "viewer", 3), (oldViewer2, Viewer2, "viewer2", 1)],
            dryRun.Mapped.Select(m => (m.OldId, m.NewId, m.Name, m.Records)));
        Assert.Empty(dryRun.Unmatched);
        Assert.Empty(owner.Remaps);
        Assert.Empty(owner.Snapshots);

        var applied = await reconciler.RemapByNameAsync(dryRun: false, Ct);

        Assert.Equal(12, applied.MovedRecords);
        Assert.Equal(["fake-remap-0.json"], applied.SafetySnapshots);
        Assert.Equal(8, owner.Records.Count(r => r.UserId == Admin));
        Assert.Equal(3, owner.Records.Count(r => r.UserId == Viewer));
        Assert.Equal("admin", owner.KnownUsers.NameOf(Admin));
        Assert.Null(owner.KnownUsers.NameOf(oldAdmin));
        Assert.Empty(reconciler.FindUnknownUsers());
        Assert.False(reconciler.LastResult!.GuardTripped);
        Assert.Equal(false, reconciler.Describe()["StoredUsersMismatch"]);
    }

    [Fact]
    public async Task RemapByName_LeavesAloneWhatItCantMatchSafely()
    {
        var noName = Guid.NewGuid();
        var nobody = Guid.NewGuid();
        var twinA = Guid.NewGuid();
        var twinB = Guid.NewGuid();
        var owner = new FakeUserDataOwner().Add(Admin, 50, name: "admin").Add(noName, 1).Add(nobody, 1, name: "nobody").Add(twinA, 1, name: "viewer").Add(twinB, 1, name: "VIEWER").Add(Gone, 1, hasFile: true, name: "viewer2");
        var reconciler = Create(owner);

        // viewer2 was deleted here and its clip record kept: a new "viewer2" is someone else.
        _server.Remove(Viewer2);
        _server[Guid.NewGuid()] = "viewer2";
        await reconciler.HandleUserDeletedAsync(Gone, Ct);

        var report = await reconciler.RemapByNameAsync(dryRun: false, Ct);

        Assert.Empty(report.Mapped);
        Assert.Equal(0, report.MovedRecords);
        Assert.Empty(owner.Remaps);
        Assert.Equal(
            new Dictionary<Guid, string>
            {
                [noName] = UserRemapUnmatched.NoKnownName,
                [nobody] = UserRemapUnmatched.NoUserWithThisName,
                [twinA] = UserRemapUnmatched.SameNameTwice,
                [twinB] = UserRemapUnmatched.SameNameTwice,
                [Gone] = UserRemapUnmatched.DeletedOnThisServer
            },
            report.Unmatched.ToDictionary(u => u.OldId, u => u.Reason));
    }

    [Fact]
    public async Task RemapByName_RefusesAReadOnlyStore_ButStillAnswersADryRun()
    {
        var owner = new FakeUserDataOwner().Add(Guid.NewGuid(), 1, name: "admin");
        var readOnly = new FakeUserDataOwner("locked") { ReadOnly = true };
        var reconciler = Create(owner, readOnly);

        Assert.Single((await reconciler.RemapByNameAsync(dryRun: true, Ct)).Mapped);
        var ex = await Assert.ThrowsAsync<PluginException>(() => reconciler.RemapByNameAsync(dryRun: false, Ct));
        Assert.Equal(PluginErrorKind.Conflict, ex.Kind);
        Assert.Empty(owner.Remaps);
    }

    [Theory]
    [InlineData("KeepAsOrphan", true)]
    [InlineData("Anonymize", true)]
    [InlineData("Delete", false)]
    public async Task UserDeleted_AppliesTheSettingsPolicy_AfterASnapshot(string policyName, bool marked)
    {
        var policy = Enum.Parse<DeletedUserPolicy>(policyName);
        _policy = policy;
        var owner = new FakeUserDataOwner().Add(Admin, 5).Add(Viewer, 2, name: "viewer").Add(Viewer, 1, hasFile: true);
        var untouched = new FakeUserDataOwner("other").Add(Admin, 1);
        var reconciler = Create(owner, untouched);
        _server.Remove(Viewer);

        Assert.Equal(3, await reconciler.HandleUserDeletedAsync(Viewer, Ct));

        Assert.Equal([(Viewer, policy)], owner.Deletions);
        Assert.Equal([$"fake-{SnapshotOperations.UserDeleted}-0.json"], owner.Snapshots);
        Assert.Empty(untouched.Snapshots);
        Assert.Empty(untouched.Deletions);
        Assert.True(reconciler.IsOrphan(Viewer));
        Assert.Equal(marked ? 1 : 0, owner.Records.Count(r => r.UserId == Viewer));
        Assert.Equal(marked ? "viewer" : null, owner.KnownUsers.NameOf(Viewer));
        Assert.Equal(5, owner.Records.Count(r => r.UserId == Admin));

        // A kept orphan never trips the guard later, even at more than 10 % of the records.
        var run = await reconciler.ReconcileAsync(Ct);
        Assert.False(run.GuardTripped);
        Assert.Empty(run.NewlyOrphaned);
        Assert.True(reconciler.IsOrphan(Viewer));
    }

    [Fact]
    public async Task UserDeleted_WithoutRecords_TakesNoSnapshotAndWritesNothing()
    {
        var owner = new FakeUserDataOwner().Add(Admin, 1);
        var reconciler = Create(owner);

        Assert.Equal(0, await reconciler.HandleUserDeletedAsync(Viewer, Ct));
        Assert.Equal(0, await reconciler.HandleUserDeletedAsync(Guid.Empty, Ct));

        Assert.Empty(owner.Snapshots);
        Assert.Empty(owner.Deletions);
        Assert.False(reconciler.IsOrphan(Viewer));
        Assert.False(File.Exists(MarksFile));
        Assert.Empty(reconciler.FindUnknownUsers());
    }

    [Fact]
    public async Task ADeletedUsersRecords_ThatComeBack_AreNeverCountedByTheGuardNorRemapped()
    {
        // Deleted with the Delete policy: nothing left, but the id is remembered.
        _policy = DeletedUserPolicy.Delete;
        var owner = new FakeUserDataOwner().Add(Admin, 5).Add(Viewer, 5, name: "viewer").Add(Viewer2, 5, name: "viewer2");
        var reconciler = Create(owner);
        _server.Remove(Viewer);
        _server.Remove(Viewer2);
        await reconciler.HandleUserDeletedAsync(Viewer, Ct);
        await reconciler.HandleUserDeletedAsync(Viewer2, Ct);
        Assert.Equal(5, owner.Records.Count);

        // An older snapshot is restored: their records are back, and new users with the same names exist.
        owner.Add(Viewer, 5, name: "viewer").Add(Viewer2, 5, name: "viewer2");
        _server[Guid.NewGuid()] = "viewer";
        _server[Guid.NewGuid()] = "viewer2";
        var run = await reconciler.ReconcileAsync(Ct);
        var remap = await reconciler.RemapByNameAsync(dryRun: false, Ct);

        Assert.False(run.GuardTripped);
        Assert.Equal(2, run.Orphans.Count);
        Assert.All(run.Orphans, o => Assert.Equal(OrphanMarkSource.Deleted, o.Mark!.Source));
        Assert.Empty(remap.Mapped);
        Assert.All(remap.Unmatched, u => Assert.Equal(UserRemapUnmatched.DeletedOnThisServer, u.Reason));
        Assert.Empty(owner.Remaps);
    }

    [Fact]
    public async Task UserDeleted_SkipsAReadOnlyOwner_AndAFailingOwnerDoesntStopTheOthers()
    {
        var readOnly = new FakeUserDataOwner("locked") { ReadOnly = true }.Add(Viewer, 2);
        var failing = new FakeUserDataOwner("failing") { FailWith = new InvalidOperationException("boom") }.Add(Viewer, 2);
        var working = new FakeUserDataOwner("working").Add(Viewer, 2).Add(Admin, 50);
        var reconciler = Create(readOnly, failing, working);

        // Jellyfin removes the user before it reports the deletion (since User Ratings 1.0.1 the retry below is only for ids
        // the server doesn't know).
        _server.Remove(Viewer);
        Assert.Equal(2, await reconciler.HandleUserDeletedAsync(Viewer, Ct));

        Assert.Empty(readOnly.Deletions);
        Assert.DoesNotContain(working.Records, r => r.UserId == Viewer);
        Assert.Single(_logger.At(LogLevel.Warning), e => e.Message.Contains("locked", StringComparison.Ordinal));
        Assert.Single(_logger.At(LogLevel.Error), e => e.Message.Contains("failing", StringComparison.Ordinal));

        // The failing owner still holds the user's records, so the id is marked for the admin.
        Assert.True(reconciler.IsOrphan(Viewer));

        // Its policy is pending, the read-only owner's isn't (left for the admin, as before), and the next
        // reconciliation applies it once the owner works again.
        Assert.Equal(["failing"], Mark(reconciler, Viewer).PolicyPending);
        failing.FailWith = null;
        await reconciler.ReconcileAsync(Ct);
        Assert.DoesNotContain(failing.Records, r => r.UserId == Viewer);
        Assert.Null(Mark(reconciler, Viewer).PolicyPending);
        Assert.Empty(readOnly.Deletions);
    }

    [Fact]
    public async Task UserDeleted_WhileAnOwnerCantCountItsRecords_IsPending_AndEachReconciliationAppliesTheSettingAgainUntilItCan()
    {
        // User Ratings refuses Delete while its archive can't be read, and can't count then.
        var ratings = new FakeUserDataOwner("ratings") { CountFailsWith = new PluginException(PluginErrorKind.Unavailable, "the archive can't be read") }.Add(Admin, 50).Add(Viewer, 3);
        var reconciler = Create(ratings);
        _policy = DeletedUserPolicy.KeepAsOrphan;
        _server.Remove(Viewer);

        Assert.Equal(0, await reconciler.HandleUserDeletedAsync(Viewer, Ct));

        var mark = Mark(reconciler, Viewer);
        Assert.Equal(OrphanMarkSource.Deleted, mark.Source);
        Assert.Equal(["ratings"], mark.PolicyPending);
        Assert.Equal(1, reconciler.Describe()["PolicyPending"]);
        Assert.Empty(ratings.Deletions);
        Assert.Contains(_logger.At(LogLevel.Error), e => e.Message.Contains("applied again at the next reconciliation", StringComparison.Ordinal));

        // Still failing: still pending, and it survives a restart (a new reconciler over the same marks file).
        await reconciler.ReconcileAsync(Ct);
        Assert.Equal(["ratings"], Mark(reconciler, Viewer).PolicyPending);
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.Contains("still can't be applied", StringComparison.Ordinal));
        reconciler = Create(ratings);
        Assert.Equal(1, reconciler.Describe()["PolicyPending"]);

        // The owner works again, and the setting now says Delete: the policy applied is the setting as it is then, after a
        // pre-operation snapshot.
        ratings.CountFailsWith = null;
        _policy = DeletedUserPolicy.Delete;
        await reconciler.ReconcileAsync(Ct);

        Assert.Equal([(Viewer, DeletedUserPolicy.Delete)], ratings.Deletions);
        Assert.DoesNotContain(ratings.Records, r => r.UserId == Viewer);
        Assert.Equal(["ratings-" + SnapshotOperations.UserDeleted + "-0.json"], ratings.Snapshots);
        Assert.Null(Mark(reconciler, Viewer).PolicyPending);
        Assert.Equal(0, reconciler.Describe()["PolicyPending"]);
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains("was applied now", StringComparison.Ordinal));

        // Nothing pending: the next run applies nothing again.
        await reconciler.ReconcileAsync(Ct);
        Assert.Single(ratings.Deletions);
    }

    [Fact]
    public async Task APendingPolicy_ForAUserWhoIsBack_NeverTouchesTheirRecords_AndTheirMarkGoes()
    {
        // Regression (User Ratings 1.0.1): the policy fails when the user is deleted, then Jellyfin's database is
        // restored with the user in it (the same id). The retry used to apply the policy whenever the owner still held
        // records of the id, so the next reconciliation deleted that current user's ratings.
        _policy = DeletedUserPolicy.Delete;
        var ratings = new FakeUserDataOwner("ratings") { CountFailsWith = new PluginException(PluginErrorKind.Unavailable, "the archive can't be read") }.Add(Admin, 50).Add(Viewer, 3, name: "viewer");
        var other = new FakeUserDataOwner("other") { FailWith = new InvalidOperationException("boom") }.Add(Viewer, 2, name: "viewer");
        var reconciler = Create(ratings, other);
        _server.Remove(Viewer);
        await reconciler.HandleUserDeletedAsync(Viewer, Ct);
        Assert.Equal(["other", "ratings"], Mark(reconciler, Viewer).PolicyPending);
        var snapshotsBefore = ratings.Snapshots.Concat(other.Snapshots).ToList();

        // The user is back, and both owners work again.
        _server[Viewer] = "viewer";
        ratings.CountFailsWith = null;
        other.FailWith = null;
        var run = await reconciler.ReconcileAsync(Ct);

        Assert.Empty(ratings.Deletions);
        Assert.Empty(other.Deletions);
        Assert.Equal(snapshotsBefore, ratings.Snapshots.Concat(other.Snapshots).ToList());
        Assert.Equal(3, ratings.Records.Count(r => r.UserId == Viewer));
        Assert.Equal(2, other.Records.Count(r => r.UserId == Viewer));
        Assert.False(reconciler.IsOrphan(Viewer));
        Assert.DoesNotContain(Viewer, reconciler.DeletedUserIds);
        Assert.Equal(0, reconciler.Describe()["PolicyPending"]);
        Assert.Empty(run.Orphans);
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.Contains("is a user of this server again", StringComparison.Ordinal) && e.Message.Contains(Viewer.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_logger.At(LogLevel.Information), e => e.Message.Contains("was applied now", StringComparison.Ordinal));

        // The mark is gone from the file too: after a restart nothing is pending, and the policy never comes back.
        reconciler = Create(ratings, other);
        Assert.Equal(0, reconciler.Describe()["PolicyPending"]);
        await reconciler.ReconcileAsync(Ct);
        Assert.Empty(ratings.Deletions);
        Assert.Empty(other.Deletions);
    }

    [Fact]
    public async Task APendingPolicy_IsntApplied_WhileTheServersUserListCantBeReadOrIsEmpty()
    {
        // Fail closed: without the user list a user who is back can't be told from one who is gone.
        _policy = DeletedUserPolicy.Delete;
        var ratings = new FakeUserDataOwner("ratings") { CountFailsWith = new PluginException(PluginErrorKind.Unavailable, "the archive can't be read") }.Add(Admin, 50).Add(Viewer, 3);
        IReadOnlyDictionary<Guid, string>? list = _server;
        _serverUsers = () => list ?? throw new InvalidOperationException("LDAP down");
        var reconciler = Create(ratings);
        _server.Remove(Viewer);
        await reconciler.HandleUserDeletedAsync(Viewer, Ct);
        ratings.CountFailsWith = null;

        // Unreadable: the retry applies nothing (the run itself then fails on the list, as it always has).
        list = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => reconciler.ReconcileAsync(Ct));
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.Contains("user list couldn't be read", StringComparison.Ordinal));

        // Empty: the retry applies nothing, and the run's guard changes nothing.
        list = new Dictionary<Guid, string>();
        await reconciler.ReconcileAsync(Ct);
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.Contains("user list is empty", StringComparison.Ordinal));

        Assert.Empty(ratings.Deletions);
        Assert.Empty(ratings.Snapshots);
        Assert.Equal(3, ratings.Records.Count(r => r.UserId == Viewer));
        Assert.Equal(["ratings"], Mark(reconciler, Viewer).PolicyPending);

        // The list is back, the user still gone: the policy is applied now, after its snapshot.
        list = _server;
        await reconciler.ReconcileAsync(Ct);
        Assert.Equal([(Viewer, DeletedUserPolicy.Delete)], ratings.Deletions);
        Assert.DoesNotContain(ratings.Records, r => r.UserId == Viewer);
        Assert.Equal(["ratings-" + SnapshotOperations.UserDeleted + "-0.json"], ratings.Snapshots);
        Assert.Null(Mark(reconciler, Viewer).PolicyPending);
        Assert.Contains(Viewer, reconciler.DeletedUserIds);
    }

    [Fact]
    public async Task ADeletedUsersMark_GoesOnceTheServerKnowsTheirIdAgain()
    {
        // KeepAsOrphan kept the records under a Deleted mark; Jellyfin's database is restored with one of the users in it.
        var owner = new FakeUserDataOwner().Add(Admin, 50).Add(Viewer, 2, hasFile: true, name: "viewer").Add(Viewer2, 1, hasFile: true, name: "viewer2");
        var reconciler = Create(owner);
        _server.Remove(Viewer);
        _server.Remove(Viewer2);
        await reconciler.HandleUserDeletedAsync(Viewer, Ct);
        await reconciler.HandleUserDeletedAsync(Viewer2, Ct);
        await reconciler.ReconcileAsync(Ct);
        Assert.True(reconciler.IsOrphan(Viewer) && reconciler.IsOrphan(Viewer2));

        _server[Viewer] = "viewer";
        await reconciler.ReconcileAsync(Ct);

        Assert.False(reconciler.IsOrphan(Viewer));
        Assert.Equal([Viewer2], reconciler.DeletedUserIds);
        Assert.Equal(2, owner.Records.Count(r => r.UserId == Viewer));
        Assert.Equal(DeletedUserPolicy.KeepAsOrphan, Assert.Single(owner.Deletions, d => d.UserId == Viewer).Policy);
    }

    [Fact]
    public async Task ThePolicy_IsReadEachTime_AndAnUnreadableSettingKeepsTheRecords()
    {
        var owner = new FakeUserDataOwner().Add(Viewer, 1).Add(Viewer2, 1);
        var calls = 0;
        using var marks = new KitUserMarks(MarksFile, _logger, _clock);
        using var reconciler = new UserReconciler(
            () => _server,
            [owner],
            marks,
            () => ++calls == 1 ? DeletedUserPolicy.Delete : throw new InvalidOperationException("no settings"),
            _logger,
            _clock);

        await reconciler.HandleUserDeletedAsync(Viewer, Ct);
        await reconciler.HandleUserDeletedAsync(Viewer2, Ct);

        Assert.Equal([(Viewer, DeletedUserPolicy.Delete), (Viewer2, DeletedUserPolicy.KeepAsOrphan)], owner.Deletions);
    }

    [Fact]
    public async Task ApplyPolicy_OnListedOrphans_AfterASnapshot()
    {
        _policy = DeletedUserPolicy.Delete;
        var owner = new FakeUserDataOwner().Add(Admin, 2).Add(Gone, 3, name: "gone").Add(Gone2, 1, hasFile: true, name: "gone2");
        var reconciler = Create(owner);
        Assert.True((await reconciler.ReconcileAsync(Ct)).GuardTripped);

        var report = await reconciler.ApplyPolicyAsync([Gone, Gone2, Gone], Ct);

        Assert.Equal(DeletedUserPolicy.Delete, report.Policy);
        Assert.Equal(4, report.Affected);
        Assert.Equal(3, report.Users[Gone]);
        Assert.Equal(["fake-apply-policy-0.json"], report.SafetySnapshots);
        Assert.Equal(2, owner.Records.Count);
        Assert.Empty(reconciler.FindUnknownUsers());
        Assert.False(reconciler.LastResult!.GuardTripped);
    }

    [Fact]
    public async Task ApplyPolicy_RefusesCurrentUsersEmptyIdsAndReadOnlyStores()
    {
        var owner = new FakeUserDataOwner().Add(Gone, 1);
        var reconciler = Create(owner);

        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<PluginException>(() => reconciler.ApplyPolicyAsync([], Ct))).Kind);
        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<PluginException>(() => reconciler.ApplyPolicyAsync([Guid.Empty], Ct))).Kind);
        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<PluginException>(() => reconciler.ApplyPolicyAsync(Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()).ToList(), Ct))).Kind);
        var current = await Assert.ThrowsAsync<PluginException>(() => reconciler.ApplyPolicyAsync([Gone, Viewer], Ct));
        Assert.Equal(PluginErrorKind.Invalid, current.Kind);
        Assert.Contains(KnownUsers.FormatId(Viewer), current.Message, StringComparison.Ordinal);
        Assert.Empty(owner.Deletions);

        var locked = Create(owner, new FakeUserDataOwner("locked") { ReadOnly = true });
        Assert.Equal(PluginErrorKind.Conflict, (await Assert.ThrowsAsync<PluginException>(() => locked.ApplyPolicyAsync([Gone], Ct))).Kind);
        Assert.Empty(owner.Deletions);
    }

    [Fact]
    public async Task ApplyPolicy_WithoutTheServersUserList_Is503_AndWritesNothing()
    {
        // An empty list (a directory service down) or one that can't be read would let a current user's id through the
        // current-user check.
        _policy = DeletedUserPolicy.Delete;
        var owner = new FakeUserDataOwner().Add(Viewer, 2);
        foreach (var server in new Func<IReadOnlyDictionary<Guid, string>>[] { () => new Dictionary<Guid, string>(), () => throw new InvalidOperationException("LDAP down") })
        {
            _serverUsers = server;
            var ex = await Assert.ThrowsAsync<PluginException>(() => Create(owner).ApplyPolicyAsync([Viewer], Ct));
            Assert.Equal((PluginErrorKind.Unavailable, UserReconciler.UserListUnavailableMessage), (ex.Kind, ex.Message));
        }

        Assert.Empty(owner.Deletions);
        Assert.Empty(owner.Snapshots);
        Assert.False(File.Exists(MarksFile));
    }

    [Fact]
    public async Task ApplyPolicy_TakesASnapshotOnlyWhereThePolicyWouldChangeRecords_AndStillAppliesIt()
    {
        // KeepAsOrphan applied again changes nothing, so it takes no snapshot; an owner that doesn't say (the default)
        // is snapshotted as before.
        var quiet = new FakeUserDataOwner("quiet") { WouldChange = (_, policy) => policy != DeletedUserPolicy.KeepAsOrphan }.Add(Gone, 1, hasFile: true);
        var plain = new FakeUserDataOwner("plain").Add(Gone, 1, hasFile: true);
        var reconciler = Create(quiet, plain);

        var report = await reconciler.ApplyPolicyAsync([Gone], Ct);

        Assert.Empty(quiet.Snapshots);
        Assert.Equal([(Gone, DeletedUserPolicy.KeepAsOrphan)], quiet.Deletions);
        Assert.Equal(["plain-apply-policy-0.json"], report.SafetySnapshots);
        Assert.Equal(OrphanMarkSource.PolicyApplied, reconciler.FindUnknownUsers().Single().Mark!.Source);
    }

    [Fact]
    public async Task ApplyPolicy_ThatFailsPartWay_MarksOnlyTheIdsEveryOwnerHoldingThemFinished()
    {
        // The second owner fails (or turns read-only) after the first finished both ids; Gone2, which only the first
        // holds, is applied and marked; Gone, which the second also holds, isn't marked.
        _policy = DeletedUserPolicy.KeepAsOrphan;
        foreach (var readOnly in new[] { false, true })
        {
            File.Delete(MarksFile);
            var second = new FakeUserDataOwner("second").Add(Gone, 1, hasFile: true);
            var first = new FakeUserDataOwner("first").Add(Gone, 1, hasFile: true).Add(Gone2, 1, hasFile: true);
            if (readOnly)
            {
                first.AfterDeletion = () => second.ReadOnly = true;
            }
            else
            {
                second.FailWith = new PluginException(PluginErrorKind.Unavailable, "the store went read-only");
            }

            var reconciler = Create(first, second);
            var ex = await Assert.ThrowsAsync<PluginException>(() => reconciler.ApplyPolicyAsync([Gone, Gone2], Ct));

            Assert.Equal(readOnly ? PluginErrorKind.Conflict : PluginErrorKind.Unavailable, ex.Kind);
            Assert.Equal([(Gone, DeletedUserPolicy.KeepAsOrphan), (Gone2, DeletedUserPolicy.KeepAsOrphan)], first.Deletions);
            Assert.True(reconciler.IsOrphan(Gone2));
            Assert.False(reconciler.IsOrphan(Gone));
            Assert.Equal([Gone2], reconciler.DeletedUserIds.ToList());
        }
    }

    [Fact]
    public async Task RemapByName_BoundToItsDryRun_NeedsItsPlanToken_AndRefusesAChangedPlan()
    {
        // Opt-in: the real run maps only what the dry run the admin checked showed.
        var owner = new FakeUserDataOwner().Add(Admin, 30, name: "admin").Add(Gone, 3, name: "viewer");
        var reconciler = Create(owner);
        await reconciler.ReconcileAsync(Ct);
        var dry = await reconciler.RemapByNameAsync(dryRun: true, ifPlan: null, Ct);
        Assert.Matches("^p1-[0-9a-f]{20}$", dry.PlanToken);
        Assert.Equal(dry.PlanToken, dry.ToDictionary()["PlanToken"]);
        Assert.Equal(UserReconciler.PlanTokenOf(dry.Mapped), (await reconciler.RemapByNameAsync(dryRun: true, ifPlan: "anything", Ct)).PlanToken);

        // The unbound overload (Bookmarks) answers as before: no token.
        Assert.False((await reconciler.RemapByNameAsync(dryRun: true, Ct)).ToDictionary().ContainsKey("PlanToken"));

        var missing = await Assert.ThrowsAsync<PluginException>(() => reconciler.RemapByNameAsync(dryRun: false, ifPlan: " ", Ct));
        Assert.Equal((PluginErrorKind.Invalid, UserReconciler.PlanRequiredMessage), (missing.Kind, missing.Message));

        // Meanwhile another record of the old id turns up: the plan's record count changed.
        owner.Add(Gone, 1);
        var changed = await Assert.ThrowsAsync<PluginException>(() => reconciler.RemapByNameAsync(dryRun: false, ifPlan: dry.PlanToken, Ct));
        Assert.Equal((PluginErrorKind.Conflict, UserReconciler.PlanChangedMessage), (changed.Kind, changed.Message));
        Assert.Empty(owner.Remaps);
        Assert.Empty(owner.Snapshots);

        // A conflict that turned up since the dry run (the new id rated one of the moved items meanwhile, so User
        // Ratings would detach one as Ambiguous) changes the plan too, the record counts unchanged.
        var again = await reconciler.RemapByNameAsync(dryRun: true, ifPlan: null, Ct);
        owner.Conflicts = (oldId, newId) => oldId == Gone && newId == Viewer ? 1 : 0;
        var conflict = await Assert.ThrowsAsync<PluginException>(() => reconciler.RemapByNameAsync(dryRun: false, ifPlan: again.PlanToken, Ct));
        Assert.Equal((PluginErrorKind.Conflict, UserReconciler.PlanChangedMessage), (conflict.Kind, conflict.Message));
        Assert.Empty(owner.Remaps);

        // The token is compared in any case: an admin (or a script) may upper-case it.
        again = await reconciler.RemapByNameAsync(dryRun: true, ifPlan: null, Ct);
        Assert.NotEqual(UserReconciler.PlanTokenOf(again.Mapped), again.PlanToken);
        var applied = await reconciler.RemapByNameAsync(dryRun: false, ifPlan: again.PlanToken!.ToUpperInvariant(), Ct);
        Assert.Equal((4, again.PlanToken), (applied.MovedRecords, applied.PlanToken));
        Assert.Equal(34, owner.Records.Count(r => r.UserId == Viewer || r.UserId == Admin));
    }

    [Fact]
    public async Task ReadOnlyOwners_AreLeftOutOfTheCounts()
    {
        var readOnly = new FakeUserDataOwner("locked") { ReadOnly = true }.Add(Gone, 1).Add(Gone2, 1);
        var owner = new FakeUserDataOwner().Add(Admin, 10);

        var result = await Create(owner, readOnly).ReconcileAsync(Ct);

        Assert.False(result.GuardTripped);
        Assert.Empty(result.Orphans);
        Assert.Equal(["locked"], result.SkippedOwners);
        Assert.Equal(0, readOnly.Refreshes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarksOfIdsOnlyASkippedOwnerHolds_StayUntilItIsBack_SoTheGuardDoesntTripThen(bool unreadable)
    {
        // Gone and Gone2 are orphans the reconciler marked in two runs, held only by the second owner. That owner
        // is left out of one run (a newer or failed upgrade during a downgrade, or a locked file) and then comes back.
        var first = new FakeUserDataOwner("first").Add(Admin, 50);
        var second = new FakeUserDataOwner("second").Add(Admin, 10).Add(Gone, 2, name: "gone");
        var reconciler = Create(first, second);
        Assert.False((await reconciler.ReconcileAsync(Ct)).GuardTripped);
        second.Add(Gone2, 2, name: "gone2");
        Assert.False((await reconciler.ReconcileAsync(Ct)).GuardTripped);
        Assert.True(reconciler.IsOrphan(Gone) && reconciler.IsOrphan(Gone2));

        // Left out: nothing counted, and the marks stay (the server knowing Viewer2 again is the only change allowed).
        if (unreadable)
        {
            second.Unreadable = true;
        }
        else
        {
            second.ReadOnly = true;
        }

        var skipped = await reconciler.ReconcileAsync(Ct);
        Assert.Equal(["second"], skipped.SkippedOwners);
        Assert.Empty(skipped.Orphans);
        Assert.True(reconciler.IsOrphan(Gone), "A skipped owner's orphan lost its mark.");
        Assert.True(reconciler.IsOrphan(Gone2));

        // Back: no new orphans, no banner.
        second.Unreadable = false;
        second.ReadOnly = false;
        var back = await reconciler.ReconcileAsync(Ct);

        Assert.False(back.GuardTripped);
        Assert.Empty(back.NewlyOrphaned);
        Assert.Equal(2, back.Orphans.Count);
        Assert.Null(reconciler.Describe()["Banner"]);
    }

    [Fact]
    public async Task WhileAnOwnerIsSkipped_AMarkStillGoes_WhenTheServerKnowsTheIdAgain()
    {
        var owner = new FakeUserDataOwner().Add(Admin, 50);
        var locked = new FakeUserDataOwner("locked").Add(Gone, 1);
        var reconciler = Create(owner, locked);
        await reconciler.ReconcileAsync(Ct);
        locked.Add(Gone2, 1);
        await reconciler.ReconcileAsync(Ct);
        locked.ReadOnly = true;

        // The account of Gone is back (Jellyfin's database restored from a backup) while the locked owner is left out.
        _server[Gone] = "gone";
        var run = await reconciler.ReconcileAsync(Ct);

        Assert.Equal(["locked"], run.SkippedOwners);
        Assert.False(reconciler.IsOrphan(Gone));
        Assert.True(reconciler.IsOrphan(Gone2));
    }

    [Fact]
    public async Task DeletedUserIds_AreTheUsersDeletedHere_NotTheReconcilersOrphans()
    {
        var owner = new FakeUserDataOwner().Add(Admin, 50).Add(Viewer, 1, hasFile: true).Add(Gone, 1);
        var reconciler = Create(owner);
        await reconciler.ReconcileAsync(Ct);
        _server.Remove(Viewer);
        await reconciler.HandleUserDeletedAsync(Viewer, Ct);

        Assert.True(reconciler.IsOrphan(Gone));
        Assert.Equal([Viewer], reconciler.DeletedUserIds);
    }

    [Fact]
    public async Task AnUnreadableOwner_IsLeftOut_AndTheOthersStillWork()
    {
        var unreadable = new FakeUserDataOwner("locked file") { Unreadable = true }.Add(Gone, 5).Add(Viewer, 1);
        var owner = new FakeUserDataOwner().Add(Admin, 10).Add(Viewer, 2);
        var reconciler = Create(unreadable, owner);

        var result = await reconciler.ReconcileAsync(Ct);
        var described = reconciler.Describe();
        _server.Remove(Viewer);

        Assert.Equal(["locked file"], result.SkippedOwners);
        Assert.Empty(result.Orphans);
        Assert.Equal(new[] { "locked file" }, described["SkippedOwners"]);
        Assert.Equal(2, await reconciler.HandleUserDeletedAsync(Viewer, Ct));
        Assert.Single(_logger.At(LogLevel.Error), e => e.Message.Contains("locked file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Describe_ListsUnknownUsersWithNames_ForAdmins()
    {
        var owner = new FakeUserDataOwner().Add(Admin, 30, name: "admin").Add(Gone, 2, name: "gone");
        var reconciler = Create(owner);

        var before = reconciler.Describe();
        Assert.Null(before["LastReconciledUtc"]);
        await reconciler.ReconcileAsync(Ct);
        var after = reconciler.Describe();

        Assert.Equal("KeepAsOrphan", after["DeletedUserPolicy"]);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, after["LastReconciledUtc"]);
        Assert.Equal(3, after["ServerUsers"]);
        Assert.Equal(2, after["StoredUsers"]);
        Assert.Equal(32, after["Records"]);
        var orphan = Assert.Single(Assert.IsAssignableFrom<IEnumerable<IDictionary<string, object?>>>(after["OrphanUsers"]));
        Assert.Equal(KnownUsers.FormatId(Gone), orphan["UserId"]);
        Assert.Equal("gone", orphan["LastKnownName"]);
        Assert.Equal(2, orphan["Records"]);
        Assert.Equal(true, orphan["Marked"]);
        Assert.Equal("Reconciler", orphan["Source"]);
        Assert.Null(after["Banner"]);
        Assert.Equal("2 records belong to 1 unknown users.", after["Message"]);
    }

    [Fact]
    public async Task Marks_ThatCantBeSaved_AreKeptInMemory()
    {
        // A newer kit wrote the file: the marks store is read-only.
        await File.WriteAllTextAsync(MarksFile, "{ \"Version\": 99, \"Orphans\": [] }", Ct);
        var owner = new FakeUserDataOwner().Add(Admin, 30).Add(Gone, 1);
        var reconciler = Create(owner);

        await reconciler.ReconcileAsync(Ct);

        Assert.True(reconciler.IsOrphan(Gone));
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.Contains("kept in memory", StringComparison.Ordinal));
        Assert.Equal("{ \"Version\": 99, \"Orphans\": [] }", await File.ReadAllTextAsync(MarksFile, Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnreadableMarks_RefuseRemapByName_SayWhatToDo_LogOnceWithoutAStackTrace_AndAnAdminCanStartThemEmpty(bool recoveryPage)
    {
        // The JSON store hardening: an unreadable kit-users.json left its store read-only (the hardening), so the list
        // of users deleted here was empty: Remap by name could give a deleted user's records to a new user of the same
        // name. Diagnostics said nothing, the marks were kept in memory with a Warning and its stack trace, and the
        // only way out was stopping the server. viewer2 was deleted here while holding a clip record; then the file
        // broke, and a new "viewer2" was made.
        var owner = new FakeUserDataOwner().Add(Admin, 50, name: "admin").Add(Gone, 1, hasFile: true, name: "viewer2");
        _server.Remove(Viewer2);
        _server[Guid.NewGuid()] = "viewer2";
        await Create(owner).HandleUserDeletedAsync(Gone, Ct);
        await File.WriteAllTextAsync(MarksFile, "{ not json", Ct);
        var logger = new ListLogger();
        var marks = new KitUserMarks(MarksFile, logger, _clock, recoveryPage);
        var reconciler = new UserReconciler(() => _server, [owner], marks, () => _policy, logger, _clock);
        _disposables.Add(marks);
        _disposables.Add(reconciler);

        var described = Assert.IsAssignableFrom<IDictionary<string, object?>>(reconciler.Describe()["Marks"]);
        Assert.Equal(MarksFile, described["File"]);
        Assert.Equal(nameof(StoreStatus.ReadOnlyDataUnreadable), described["Status"]);
        Assert.Equal(true, described["ReadOnly"]);
        Assert.Equal(recoveryPage, described["Recoverable"]);
        var message = Assert.IsType<string>(described["Message"]);
        Assert.StartsWith("The data file couldn't be read and there was no backup. There is no snapshot of it.", message, StringComparison.Ordinal);
        Assert.Contains(recoveryPage ? "To go on, start empty on the plugin's settings page" : "To go on, stop the server, put a readable copy of kit-users.json back", message, StringComparison.Ordinal);

        // Remap by name fails closed, a dry run too, and says what to do.
        foreach (var dryRun in new[] { true, false })
        {
            var refused = await Assert.ThrowsAsync<PluginException>(() => reconciler.RemapByNameAsync(dryRun, Ct));
            Assert.Equal(PluginErrorKind.Conflict, refused.Kind);
            Assert.StartsWith("Remap by name is refused: the list of users deleted on this server (kit-users.json) can't be read, so a deleted user's records could go to a new user of the same name. Nothing was changed.", refused.Message, StringComparison.Ordinal);
            Assert.EndsWith(recoveryPage ? "on the plugin's settings page (Data tab) first, or put a readable copy of the file back with the server stopped." : "or move it aside, with the server stopped first.", refused.Message, StringComparison.Ordinal);
        }

        Assert.Throws<PluginException>(() => reconciler.ThrowIfDeletedUsersUnknown("An import that maps users by name"));
        Assert.Empty(owner.Remaps);

        // The marks made meanwhile are kept in memory: one Warning, without a stack trace, worded by the page.
        var later = Guid.NewGuid();
        owner.Add(later, 1, hasFile: true, name: "later");
        await reconciler.HandleUserDeletedAsync(later, Ct);
        await reconciler.ReconcileAsync(Ct);
        await reconciler.ReconcileAsync(Ct);
        var warning = Assert.Single(logger.At(LogLevel.Warning), e => e.Message.Contains("orphan marks", StringComparison.Ordinal));
        Assert.Null(warning.Exception);
        Assert.Contains(recoveryPage ? "An admin can start the list empty on the plugin's settings page (Data tab)" : "Put a readable copy of the file back, or move it aside, with the server stopped.", warning.Message, StringComparison.Ordinal);
        Assert.NotNull(Assert.IsAssignableFrom<IDictionary<string, object?>>(reconciler.Describe()["Marks"])["MarksInMemory"]);
        Assert.Equal("{ not json", await File.ReadAllTextAsync(MarksFile, Ct));

        // Start empty: what couldn't be read is kept, the marks of this process are saved, the reconciler runs at once, and
        // the file can be written again. Gone's mark is lost with the old file, as the page says: it is an unknown user now.
        var result = await reconciler.StartMarksEmptyAsync(Ct);
        Assert.Equal(true, result["StartedEmpty"]);
        var corrupt = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<string>>(result["MovedAside"]));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(_data.Combine(corrupt), Ct));
        Assert.True((int)result["MarksKept"]! >= 1);
        Assert.False(marks.IsReadOnly);
        Assert.Contains(later, reconciler.DeletedUserIds);
        Assert.True(reconciler.IsOrphan(Gone));
        Assert.Equal(OrphanMarkSource.Reconciler, marks.Current[Gone].Source);
        Assert.Null(Assert.IsAssignableFrom<IDictionary<string, object?>>(reconciler.Describe()["Marks"])["MarksInMemory"]);
        Assert.Single((await reconciler.RemapByNameAsync(dryRun: true, Ct)).Mapped);

        using var restarted = new KitUserMarks(MarksFile, logger, _clock, recoveryPage);
        Assert.Equal(StoreStatus.Ok, restarted.Store.Status);
        Assert.Contains(later, restarted.DeletedIds);
    }

    private OrphanMark Mark(UserReconciler reconciler, Guid userId) => _marksOf[reconciler].Current[userId];

    private UserReconciler Create(params IUserDataOwner[] owners)
    {
        var marks = new KitUserMarks(MarksFile, _logger, _clock);
        var reconciler = new UserReconciler(_serverUsers ?? (() => _server), owners, marks, () => _policy, _logger, _clock);
        _marksOf[reconciler] = marks;
        _disposables.Add(marks);
        _disposables.Add(reconciler);
        return reconciler;
    }
}
