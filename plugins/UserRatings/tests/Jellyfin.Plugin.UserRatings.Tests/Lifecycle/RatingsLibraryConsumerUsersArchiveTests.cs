using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Services;
using Microsoft.Data.Sqlite;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Lifecycle;

/// <summary>
/// The fixes to the users' lifecycle (0.3.13.0), on the same fixture, from their repro tests (some are
/// with the archive's and the merged versions' tests). The users' lifecycle takes
/// the archive too: <c>Delete</c> leaves no archived review text and tombstones the archived ids, <c>Anonymize</c> and
/// <i>Remap by name</i> re-key the archived ratings so they come back as the former member's or the new id's, and the counts
/// (the reconciler's, the kit's marks, the names) see them. A burst of deletions keeps a snapshot of every deleted
/// user's ratings, and an operation that changes nothing takes none. A real remap moves only what its dry run showed.
/// A former member's held rating stays held in its folder.
/// </summary>
public sealed partial class RatingsLibraryConsumerTests
{
    private const string HeistReview = "Synthetic review of the heist.";

    // Delete, the default, deletes every rating of the user: the review archived before the deletion leaves
    // the archive file, its earlier lines too, a tombstone keeps an import from bringing it back, and the user is marked
    // deleted though they had no live rating.
    [Fact]
    public async Task Delete_RemovesTheDeletedMembersArchivedReview_WithATombstone_AndMarksThemDeleted()
    {
        var (heat, onHeat) = await ArchivedHeatAsync();
        var owner = Owner();
        var reconciler = UsersReconciler(owner, () => new Dictionary<Guid, string> { [_bob.Id] = "bob" }, DeletedUserPolicy.Delete);
        Assert.Equal(1, owner.CountRecordsByUser()[_alice.Id]);

        Assert.Equal(1, await reconciler.HandleUserDeletedAsync(_alice.Id, Ct));

        Assert.DoesNotContain(HeistReview, await File.ReadAllTextAsync(_detached.ArchiveOf(_consumer).FilePath, Ct), StringComparison.Ordinal);
        Assert.Empty(_detached.ArchiveOf(_consumer).Read().Records);
        Assert.Contains(_alice.Id, reconciler.DeletedUserIds);
        var tombstone = _fx.Store.GetTombstone(onHeat.Id)!;
        Assert.Equal((_alice.Id, heat.Id, RatingValues.DeletedBy.UserDeleted), (tombstone.UserId, tombstone.ItemId, tombstone.DeletedBy));
        var log = Assert.Single(_fx.Store.GetModerationLog(0, 10), l => l.Action == RatingValues.ModerationAction.UserDeleted);
        Assert.Equal("{\"Ratings\":0,\"Reviews\":0,\"Detached\":0,\"Archived\":1}", log.Details);
        Assert.False(owner.CountRecordsByUser().ContainsKey(_alice.Id));

        // The copy taken before the archive was rewritten is a pre-op one, kept by age as the pre-op snapshots are.
        Assert.Single(Directory.GetFiles(Path.Combine(_detached.SnapshotDirectory, "pre-op"), "*-archived-" + SnapshotOperations.UserDeleted + "-*"));

        // Heat back: nothing of alice's comes back.
        var back = AddMovie("Heat (1995) [1080p]", _movies, "949");
        _library.RaiseAdded(back);
        await _detached.ReattachFromArchiveAsync(_consumer, Ct);
        Assert.Null(_fx.Store.Get(onHeat.Id));
    }

    // With a live rating too, both go, counted apart.
    [Fact]
    public async Task Delete_WithALiveRatingToo_DeletesBoth_AndCountsTheArchivedOneApart()
    {
        await ArchivedHeatAsync();
        var live = await RateAsync(AddMovie("Collateral (2004)", _movies, "1538"), _alice, 7);
        var owner = Owner();
        var reconciler = UsersReconciler(owner, () => new Dictionary<Guid, string> { [_bob.Id] = "bob" }, DeletedUserPolicy.Delete);
        var tally = RatingsUserDataOwner.BeginTally();

        Assert.Equal(2, await reconciler.HandleUserDeletedAsync(_alice.Id, Ct));

        Assert.Null(_fx.Store.Get(live.Id));
        Assert.Equal(RatingValues.DeletedBy.UserDeleted, _fx.Store.GetTombstone(live.Id)!.DeletedBy);
        Assert.DoesNotContain(_detached.ArchiveOf(_consumer).Read().Records, r => RatingsUserDataOwner.ArchivedUserIdOf(r) == _alice.Id);
        Assert.Equal(1, tally.Archived);
        var log = Assert.Single(_fx.Store.GetModerationLog(0, 10), l => l.Action == RatingValues.ModerationAction.UserDeleted);
        Assert.Equal("{\"Ratings\":1,\"Reviews\":0,\"Detached\":0,\"Archived\":1}", log.Details);
    }

    // Another member's archived rating stays where it is when alice is deleted.
    [Fact]
    public async Task Delete_LeavesAnotherMembersArchivedRatingInTheArchive()
    {
        var (_, onHeat) = await ArchivedHeatAsync();
        var owner = Owner();
        var reconciler = UsersReconciler(owner, () => new Dictionary<Guid, string> { [_alice.Id] = "alice" }, DeletedUserPolicy.Delete);
        var bobs = await RateAsync(AddMovie("Collateral (2004)", _movies, "1538"), _bob, 7);

        Assert.Equal(2, await reconciler.HandleUserDeletedAsync(_bob.Id, Ct));

        Assert.Null(_fx.Store.Get(bobs.Id));
        Assert.Equal(onHeat.Id.ToString("D", CultureInfo.InvariantCulture), Assert.Single(_detached.ArchiveOf(_consumer).Read().Records).Id);
        Assert.Contains(HeistReview, await File.ReadAllTextAsync(_detached.ArchiveOf(_consumer).FilePath, Ct), StringComparison.Ordinal);
    }

    // Anonymize keeps a deleted member's ratings, counted, as a former member's: the archived one is re-keyed too, so it
    // comes back as the former member's when the film does (the kit resolves a former member's without a view).
    [Fact]
    public async Task Anonymize_TheArchivedRatingComesBackAsTheFormerMembersWhenTheFilmDoes()
    {
        var (_, onHeat) = await ArchivedHeatAsync();
        var live = await RateAsync(AddMovie("Collateral (2004)", _movies, "1538"), _alice, 7);
        var owner = Owner();
        ServerKnows(_bob);

        Assert.Equal(2, await owner.HandleUserDeletedAsync(_alice.Id, DeletedUserPolicy.Anonymize, Ct));

        var former = _fx.Store.Get(live.Id)!.UserId;
        Assert.Equal("Former member", _fx.Store.GetFormerUsers()[former]);
        var line = Assert.Single(_detached.ArchiveOf(_consumer).Read().Records);
        Assert.Equal(((Guid?)former, (Guid?)null), (RatingsUserDataOwner.ArchivedUserIdOf(line), line.UserId));
        var file = await File.ReadAllTextAsync(_detached.ArchiveOf(_consumer).FilePath, Ct);
        Assert.DoesNotContain(_alice.Id.ToString("D", CultureInfo.InvariantCulture), file, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_alice.Id.ToString("N", CultureInfo.InvariantCulture), file, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(HeistReview, file, StringComparison.Ordinal);
        Assert.False(owner.CountRecordsByUser().ContainsKey(_alice.Id));

        var back = AddMovie("Heat (1995) [1080p]", _movies, "949");
        _library.RaiseAdded(back);
        await _detached.ReattachFromArchiveAsync(_consumer, Ct);

        var after = _fx.Store.Get(onHeat.Id)!;
        Assert.Equal((former, back.Id, true, HeistReview), (after.UserId, after.ItemId, after.IsAttached, after.ReviewText));
        Assert.Empty(_detached.ArchiveOf(_consumer).Read().Records);
    }

    // A rating archived once and back since leaves its earlier lines (the member's id, the text) in the archive; Delete and
    // Anonymize take them out too, so no line links the deleted id to a text, and the live rating is as the policy says.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMembersOnceArchivedRating_LeavesNoLineWithTheirId_WhenTheyAreDeleted(bool anonymize)
    {
        var policy = anonymize ? DeletedUserPolicy.Anonymize : DeletedUserPolicy.Delete;
        var (_, onHeat) = await ArchivedHeatAsync();
        var back = AddMovie("Heat (1995) [1080p]", _movies, "949");
        _library.RaiseAdded(back);
        await _detached.ReattachFromArchiveAsync(_consumer, Ct);
        Assert.Equal(back.Id, _fx.Store.Get(onHeat.Id)!.ItemId);
        Assert.Contains(_alice.Id.ToString("D", CultureInfo.InvariantCulture), await File.ReadAllTextAsync(_detached.ArchiveOf(_consumer).FilePath, Ct), StringComparison.OrdinalIgnoreCase);
        ServerKnows(_bob);

        Assert.Equal(1, await Owner().HandleUserDeletedAsync(_alice.Id, policy, Ct));

        var file = await File.ReadAllTextAsync(_detached.ArchiveOf(_consumer).FilePath, Ct);
        Assert.DoesNotContain(_alice.Id.ToString("D", CultureInfo.InvariantCulture), file, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_alice.Id.ToString("N", CultureInfo.InvariantCulture), file, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(HeistReview, file, StringComparison.Ordinal);
        Assert.Equal(policy == DeletedUserPolicy.Delete, _fx.Store.Get(onHeat.Id) is null);
    }

    // Remap by name after a restore onto a rebuilt server gives the archived rating back to the new id too.
    [Fact]
    public async Task RemapByName_GivesTheArchivedRatingBackToo()
    {
        var (_, onHeat) = await ArchivedHeatAsync();
        var live = await RateAsync(AddMovie("Collateral (2004)", _movies, "1538"), _alice, 7);
        await _fx.Store.WriteAsync(w => { RatingsUserDataOwner.RecordName(w, _alice.Id, "alice"); RatingsUserDataOwner.RecordName(w, _bob.Id, "bob"); return 0; }, Ct);
        await PadAsync(_bob, 10);

        // Rebuilt: alice is a new id now.
        var newAlice = ServicesFixture.NewUser("alice");
        ServerKnows(newAlice, _bob);
        var owner = Owner();
        var reconciler = UsersReconciler(owner, () => new Dictionary<Guid, string> { [newAlice.Id] = "alice", [_bob.Id] = "bob" }, DeletedUserPolicy.Delete);
        await reconciler.ReconcileAsync(Ct);
        Assert.Equal(2, owner.CountRecordsByUser()[_alice.Id]);
        Assert.Equal("alice", owner.KnownUsers.NameOf(_alice.Id));
        var dry = await reconciler.RemapByNameAsync(dryRun: true, ifPlan: null, Ct);
        Assert.Equal((0, 2), (dry.MovedRecords, Assert.Single(dry.Mapped).Records));

        var tally = RatingsUserDataOwner.BeginTally();
        var report = await reconciler.RemapByNameAsync(dryRun: false, dry.PlanToken, Ct);

        Assert.Equal((2, 1), (report.MovedRecords, tally.Archived));
        Assert.Equal(newAlice.Id, _fx.Store.Get(live.Id)!.UserId);
        var line = Assert.Single(_detached.ArchiveOf(_consumer).Read().Records);
        Assert.Equal(((Guid?)newAlice.Id, (Guid?)newAlice.Id), (RatingsUserDataOwner.ArchivedUserIdOf(line), line.UserId));
        var remapped = Assert.Single(_fx.Store.GetModerationLog(0, 10), l => l.Action == RatingValues.ModerationAction.UserRemapped);
        Assert.Equal(((Guid?)_alice.Id, $"{{\"NewUserId\":\"{newAlice.Id:N}\",\"Ratings\":1,\"Archived\":1,\"Ambiguous\":0}}"), (remapped.TargetUserId, remapped.Details));
        Assert.DoesNotContain("alice", remapped.Details!, StringComparison.Ordinal);

        var back = AddMovie("Heat (1995) [1080p]", _movies, "949");
        _library.RaiseAdded(back);
        await _detached.ReattachFromArchiveAsync(_consumer, Ct);

        var after = _fx.Store.Get(onHeat.Id)!;
        Assert.Equal((newAlice.Id, back.Id, true), (after.UserId, after.ItemId, after.IsAttached));
    }

    // The names (KnownUsers.Refresh) keep an id whose only ratings are archived: Remap by name needs it.
    [Fact]
    public async Task TheNames_KeepAnIdWhoseOnlyRatingsAreArchived()
    {
        await ArchivedHeatAsync();
        await _fx.Store.WriteAsync(w => { RatingsUserDataOwner.RecordName(w, _alice.Id, "alice"); return 0; }, Ct);
        var owner = Owner();
        Assert.Equal(1, owner.CountRecordsByUser()[_alice.Id]);

        // The server doesn't know alice now: her name is the only link back, and Refresh keeps it.
        await owner.RefreshKnownUsersAsync(new Dictionary<Guid, string> { [_bob.Id] = "bob" }, Ct);

        Assert.Equal("alice", owner.KnownUsers.NameOf(_alice.Id));
    }

    // A script deleting 10 users with ratings: every deleted user's rating is in some pre-op snapshot, as the
    // pre-op snapshots are kept by age (7 days, at most 50).
    [Fact]
    public async Task ABurstOfTenDeletions_KeepsASnapshotWithEachDeletedUsersRating()
    {
        var users = Enumerable.Range(0, 10).Select(i => ServicesFixture.NewUser($"throwaway{i}")).ToList();
        var ratings = new List<RatingRecord>();
        foreach (var user in users)
        {
            ratings.Add(await RateAsync(AddMovie($"Film {user.Username}", _movies, null), user, 6, "Synthetic."));
        }

        var reconciler = UsersReconciler(Owner(), () => new Dictionary<Guid, string> { [_alice.Id] = "alice" }, DeletedUserPolicy.Delete);
        foreach (var user in users)
        {
            _fx.Clock.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(1, await reconciler.HandleUserDeletedAsync(user.Id, Ct));
        }

        var preOps = _fx.Database.ListSnapshots().Where(s => s.Kind == SnapshotKind.PreOp).ToList();
        Assert.Equal(10, preOps.Count);
        var lost = users.Select((_, i) => i).Where(i => !preOps.Any(s => SnapshotHolds(s.Name, ratings[i].Id))).ToList();
        Assert.Empty(lost);
    }

    // An operation that changes nothing takes no snapshot and writes no moderation row (KeepAsOrphan applied
    // again), and one that follows another with nothing written in between reuses its snapshot.
    [Fact]
    public async Task KeepAsOrphan_AppliedAgain_TakesNoSnapshot_AndWritesNoSecondRow()
    {
        var carol = ServicesFixture.NewUser("carol");
        await RateAsync(AddMovie("Collateral (2004)", _movies, "1538"), carol, 7);
        var reconciler = UsersReconciler(Owner(), () => new Dictionary<Guid, string> { [_alice.Id] = "alice", [_bob.Id] = "bob" }, DeletedUserPolicy.KeepAsOrphan);

        await reconciler.HandleUserDeletedAsync(carol.Id, Ct);
        for (var i = 0; i < 3; i++)
        {
            _fx.Clock.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(0, (await reconciler.ApplyPolicyAsync([carol.Id], Ct)).Affected);
        }

        Assert.DoesNotContain(_fx.Database.ListSnapshots(), s => s.Kind == SnapshotKind.PreOp);
        Assert.Single(_fx.Store.GetModerationLog(0, 50), l => l.Action == RatingValues.ModerationAction.UserOrphaned);
    }

    [Fact]
    public async Task ADeletionWithNothingWrittenSinceTheLastSnapshot_ReusesIt()
    {
        var carol = ServicesFixture.NewUser("carol");
        var dave = ServicesFixture.NewUser("dave");
        await RateAsync(AddMovie("Collateral (2004)", _movies, "1538"), carol, 7);
        await RateAsync(AddMovie("Ronin (1998)", _movies, "8195"), dave, 5);
        var owner = Owner();

        var first = await owner.SnapshotBeforeAsync(SnapshotOperations.UserDeleted, Ct);
        _fx.Clock.Advance(TimeSpan.FromSeconds(2));
        var second = await owner.SnapshotBeforeAsync(SnapshotOperations.UserDeleted, Ct);
        Assert.Equal(first, second);

        await owner.HandleUserDeletedAsync(carol.Id, DeletedUserPolicy.Delete, Ct);
        _fx.Clock.Advance(TimeSpan.FromSeconds(2));
        var third = await owner.SnapshotBeforeAsync(SnapshotOperations.UserDeleted, Ct);

        Assert.NotEqual(first, third);
        Assert.Equal(2, _fx.Database.ListSnapshots().Count(s => s.Kind == SnapshotKind.PreOp));
    }

    // The real remap is bound to its dry run: an orphan that turned up in between (a user deleted while the plugin
    // was off, whose name a new user took) changes the plan, so the real run is a 409 with nothing written; the dry run again
    // shows it, and its token moves exactly what it showed.
    [Fact]
    public async Task ARealRemap_MovesOnlyWhatTheDryRunShowed()
    {
        var oldAlice = ServicesFixture.NewUser("alice");
        var alices = await RateAsync(AddMovie("Collateral (2004)", _movies, "1538"), oldAlice, 7);
        await PadAsync(_bob, 20);
        await _fx.Store.WriteAsync(w => { RatingsUserDataOwner.RecordName(w, oldAlice.Id, "alice"); return 0; }, Ct);
        var server = new Dictionary<Guid, string> { [_alice.Id] = "alice", [_bob.Id] = "bob" };
        var reconciler = UsersReconciler(Owner(), () => server, DeletedUserPolicy.Delete);
        await reconciler.ReconcileAsync(Ct);
        var dry = await reconciler.RemapByNameAsync(dryRun: true, ifPlan: null, Ct);
        Assert.Equal(oldAlice.Id, Assert.Single(dry.Mapped).OldId);

        // Meanwhile an old "dave" turns up, and a new "dave" is created.
        var oldDave = ServicesFixture.NewUser("dave");
        var daves = await RateAsync(AddMovie("Ronin (1998)", _movies, "8195"), oldDave, 3);
        await _fx.Store.WriteAsync(w => { RatingsUserDataOwner.RecordName(w, oldDave.Id, "dave"); return 0; }, Ct);
        server[Guid.NewGuid()] = "dave";
        var generation = _fx.Store.Generation;

        var changed = await Assert.ThrowsAsync<PluginException>(() => reconciler.RemapByNameAsync(dryRun: false, dry.PlanToken, Ct));
        Assert.Equal((PluginErrorKind.Conflict, UserReconciler.PlanChangedMessage), (changed.Kind, changed.Message));
        Assert.Equal(generation, _fx.Store.Generation);
        Assert.Equal((oldAlice.Id, oldDave.Id), (_fx.Store.Get(alices.Id)!.UserId, _fx.Store.Get(daves.Id)!.UserId));
        Assert.DoesNotContain(_fx.Database.ListSnapshots(), s => s.Kind == SnapshotKind.PreOp);

        var again = await reconciler.RemapByNameAsync(dryRun: true, ifPlan: null, Ct);
        Assert.NotEqual(dry.PlanToken, again.PlanToken);
        var real = await reconciler.RemapByNameAsync(dryRun: false, again.PlanToken, Ct);
        Assert.Equal(again.Mapped.Select(m => (m.OldId, m.NewId)).Order(), real.Mapped.Select(m => (m.OldId, m.NewId)).Order());
        Assert.Equal(2, real.MovedRecords);
    }

    // A remap that detaches a rating as Ambiguous (the member's new id rated the item too) writes the Activity Log entry a
    // batch writes, and counts it in its UserRemapped row; the dry run reports the same count.
    [Fact]
    public async Task ARemapThatMakesAmbiguousRows_WritesTheActivityLogEntry_AndCountsThem()
    {
        var collateral = AddMovie("Collateral (2004)", _movies, "1538");
        var oldAlice = ServicesFixture.NewUser("alice");
        await RateAsync(collateral, oldAlice, 7, updated: Created);
        await RateAsync(collateral, _alice, 4, updated: Created.AddDays(1));
        await PadAsync(_bob, 20);
        await _fx.Store.WriteAsync(w => { RatingsUserDataOwner.RecordName(w, oldAlice.Id, "alice"); return 0; }, Ct);
        var owner = Owner();
        var reconciler = UsersReconciler(owner, () => new Dictionary<Guid, string> { [_alice.Id] = "alice", [_bob.Id] = "bob" }, DeletedUserPolicy.Delete);
        await reconciler.ReconcileAsync(Ct);
        var dry = await reconciler.RemapByNameAsync(dryRun: true, ifPlan: null, Ct);
        Assert.Equal(1, owner.CountRemapConflicts(dry.Mapped.ToDictionary(m => m.OldId, m => m.NewId)));
        Assert.Empty(_entries);

        var tally = RatingsUserDataOwner.BeginTally();
        await reconciler.RemapByNameAsync(dryRun: false, dry.PlanToken, Ct);

        Assert.Equal(1, tally.Ambiguous);
        Assert.Equal(RatingsLibraryConsumer.AmbiguousActivityName(1), Assert.Single(_entries).Name);
        var remapped = Assert.Single(_fx.Store.GetModerationLog(0, 10), l => l.Action == RatingValues.ModerationAction.UserRemapped);
        Assert.Equal($"{{\"NewUserId\":\"{_alice.Id:N}\",\"Ratings\":1,\"Archived\":0,\"Ambiguous\":1}}", remapped.Details);
    }

    // A member deleted (Delete) while the guard holds their rating leaves no stale held entry.
    [Fact]
    public async Task AHeldRatingsMemberDeleted_LeavesNoStaleHeldEntry()
    {
        var (heat, _, _, onHeat, _) = await GuardHeldWithACopyElsewhereAsync();
        ServerKnows(_bob);
        await Owner().HandleUserDeletedAsync(_alice.Id, DeletedUserPolicy.Delete, Ct);
        Assert.Null(_fx.Store.Get(onHeat.Id));

        await DailyAsync();

        Assert.DoesNotContain(Held(), h => (Guid)h["ItemId"]! == heat.Id && ((IEnumerable<Guid>)h["RatingIds"]!).Contains(onHeat.Id));
    }

    // A member anonymized while the guard holds their rating: the former member's held rating stays held in its
    // folder (the library-folder rule is every held rating's), not moved to the copy in another library.
    [Fact]
    public async Task AHeldRatingsMemberAnonymized_StaysHeldInItsFolder()
    {
        var (heat, _, _, onHeat, _) = await GuardHeldWithACopyElsewhereAsync();
        ServerKnows(_bob);
        await Owner().HandleUserDeletedAsync(_alice.Id, DeletedUserPolicy.Anonymize, Ct);

        await DailyAsync();

        var after = _fx.Store.Get(onHeat.Id)!;
        Assert.Equal((heat.Id, true), (after.ItemId, after.IsAttached));
        Assert.NotEqual(_alice.Id, after.UserId);
        Assert.Equal([onHeat.Id], HeldRatingIds(heat.Id));
    }

    private RatingsUserDataOwner Owner() => new(_fx.Store, _logger, _fx.Clock, () => _consumer);

    private UserReconciler UsersReconciler(RatingsUserDataOwner owner, Func<IReadOnlyDictionary<Guid, string>> server, DeletedUserPolicy policy)
    {
        var reconciler = new UserReconciler(
            server,
            [owner],
            new KitUserMarks(_fx.Directory.Combine("userratings", "kit-users.json"), _logger, _fx.Clock),
            () => policy,
            _logger,
            _fx.Clock);
        _disposables.Add(reconciler);
        return reconciler;
    }

    private void ServerKnows(params User[] users)
        => _users.Setup(u => u.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => users.FirstOrDefault(u => u.Id == id));

    // Ratings of a member on films of their own, so the reconciler's mismatch guard sees enough of the server's users.
    private async Task PadAsync(User user, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await RateAsync(AddMovie($"Padding {i}", _movies, null), user, 6);
        }
    }

    private bool SnapshotHolds(string snapshot, Guid ratingId)
    {
        var copy = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_fx.Snapshots, "pre-op", snapshot), Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(copy.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Ratings WHERE Id = $Id;";
        command.Parameters.AddWithValue("$Id", ratingId.ToString("N", CultureInfo.InvariantCulture));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }
}
