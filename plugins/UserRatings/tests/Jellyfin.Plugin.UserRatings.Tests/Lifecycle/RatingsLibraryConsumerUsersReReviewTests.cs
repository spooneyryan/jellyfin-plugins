using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Services;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Lifecycle;

/// <summary>
/// The further fixes of 0.3.14.0, from their scratch tests and the decisions on them:
/// no byte of a deleted review stays in the database, its <c>-wal</c> or the daily snapshot after the deletion (the kit's
/// <c>secure_delete</c> and the checkpoint after a policy); the pre-operation snapshots and the archive's copies expire after
/// 7 days, at the daily snapshot too, with no newest five kept; Delete's archive rewrite matches the user's rating ids too,
/// and runs after the commit (tombstones first), pending and retried daily when it fails; a reattach never brings back a
/// tombstoned id, nor a line that changed since the resolver read it; the counts throw while the archive can't be read;
/// Remap's dry run counts the archived ratings, and its plan token covers the conflicts it would make; KeepAsOrphan logs one
/// row whatever a moderator logged since.
/// </summary>
public sealed partial class RatingsLibraryConsumerTests
{
    private const string ResidueMarker = "UR residue marker: the orchard was empty at noon.";

    // The deleted member's review leaves no byte in the database file, in its -wal (the policy asks for the
    // checkpoint; the test waits for it) or in the daily snapshot taken after; only the pre-operation snapshot taken before
    // holds it, and the daily snapshot deletes that one once it is older than 7 days.
    [Fact]
    public async Task ADeletedMembersReview_LeavesNoByteInTheDatabase_ItsWal_OrTheDailySnapshotAfter_AndThePreOpOneExpires()
    {
        var live = await RateAsync(AddMovie("Collateral (2004)", _movies, "1538"), _alice, 7, ResidueMarker);
        await RateAsync(AddMovie("Ronin (1998)", _movies, "8195"), _bob, 6, "Synthetic other review.");
        ServerKnows(_bob);
        var reconciler = UsersReconciler(Owner(), () => new Dictionary<Guid, string> { [_bob.Id] = "bob" }, DeletedUserPolicy.Delete);

        Assert.Equal(1, await reconciler.HandleUserDeletedAsync(_alice.Id, Ct));

        Assert.Null(_fx.Store.Get(live.Id));
        Assert.True(await _fx.Store.WaitForCheckpointAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct), "the policy's checkpoint completed");
        Assert.Equal(0L, new FileInfo(_fx.File + "-wal").Length);
        Assert.Empty(FilesHolding(ResidueMarker, _fx.File, _fx.File + "-wal"));
        var daily = (await _fx.Database.SnapshotAsync(7, Ct))!;
        Assert.Empty(FilesHolding(ResidueMarker, daily));
        var preOp = Assert.Single(_fx.Database.ListSnapshots(), s => s.Kind == SnapshotKind.PreOp);
        var preOpPath = Path.Combine(_fx.Snapshots, "pre-op", preOp.Name);
        Assert.NotEmpty(FilesHolding(ResidueMarker, preOpPath));

        _fx.Clock.Advance(TimeSpan.FromDays(8));
        await _fx.Database.SnapshotAsync(7, Ct);
        Assert.False(File.Exists(preOpPath));
    }

    // A burst's pre-operation snapshots and the archive's copies don't outlive 7 days, none of them, even when no later
    // operation takes another: the daily snapshot rotates them.
    [Fact]
    public async Task ABurstsPreOpSnapshots_AndTheArchivesCopies_AreGone7DaysLater_AtTheDailySnapshot()
    {
        var (_, onHeat) = await ArchivedHeatAsync();
        var users = Enumerable.Range(0, 12).Select(i => ServicesFixture.NewUser($"burst{i}")).ToList();
        foreach (var user in users)
        {
            await RateAsync(AddMovie($"Burst {user.Username}", _movies, null), user, 6, "Synthetic burst review.");
        }

        var reconciler = UsersReconciler(Owner(), () => new Dictionary<Guid, string> { [_bob.Id] = "bob" }, DeletedUserPolicy.Delete);
        foreach (var id in users.Select(u => u.Id).Append(_alice.Id))
        {
            _fx.Clock.Advance(TimeSpan.FromSeconds(2));
            await reconciler.HandleUserDeletedAsync(id, Ct);
        }

        Assert.Null(_fx.Store.Get(onHeat.Id));
        var preOpFolder = Path.Combine(_fx.Snapshots, "pre-op");
        Assert.Equal(13, _fx.Database.ListSnapshots().Count(s => s.Kind == SnapshotKind.PreOp));
        Assert.Single(Directory.GetFiles(preOpFolder, "userratings-archived-*.jsonl"));

        _fx.Clock.Advance(TimeSpan.FromDays(6));
        await _fx.Database.SnapshotAsync(7, Ct);
        Assert.Equal(13, _fx.Database.ListSnapshots().Count(s => s.Kind == SnapshotKind.PreOp));

        // A day and a minute more: the newest of the burst is past 7 days too.
        _fx.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        await _fx.Database.SnapshotAsync(7, Ct);
        Assert.DoesNotContain(_fx.Database.ListSnapshots(), s => s.Kind == SnapshotKind.PreOp);
        Assert.Empty(Directory.GetFiles(preOpFolder, "userratings-archived-*.jsonl"));
    }

    // A member whose rating was archived and came back, then given to a new id by Remap by name, then deleted (Delete):
    // the archive's earlier line under the old id goes too, as the rewrite matches the user's rating ids.
    [Fact]
    public async Task DeleteAfterARemap_LeavesNoArchiveLineWithTheReview_TheEarlierLineUnderTheOldIdNeither()
    {
        var (_, onHeat) = await ArchivedHeatAsync();
        var back = AddMovie("Heat (1995) [1080p]", _movies, "949");
        _library.RaiseAdded(back);
        await _detached.ReattachFromArchiveAsync(_consumer, Ct);
        Assert.Equal(back.Id, _fx.Store.Get(onHeat.Id)!.ItemId);
        await _fx.Store.WriteAsync(w => { RatingsUserDataOwner.RecordName(w, _alice.Id, "alice"); RatingsUserDataOwner.RecordName(w, _bob.Id, "bob"); return 0; }, Ct);
        await PadAsync(_bob, 10);
        var newAlice = ServicesFixture.NewUser("alice");
        ServerKnows(newAlice, _bob);
        var server = new Dictionary<Guid, string> { [newAlice.Id] = "alice", [_bob.Id] = "bob" };
        var reconciler = UsersReconciler(Owner(), () => server, DeletedUserPolicy.Delete);
        await reconciler.ReconcileAsync(Ct);
        var dry = await reconciler.RemapByNameAsync(dryRun: true, ifPlan: null, Ct);
        await reconciler.RemapByNameAsync(dryRun: false, dry.PlanToken, Ct);
        Assert.Equal(newAlice.Id, _fx.Store.Get(onHeat.Id)!.UserId);
        Assert.Contains(HeistReview, await File.ReadAllTextAsync(_detached.ArchiveOf(_consumer).FilePath, Ct), StringComparison.Ordinal);

        server.Remove(newAlice.Id);
        ServerKnows(_bob);
        await reconciler.HandleUserDeletedAsync(newAlice.Id, Ct);

        Assert.Null(_fx.Store.Get(onHeat.Id));
        Assert.DoesNotContain(HeistReview, await File.ReadAllTextAsync(_detached.ArchiveOf(_consumer).FilePath, Ct), StringComparison.Ordinal);
    }

    // Purge order and the tombstone skip: the archive's rewrite runs after the commit, so the tombstones exist first; one that
    // fails stays pending (counted in Diagnostics), its ratings count for nobody and never come back meanwhile, though their
    // line is still there as the resolver reads it; the daily step runs it again.
    [Fact]
    public async Task ADeletesArchiveRewrite_ThatFails_IsPending_ItsRatingNeverComesBack_AndTheDailyStepRunsItAgain()
    {
        var (_, onHeat) = await ArchivedHeatAsync();
        var owner = Owner();
        var reconciler = UsersReconciler(owner, () => new Dictionary<Guid, string> { [_bob.Id] = "bob" }, DeletedUserPolicy.Delete);
        var archiveFile = _detached.ArchiveOf(_consumer).FilePath;

        // The rewrite's temporary file can't be created: the archive still reads, its rewrite fails.
        var blocker = Directory.CreateDirectory(archiveFile + ".tmp");
        Assert.Equal(1, await reconciler.HandleUserDeletedAsync(_alice.Id, Ct));

        Assert.Equal(_alice.Id, _fx.Store.GetTombstone(onHeat.Id)!.UserId);
        Assert.Contains(HeistReview, await File.ReadAllTextAsync(archiveFile, Ct), StringComparison.Ordinal);
        Assert.Equal(1, _consumer.Describe()["ArchivePurgesPending"]);
        Assert.False(owner.CountRecordsByUser().ContainsKey(_alice.Id));
        Assert.Contains(_logger.Entries, e => e.Message.Contains("couldn't be taken out of the ratings' archive", StringComparison.Ordinal));

        // Applied again meanwhile, it finds nothing left to change.
        Assert.Equal(0, await owner.HandleUserDeletedAsync(_alice.Id, DeletedUserPolicy.Delete, Ct));

        // Heat back: the line is there, unchanged, but the id is tombstoned.
        var back = AddMovie("Heat (1995) [1080p]", _movies, "949");
        _library.RaiseAdded(back);
        await _detached.ReattachFromArchiveAsync(_consumer, Ct);
        Assert.Null(_fx.Store.Get(onHeat.Id));
        Assert.Single(_detached.ArchiveOf(_consumer).Read().Records);

        blocker.Delete();
        var step = await owner.RunPendingPurgesAsync(Ct);

        Assert.Equal((1, 1, 0, 1), ((int)step["Pending"]!, (int)step["Done"]!, (int)step["Failed"]!, (int)step["Purged"]!));
        Assert.DoesNotContain(HeistReview, await File.ReadAllTextAsync(archiveFile, Ct), StringComparison.Ordinal);
        Assert.Equal(0, _consumer.Describe()["ArchivePurgesPending"]);
        Assert.Equal(0, (int)(await owner.RunPendingPurgesAsync(Ct))["Pending"]!);
    }

    // A reattach whose resolver read the line before a Delete purged it never brings the rating back.
    [Fact]
    public async Task ARestoreRacingADeletion_NeverBringsBackTheDeletedMembersRating()
    {
        var (_, onHeat) = await ArchivedHeatAsync();
        var back = AddMovie("Heat (1995) [1080p]", _movies, "949");
        _library.RaiseAdded(back);
        var stale = Assert.Single(_detached.ArchiveOf(_consumer).Read().Records);
        ServerKnows(_bob);
        await Owner().HandleUserDeletedAsync(_alice.Id, DeletedUserPolicy.Delete, Ct);

        var restored = await _consumer.RestoreFromArchiveAsync([new DetachedResolution(stale, Relink(back))], Ct);

        Assert.Empty(restored);
        Assert.Null(_fx.Store.Get(onHeat.Id));
    }

    // The line-as-read check (RestoreFromArchiveAsync): a reattach whose resolver read the line before an Anonymize re-keyed
    // it (no tombstone: the rating lives on, the former member's) never brings it back under the deleted id; the next
    // reattach brings it back as the former member's.
    [Fact]
    public async Task ARestoreRacingAnAnonymization_NeverBringsBackTheRatingUnderTheDeletedId()
    {
        var (_, onHeat) = await ArchivedHeatAsync();
        var back = AddMovie("Heat (1995) [1080p]", _movies, "949");
        _library.RaiseAdded(back);
        var stale = Assert.Single(_detached.ArchiveOf(_consumer).Read().Records);
        ServerKnows(_bob);
        await Owner().HandleUserDeletedAsync(_alice.Id, DeletedUserPolicy.Anonymize, Ct);
        Assert.Null(_fx.Store.GetTombstone(onHeat.Id));

        var restored = await _consumer.RestoreFromArchiveAsync([new DetachedResolution(stale, Relink(back))], Ct);

        Assert.Empty(restored);
        Assert.Null(_fx.Store.Get(onHeat.Id));

        await _detached.ReattachFromArchiveAsync(_consumer, Ct);
        var former = _fx.Store.Get(onHeat.Id)!;
        Assert.NotEqual(_alice.Id, former.UserId);
        Assert.Contains(former.UserId, _fx.Store.GetFormerUsers().Keys);
    }

    // While the archive can't be read, the counts throw (never leave its ratings out), so the reconciler skips the owner:
    // the name of an id whose only rating is archived, and its mark, stay.
    [Fact]
    public async Task WhileTheArchiveCantBeRead_TheCountsThrow_SoTheReconcilerSkipsTheOwner_AndKeepsAnArchivedOnlyIdsNameAndMark()
    {
        await ArchivedHeatAsync();
        await _fx.Store.WriteAsync(w => { RatingsUserDataOwner.RecordName(w, _alice.Id, "alice"); return 0; }, Ct);
        await PadAsync(_bob, 10);
        var owner = Owner();
        var reconciler = UsersReconciler(owner, () => new Dictionary<Guid, string> { [_bob.Id] = "bob" }, DeletedUserPolicy.Delete);
        await reconciler.ReconcileAsync(Ct);
        Assert.True(reconciler.IsOrphan(_alice.Id));
        var file = _detached.ArchiveOf(_consumer).FilePath;

        UserReconcileResult during;
        using (var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            held.Seek(0, SeekOrigin.End);
            held.WriteByte((byte)'\n');
            await held.FlushAsync(Ct);
            var counts = Assert.Throws<PluginException>(owner.CountRecordsByUser);
            Assert.Equal((PluginErrorKind.Unavailable, RatingsUserDataOwner.ArchiveUnavailableMessage), (counts.Kind, counts.Message));
            await Assert.ThrowsAsync<PluginException>(() => owner.RefreshKnownUsersAsync(new Dictionary<Guid, string> { [_bob.Id] = "bob" }, Ct));
            during = await reconciler.ReconcileAsync(Ct);
        }

        Assert.Contains(RatingsLibraryConsumer.StoreName, during.SkippedOwners);
        Assert.True(reconciler.IsOrphan(_alice.Id));
        Assert.Equal("alice", owner.KnownUsers.NameOf(_alice.Id));
        Assert.Equal(1, owner.CountRecordsByUser()[_alice.Id]);
    }

    // Delete while the archive can't be read is refused (fail closed), and the kit records it as pending: the user is
    // marked deleted, Diagnostics counts it, and the next reconciliation, once the archive reads, applies it.
    [Fact]
    public async Task ADeleteRefusedWhileTheArchiveCantBeRead_IsPending_AndTheNextReconciliationAppliesIt()
    {
        var (_, onHeat) = await ArchivedHeatAsync();
        var live = await RateAsync(AddMovie("Collateral (2004)", _movies, "1538"), _alice, 7, "Synthetic live review.");
        await PadAsync(_bob, 10);
        var reconciler = UsersReconciler(Owner(), () => new Dictionary<Guid, string> { [_bob.Id] = "bob" }, DeletedUserPolicy.Delete);
        var file = _detached.ArchiveOf(_consumer).FilePath;
        using (var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            held.Seek(0, SeekOrigin.End);
            held.WriteByte((byte)'\n');
            await held.FlushAsync(Ct);
            Assert.Equal(0, await reconciler.HandleUserDeletedAsync(_alice.Id, Ct));
            await reconciler.ReconcileAsync(Ct);
        }

        Assert.NotNull(_fx.Store.Get(live.Id));
        Assert.Contains(_alice.Id, reconciler.DeletedUserIds);
        Assert.Equal(1, reconciler.Describe()["PolicyPending"]);

        await reconciler.ReconcileAsync(Ct);

        Assert.Null(_fx.Store.Get(live.Id));
        Assert.Equal(_alice.Id, _fx.Store.GetTombstone(onHeat.Id)!.UserId);
        Assert.DoesNotContain(HeistReview, await File.ReadAllTextAsync(file, Ct), StringComparison.Ordinal);
        Assert.Equal(0, reconciler.Describe()["PolicyPending"]);
    }

    // The dry run's ArchivedRecords counts the archived ratings that would move, as the real run reports.
    [Fact]
    public async Task TheDryRunsArchivedRecords_CountsTheArchivedRatingThatWouldMove()
    {
        await ArchivedHeatAsync();
        await RateAsync(AddMovie("Collateral (2004)", _movies, "1538"), _alice, 7);
        await _fx.Store.WriteAsync(w => { RatingsUserDataOwner.RecordName(w, _alice.Id, "alice"); RatingsUserDataOwner.RecordName(w, _bob.Id, "bob"); return 0; }, Ct);
        await PadAsync(_bob, 10);
        var newAlice = ServicesFixture.NewUser("alice");
        ServerKnows(newAlice, _bob);
        var owner = Owner();
        var reconciler = UsersReconciler(owner, () => new Dictionary<Guid, string> { [newAlice.Id] = "alice", [_bob.Id] = "bob" }, DeletedUserPolicy.Delete);
        await reconciler.ReconcileAsync(Ct);
        var api = new UserRatingsAdminApi(_consumer, () => reconciler, () => owner);

        var dry = await api.RemapUsersAsync(null, "Name", true, null, Ct);
        var real = await api.RemapUsersAsync(null, "Name", false, (string)dry["PlanToken"]!, Ct);

        Assert.Equal((1, 1), ((int)dry["ArchivedRecords"]!, (int)real["ArchivedRecords"]!));
    }

    // The plan token covers the conflicts the remap would make, so the new member rating the same item between the runs
    // (an Ambiguous row the dry run didn't show) makes the real run a 409 with nothing written; the token is compared in any
    // case.
    [Fact]
    public async Task ARealRemap_RefusesAnAmbiguousRowTheDryRunDidntShow_AndTakesTheTokenInAnyCase()
    {
        var collateral = AddMovie("Collateral (2004)", _movies, "1538");
        var oldAlice = ServicesFixture.NewUser("alice");
        var old = await RateAsync(collateral, oldAlice, 7, updated: Created);
        await PadAsync(_bob, 20);
        await _fx.Store.WriteAsync(w => { RatingsUserDataOwner.RecordName(w, oldAlice.Id, "alice"); return 0; }, Ct);
        var owner = Owner();
        var reconciler = UsersReconciler(owner, () => new Dictionary<Guid, string> { [_alice.Id] = "alice", [_bob.Id] = "bob" }, DeletedUserPolicy.Delete);
        await reconciler.ReconcileAsync(Ct);
        var api = new UserRatingsAdminApi(_consumer, () => reconciler, () => owner);
        var dry = await api.RemapUsersAsync(null, "Name", true, null, Ct);
        Assert.Equal(0, dry["Ambiguous"]);

        await RateAsync(collateral, _alice, 4, updated: Created.AddDays(1));
        var generation = _fx.Store.Generation;
        var changed = await Assert.ThrowsAsync<PluginException>(() => api.RemapUsersAsync(null, "Name", false, (string)dry["PlanToken"]!, Ct));

        Assert.Equal((PluginErrorKind.Conflict, UserReconciler.PlanChangedMessage), (changed.Kind, changed.Message));
        Assert.Equal(generation, _fx.Store.Generation);
        Assert.Equal(oldAlice.Id, _fx.Store.Get(old.Id)!.UserId);

        var again = await api.RemapUsersAsync(null, "Name", true, null, Ct);
        Assert.Equal(1, again["Ambiguous"]);
        var real = await api.RemapUsersAsync(null, "Name", false, ((string)again["PlanToken"]!).ToUpperInvariant(), Ct);
        Assert.Equal(1, real["Ambiguous"]);
    }

    // KeepAsOrphan applied again after a moderator's row about the member writes no second UserOrphaned row.
    [Fact]
    public async Task KeepAsOrphan_AppliedAgainAfterAModeratorsRow_WritesNoSecondUserOrphanedRow()
    {
        var carol = ServicesFixture.NewUser("carol");
        var rating = await RateAsync(AddMovie("Collateral (2004)", _movies, "1538"), carol, 7, "Synthetic review.");
        var reconciler = UsersReconciler(Owner(), () => new Dictionary<Guid, string> { [_alice.Id] = "alice", [_bob.Id] = "bob" }, DeletedUserPolicy.KeepAsOrphan);
        await reconciler.HandleUserDeletedAsync(carol.Id, Ct);
        await _fx.Store.WriteAsync(
            w => w.AppendModerationLog(new ModerationLogRecord { AtUtc = Now, ActorUserId = _alice.Id, Action = RatingValues.ModerationAction.RemoveText, RatingId = rating.Id, TargetUserId = carol.Id }),
            Ct);

        Assert.Equal(0, (await reconciler.ApplyPolicyAsync([carol.Id], Ct)).Affected);

        Assert.Single(_fx.Store.GetModerationLog(0, 50), l => l.Action == RatingValues.ModerationAction.UserOrphaned);
    }

    private static ResolveResult Relink(MediaBrowser.Controller.Entities.BaseItem item)
        => new() { Outcome = ResolveOutcome.Relink, Strength = MatchStrength.Strong, Source = MatchSource.ProviderIds, ItemId = item.Id, Item = item };

    private static List<string> FilesHolding(string marker, params string[] files)
    {
        var needle = Encoding.UTF8.GetBytes(marker);
        return [.. files.Where(File.Exists).Where(f => StoreFixture.ReadOpenFile(f).AsSpan().IndexOf(needle) >= 0).Select(f => Path.GetFileName(f))];
    }
}
