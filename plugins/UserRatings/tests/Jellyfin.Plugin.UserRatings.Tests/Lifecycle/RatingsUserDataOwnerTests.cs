using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Lifecycle;

/// <summary>
/// The user policies, on the real ratings store: <c>Delete</c> with <c>UserDeleted</c> tombstones,
/// <c>Anonymize</c> (two deleted users' ratings of one item don't collide, "Former member", "Former member 2"),
/// <c>KeepAsOrphan</c>; the kit's reconciler over this owner marking orphans and changing no rating, former members left
/// out; <i>Remap by name</i> under the conflict rule; the names kept in <c>Meta.KnownUsers</c>; the pre-operation snapshot;
/// and read-only data left alone.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class RatingsUserDataOwnerTests : IDisposable
{
    private static readonly DateTime Created = StoreFixture.T0.AddDays(-30);

    private readonly StoreFixture _fx = new();
    private readonly ListLogger _logger = new();
    private readonly Guid _alice = Guid.NewGuid();
    private readonly Guid _bob = Guid.NewGuid();
    private readonly RatingsUserDataOwner _owner;

    public RatingsUserDataOwnerTests()
    {
        _owner = new RatingsUserDataOwner(_fx.Store, _logger, _fx.Clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Now => _fx.Clock.GetUtcNow().UtcDateTime;

    public void Dispose() => _fx.Dispose();

    // ------------------------------------------------------------------------------------------------- the policies

    [Fact]
    public async Task Delete_DeletesEveryRatingOfTheUser_AttachedAndDetached_WithUserDeletedTombstones_AndLogsCountsWithoutText()
    {
        var reviewed = await UpsertAsync(StoreFixture.Movie(_alice, Guid.NewGuid(), 9, "A private opinion.", at: Created));
        var starred = await UpsertAsync(StoreFixture.Movie(_alice, Guid.NewGuid(), 6, at: Created));
        var detached = await UpsertAsync(StoreFixture.Movie(_alice, Guid.NewGuid(), 4, at: Created) with { DetachedUtc = Created.AddDays(1), DetachReason = RatingValues.DetachReason.Missing });
        var bobs = await UpsertAsync(StoreFixture.Movie(_bob, reviewed.ItemId, 7, "Bob's words.", at: Created));
        await RecordNamesAsync((_alice, "alice"), (_bob, "bob"));

        var affected = await _owner.HandleUserDeletedAsync(_alice, DeletedUserPolicy.Delete, Ct);

        Assert.Equal(3, affected);
        foreach (var gone in new[] { reviewed, starred, detached })
        {
            Assert.Null(_fx.Store.Get(gone.Id));
            var tombstone = _fx.Store.GetTombstone(gone.Id)!;
            Assert.Equal((_alice, gone.ItemId, RatingValues.DeletedBy.UserDeleted, Now), (tombstone.UserId, tombstone.ItemId, tombstone.DeletedBy, tombstone.DeletedUtc));
        }

        Assert.Equal(bobs, _fx.Store.Get(bobs.Id));
        var log = Assert.Single(_fx.Store.GetModerationLog(0, 10));
        Assert.Equal((RatingValues.ModerationAction.UserDeleted, (Guid?)_alice, (Guid?)null, (Guid?)null, (Guid?)null), (log.Action, log.TargetUserId, log.RatingId, log.ItemId, log.ActorUserId));
        Assert.Equal("{\"Ratings\":3,\"Reviews\":1,\"Detached\":1,\"Archived\":0}", log.Details);
        Assert.Null(log.TextSnapshot);
        Assert.Null(log.Reason);

        // The user's name goes with the ratings; no text or name in the log.
        Assert.Null(_owner.KnownUsers.NameOf(_alice));
        Assert.Equal("bob", _owner.KnownUsers.NameOf(_bob));
        Assert.Equal(_bob, Assert.Single(_owner.CountRecordsByUser()).Key);
        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("private opinion", StringComparison.Ordinal) || e.Message.Contains("alice", StringComparison.Ordinal));
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains(_alice.ToString(), StringComparison.Ordinal) && e.Message.Contains("3 ratings deleted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Anonymize_ReKeysTheRatingsToANewFormerMember_AndTwoDeletedUsersOnOneItemDontCollide()
    {
        var item = Guid.NewGuid();
        var alices = await UpsertAsync(StoreFixture.Movie(_alice, item, 9, "Alice's review.", at: Created) with { EditCount = 2 });
        var alicesDetached = await UpsertAsync(StoreFixture.Movie(_alice, Guid.NewGuid(), 3, at: Created) with { DetachedUtc = Created.AddDays(2), DetachReason = RatingValues.DetachReason.Removed });
        var bobs = await UpsertAsync(StoreFixture.Movie(_bob, item, 4, at: Created.AddHours(1)));
        await RecordNamesAsync((_alice, "alice"), (_bob, "bob"));

        Assert.Equal(2, await _owner.HandleUserDeletedAsync(_alice, DeletedUserPolicy.Anonymize, Ct));
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await _owner.HandleUserDeletedAsync(_bob, DeletedUserPolicy.Anonymize, Ct));

        // Two former members with their own ids, numbered in order: one shared id would break UX_Ratings_User_Item.
        var formers = _fx.Store.GetFormerUsers();
        Assert.Equal(["Former member", "Former member 2"], formers.Values.Order(StringComparer.Ordinal));
        var aliceFormer = _fx.Store.Get(alices.Id)!.UserId;
        var bobFormer = _fx.Store.Get(bobs.Id)!.UserId;
        Assert.Equal("Former member", formers[aliceFormer]);
        Assert.Equal("Former member 2", formers[bobFormer]);
        Assert.DoesNotContain(aliceFormer, new[] { _alice, _bob, bobFormer });

        // Everything but the user id is kept: both still attached on the item and counted, the detached one still detached.
        Assert.Equal(alices with { UserId = aliceFormer }, _fx.Store.Get(alices.Id));
        Assert.Equal(alicesDetached with { UserId = aliceFormer }, _fx.Store.Get(alicesDetached.Id));
        Assert.Equal(bobs with { UserId = bobFormer }, _fx.Store.Get(bobs.Id));
        Assert.Equal(2, _fx.Store.GetAttachedForItem(item).Count);
        Assert.Null(_fx.Store.GetTombstone(alices.Id));
        Assert.Equal(_alice, Original(aliceFormer));

        var log = _fx.Store.GetModerationLog(0, 10).OrderBy(l => l.Id).ToList();
        Assert.Equal([RatingValues.ModerationAction.UserAnonymized, RatingValues.ModerationAction.UserAnonymized], log.Select(l => l.Action));
        Assert.Equal("{\"Ratings\":2,\"Reviews\":1,\"Detached\":1,\"Archived\":0,\"FormerUserId\":\"" + aliceFormer.ToString("N") + "\"}", log[0].Details);
        Assert.Equal((Guid?)_alice, log[0].TargetUserId);

        // Former members are no unknown ids (the reconciler leaves them out), and the names are gone.
        Assert.Empty(_owner.CountRecordsByUser());
        Assert.Empty(_owner.KnownUsers);

        // Applied again (the admin's Apply deleted-user policy after a restart): nothing is left under the old id; and a
        // former member's id is never a deleted user's.
        Assert.Equal(0, await _owner.HandleUserDeletedAsync(_alice, DeletedUserPolicy.Anonymize, Ct));
        Assert.Equal(0, await _owner.HandleUserDeletedAsync(aliceFormer, DeletedUserPolicy.Delete, Ct));
        Assert.Equal(2, _fx.Store.GetFormerUsers().Count);
        Assert.Equal(aliceFormer, _fx.Store.Get(alices.Id)!.UserId);
    }

    [Theory]
    [InlineData(new string[0], "Former member")]
    [InlineData(new[] { "Former member" }, "Former member 2")]
    [InlineData(new[] { "Former member", "Former member 2" }, "Former member 3")]
    [InlineData(new[] { "Former member", "Former member 3" }, "Former member 2")]
    [InlineData(new[] { "Former member 2" }, "Former member")]
    public void TheNextFormerMembersName_IsTheLowestFree(string[] taken, string expected)
        => Assert.Equal(expected, RatingsUserDataOwner.NextFormerMemberName(taken));

    [Fact]
    public async Task KeepAsOrphan_ChangesNoRating_LogsUserOrphaned_AndKeepsTheName()
    {
        var alices = await UpsertAsync(StoreFixture.Movie(_alice, Guid.NewGuid(), 9, "Kept.", at: Created));
        await RecordNamesAsync((_alice, "alice"));

        Assert.Equal(0, await _owner.HandleUserDeletedAsync(_alice, DeletedUserPolicy.KeepAsOrphan, Ct));

        Assert.Equal(alices, _fx.Store.Get(alices.Id));
        var log = Assert.Single(_fx.Store.GetModerationLog(0, 10));
        Assert.Equal((RatingValues.ModerationAction.UserOrphaned, (Guid?)_alice, "{\"Ratings\":1,\"Reviews\":1,\"Detached\":0,\"Archived\":0}"), (log.Action, log.TargetUserId, log.Details));
        Assert.Equal("alice", _owner.KnownUsers.NameOf(_alice));
        Assert.Equal(1, _owner.CountRecordsByUser()[_alice]);
        Assert.Empty(_fx.Store.GetFormerUsers());
    }

    [Theory]
    [InlineData(nameof(DeletedUserPolicy.Delete))]
    [InlineData(nameof(DeletedUserPolicy.Anonymize))]
    [InlineData(nameof(DeletedUserPolicy.KeepAsOrphan))]
    public async Task AUserWithoutRatings_OrTheEmptyId_LeavesNoTrace(string name)
    {
        var policy = Enum.Parse<DeletedUserPolicy>(name);
        await UpsertAsync(StoreFixture.Movie(_bob, Guid.NewGuid(), 5));

        Assert.Equal(0, await _owner.HandleUserDeletedAsync(_alice, policy, Ct));
        Assert.Equal(0, await _owner.HandleUserDeletedAsync(Guid.Empty, policy, Ct));

        Assert.Empty(_fx.Store.GetModerationLog(0, 10));
        Assert.Empty(_fx.Store.GetFormerUsers());
        Assert.Equal(0, _fx.Count("Tombstones"));
    }

    [Fact]
    public async Task ReadOnlyData_IsLeftAlone()
    {
        var store = new Mock<IRatingsStore>(MockBehavior.Strict);
        store.SetupGet(s => s.IsReadOnly).Returns(true);
        store.Setup(s => s.ThrowIfReadOnly()).Throws(PluginException.Unavailable("read-only"));
        var owner = new RatingsUserDataOwner(store.Object, _logger, _fx.Clock);

        Assert.True(owner.IsReadOnly);
        Assert.Equal("userratings", owner.Name);

        // The kit leaves a read-only owner alone; one that turned read-only since the kit looked refuses, so the kit never
        // marks the user done. Writing nothing, and the names wait.
        foreach (var policy in Enum.GetValues<DeletedUserPolicy>())
        {
            var refused = await Assert.ThrowsAsync<PluginException>(() => owner.HandleUserDeletedAsync(_alice, policy, Ct));
            Assert.Equal(PluginErrorKind.Unavailable, refused.Kind);
        }

        await Assert.ThrowsAsync<PluginException>(() => owner.RemapUsersAsync(new Dictionary<Guid, Guid> { [_alice] = _bob }, Ct));
        await owner.RefreshKnownUsersAsync(new Dictionary<Guid, string> { [_alice] = "alice" }, Ct);
    }

    // ---------------------------------------------------------------------------------------- the kit's reconciler

    [Fact]
    public async Task TheReconciler_OnAUserDeletedEvent_TakesAPreOpSnapshot_ThenAppliesThePolicy()
    {
        var alices = await UpsertAsync(StoreFixture.Movie(_alice, Guid.NewGuid(), 9, at: Created));
        await UpsertAsync(StoreFixture.Movie(_bob, Guid.NewGuid(), 5, at: Created));
        using var reconciler = Reconciler(new() { [_bob] = "bob" }, DeletedUserPolicy.Delete);

        Assert.Equal(1, await reconciler.HandleUserDeletedAsync(_alice, Ct));

        Assert.Null(_fx.Store.Get(alices.Id));
        var snapshot = Assert.Single(_fx.Database.ListSnapshots(), s => s.Kind == SnapshotKind.PreOp);
        Assert.Equal(SnapshotOperations.UserDeleted, snapshot.Operation);
        Assert.True(File.Exists(Path.Combine(_fx.Snapshots, "pre-op", snapshot.Name)));

        // The snapshot holds the rating from before the policy.
        var copy = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(_fx.Snapshots, "pre-op", snapshot.Name), Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false };
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(copy.ToString());
        await connection.OpenAsync(Ct);
        Assert.Equal(2, StoreFixture.Scalar(connection, "SELECT COUNT(*) FROM Ratings;"));
    }

    [Fact]
    public async Task TheReconciler_MarksUnknownIdsAsOrphans_ChangesNoRating_AndLeavesFormerMembersOut()
    {
        var alices = await UpsertAsync(StoreFixture.Movie(_alice, Guid.NewGuid(), 9, "Kept.", at: Created));
        var bobs = await UpsertAsync(StoreFixture.Movie(_bob, Guid.NewGuid(), 5, at: Created));

        // Enough of bob's that one unknown id's rating is no more than 10 % of them all (the kit's guard).
        for (var i = 0; i < 8; i++)
        {
            await UpsertAsync(StoreFixture.Movie(_bob, Guid.NewGuid(), 6, at: Created));
        }

        var carol = Guid.NewGuid();
        var carols = await UpsertAsync(StoreFixture.Movie(carol, Guid.NewGuid(), 6, at: Created));
        await _owner.HandleUserDeletedAsync(carol, DeletedUserPolicy.Anonymize, Ct);
        var former = _fx.Store.Get(carols.Id)!.UserId;
        await RecordNamesAsync((_alice, "alice"));
        var generation = _fx.Store.Generation;

        // alice deleted while the plugin was off: an orphan, kept and counted nowhere; carol's former member is no user.
        using var reconciler = Reconciler(new() { [_bob] = "bob" }, DeletedUserPolicy.Delete);
        var run = await reconciler.ReconcileAsync(Ct);

        Assert.False(run.GuardTripped);
        var orphan = Assert.Single(run.Orphans);
        Assert.Equal((_alice, "alice", 1), (orphan.UserId, orphan.LastKnownName, orphan.Records));
        Assert.True(reconciler.IsOrphan(_alice));
        Assert.False(reconciler.IsOrphan(former));
        Assert.Equal(alices, _fx.Store.Get(alices.Id));
        Assert.Equal(bobs, _fx.Store.Get(bobs.Id));
        Assert.Equal(former, _fx.Store.Get(carols.Id)!.UserId);
        Assert.Single(_fx.Store.GetModerationLog(0, 10));

        // Only the names were refreshed (bob's taken from the server, alice's kept as the only link back).
        Assert.Equal(("alice", "bob"), (_owner.KnownUsers.NameOf(_alice), _owner.KnownUsers.NameOf(_bob)));
        Assert.Equal(generation + 1, _fx.Store.Generation);
    }

    [Fact]
    public async Task TheReconcilersGuard_AfterARestoreOntoARebuiltServer_ChangesNothing_AndRemapByNameGivesTheRatingsBack()
    {
        var alices = await UpsertAsync(StoreFixture.Movie(_alice, Guid.NewGuid(), 9, at: Created));
        var bobs = await UpsertAsync(StoreFixture.Movie(_bob, Guid.NewGuid(), 5, at: Created));
        await RecordNamesAsync((_alice, "alice"), (_bob, "Bob"));
        var newAlice = Guid.NewGuid();
        var newBob = Guid.NewGuid();
        using var reconciler = Reconciler(new() { [newAlice] = "Alice", [newBob] = "bob" }, DeletedUserPolicy.Delete);

        var run = await reconciler.ReconcileAsync(Ct);
        Assert.True(run.GuardTripped);
        Assert.Equal(alices, _fx.Store.Get(alices.Id));

        var report = await reconciler.RemapByNameAsync(dryRun: false, Ct);
        Assert.Equal(2, report.MovedRecords);
        Assert.StartsWith("userratings-remap-", Assert.Single(report.SafetySnapshots), StringComparison.Ordinal);
        Assert.Equal(newAlice, _fx.Store.Get(alices.Id)!.UserId);
        Assert.Equal(newBob, _fx.Store.Get(bobs.Id)!.UserId);

        // The names moved with the ratings, then the reconciliation after the remap took the server's.
        Assert.Equal(("Alice", "bob"), (_owner.KnownUsers.NameOf(newAlice), _owner.KnownUsers.NameOf(newBob)));
        Assert.Null(_owner.KnownUsers.NameOf(_alice));
        Assert.Empty((await reconciler.ReconcileAsync(Ct)).Orphans);
    }

    // ---------------------------------------------------------------------------------------------------- remap

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Remap_MovesEveryRatingOfTheOldId_AndOnAnItemTheNewIdRated_KeepsTheNewerAttached(bool oldIsNewer)
    {
        var item = Guid.NewGuid();
        var oldOnItem = await UpsertAsync(StoreFixture.Movie(_alice, item, 9, at: Created.AddHours(oldIsNewer ? 2 : 1)));
        var oldDetached = await UpsertAsync(StoreFixture.Movie(_alice, Guid.NewGuid(), 3, at: Created) with { DetachedUtc = Created.AddDays(1), DetachReason = RatingValues.DetachReason.Missing });
        var newOnItem = await UpsertAsync(StoreFixture.Movie(_bob, item, 4, at: Created.AddHours(oldIsNewer ? 1 : 2)));
        await RecordNamesAsync((_alice, "alice"));

        Assert.Equal(2, await _owner.RemapUsersAsync(new Dictionary<Guid, Guid> { [_alice] = _bob }, Ct));

        Assert.Equal(oldDetached with { UserId = _bob }, _fx.Store.Get(oldDetached.Id));
        var (winner, loser) = oldIsNewer ? (oldOnItem.Id, newOnItem.Id) : (newOnItem.Id, oldOnItem.Id);
        Assert.Equal((_bob, true), (_fx.Store.Get(winner)!.UserId, _fx.Store.Get(winner)!.IsAttached));
        Assert.Equal((_bob, RatingValues.DetachReason.Ambiguous, (DateTime?)Now), (_fx.Store.Get(loser)!.UserId, _fx.Store.Get(loser)!.DetachReason, _fx.Store.Get(loser)!.DetachedUtc));
        Assert.Equal(_bob, Assert.Single(_fx.Store.GetAttachedForItem(item)).UserId);
        Assert.NotNull(_fx.Store.GetMeta(RatingsLibraryConsumer.AmbiguousUtcKey));
        Assert.Equal("alice", _owner.KnownUsers.NameOf(_bob));
        Assert.Null(_owner.KnownUsers.NameOf(_alice));
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains($"rating {loser} on item {item} detached as Ambiguous", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remap_NeverMovesAFormerMembersRatings_NorGivesRatingsToOne()
    {
        var carols = await UpsertAsync(StoreFixture.Movie(_alice, Guid.NewGuid(), 6));
        await _owner.HandleUserDeletedAsync(_alice, DeletedUserPolicy.Anonymize, Ct);
        var former = _fx.Store.Get(carols.Id)!.UserId;
        var bobs = await UpsertAsync(StoreFixture.Movie(_bob, Guid.NewGuid(), 5));

        Assert.Equal(0, await _owner.RemapUsersAsync(new Dictionary<Guid, Guid> { [former] = _bob, [_bob] = former, [_alice] = _alice }, Ct));

        Assert.Equal(former, _fx.Store.Get(carols.Id)!.UserId);
        Assert.Equal(bobs, _fx.Store.Get(bobs.Id));
    }

    // ---------------------------------------------------------------------------------------------- names, counts

    [Fact]
    public async Task RefreshKnownUsers_TakesTheServersNames_KeepsTheUnknownIdsNames_DropsIdsWithoutRatings_WritingOnlyOnAChange()
    {
        var carol = Guid.NewGuid();
        var dave = Guid.NewGuid();
        await UpsertAsync(StoreFixture.Movie(_alice, Guid.NewGuid(), 6));
        await UpsertAsync(StoreFixture.Movie(carol, Guid.NewGuid(), 6));
        await RecordNamesAsync((_alice, "alice"), (carol, "carol"), (dave, "dave"));

        await _owner.RefreshKnownUsersAsync(new Dictionary<Guid, string> { [_alice] = "Alice Renamed", [_bob] = "bob" }, Ct);

        var stored = JsonSerializer.Deserialize<KnownUsers>(_fx.Store.GetMeta(RatingsUserDataOwner.KnownUsersKey)!)!;
        Assert.Equal((2, "Alice Renamed", "carol"), (stored.Count, stored.NameOf(_alice), stored.NameOf(carol)));
        var generation = _fx.Store.Generation;
        await _owner.RefreshKnownUsersAsync(new Dictionary<Guid, string> { [_alice] = "Alice Renamed", [_bob] = "bob" }, Ct);
        Assert.Equal(generation, _fx.Store.Generation);
    }

    [Fact]
    public async Task RecordName_OnAMembersWrite_WritesTheNameOnce_AndAnUnreadableValueNeverFailsTheWrite()
    {
        await _fx.Store.SetMetaAsync(RatingsUserDataOwner.KnownUsersKey, "{not json", Ct);
        await _fx.Store.WriteAsync(w => { RatingsUserDataOwner.RecordName(w, _alice, "alice"); return 0; }, Ct);
        Assert.Equal("alice", _owner.KnownUsers.NameOf(_alice));

        var writer = new Mock<IRatingsWriter>(MockBehavior.Strict);
        writer.Setup(w => w.GetMeta(RatingsUserDataOwner.KnownUsersKey)).Returns(_fx.Store.GetMeta(RatingsUserDataOwner.KnownUsersKey));
        RatingsUserDataOwner.RecordName(writer.Object, _alice, "alice");
        RatingsUserDataOwner.RecordName(writer.Object, Guid.Empty, "nobody");
        RatingsUserDataOwner.RecordName(writer.Object, _bob, "  ");
        writer.Verify(w => w.SetMeta(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CountRecordsByUser_CountsAttachedAndDetachedRatings_AndQueriesAgainOnlyAfterAWrite()
    {
        await UpsertAsync(StoreFixture.Movie(_alice, Guid.NewGuid(), 6));
        await UpsertAsync(StoreFixture.Movie(_alice, Guid.NewGuid(), 6) with { DetachedUtc = Created, DetachReason = RatingValues.DetachReason.Missing });
        await UpsertAsync(StoreFixture.Movie(_bob, Guid.NewGuid(), 6));
        var queries = 0;
        var store = new Mock<IRatingsStore>(MockBehavior.Strict);
        store.SetupGet(s => s.Generation).Returns(() => _fx.Store.Generation);
        store.Setup(s => s.CountByUser()).Returns(() =>
        {
            queries++;
            return _fx.Store.CountByUser();
        });
        store.Setup(s => s.GetMeta(RatingsUserDataOwner.KnownUsersKey)).Returns(() => _fx.Store.GetMeta(RatingsUserDataOwner.KnownUsersKey));
        var owner = new RatingsUserDataOwner(store.Object, _logger, _fx.Clock);

        var counts = owner.CountRecordsByUser();
        Assert.Equal((2, 1, 2), (counts[_alice], counts[_bob], counts.Count));
        Assert.Equal(2, owner.CountRecordsByUser()[_alice]);
        Assert.Empty(owner.KnownUsers);
        Assert.Equal(1, queries);

        await UpsertAsync(StoreFixture.Movie(_bob, Guid.NewGuid(), 6));
        Assert.Equal(2, owner.CountRecordsByUser()[_bob]);
        Assert.Equal(2, queries);
    }

    [Fact]
    public async Task TheNames_WhenTheStoreCantBeReadNow_AreTheLastOnesRead()
    {
        await UpsertAsync(StoreFixture.Movie(_alice, Guid.NewGuid(), 6));
        await RecordNamesAsync((_alice, "alice"));
        var generation = 1L;
        var store = new Mock<IRatingsStore>(MockBehavior.Strict);
        store.SetupGet(s => s.Generation).Returns(() => generation);
        store.Setup(s => s.CountByUser()).Returns(() => generation == 1 ? _fx.Store.CountByUser() : throw new PluginException(PluginErrorKind.Unavailable, "busy"));
        store.Setup(s => s.GetMeta(RatingsUserDataOwner.KnownUsersKey)).Returns(() => _fx.Store.GetMeta(RatingsUserDataOwner.KnownUsersKey));
        var owner = new RatingsUserDataOwner(store.Object, _logger, _fx.Clock);
        Assert.Equal("alice", owner.KnownUsers.NameOf(_alice));

        generation = 2;
        Assert.Equal("alice", owner.KnownUsers.NameOf(_alice));
        Assert.Throws<PluginException>(owner.CountRecordsByUser);
    }

    [Fact]
    public async Task SnapshotBefore_TakesThePreOpSnapshot_AndAnswersItsFileName()
    {
        await UpsertAsync(StoreFixture.Movie(_alice, Guid.NewGuid(), 6));

        var name = await _owner.SnapshotBeforeAsync(SnapshotOperations.Remap, Ct);

        Assert.NotNull(name);
        Assert.Equal(name, Path.GetFileName(name));
        Assert.StartsWith("userratings-remap-", name, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_fx.Snapshots, "pre-op", name)));
    }

    // ------------------------------------------------------------------------------------------------------ helpers

    private UserReconciler Reconciler(Dictionary<Guid, string> serverUsers, DeletedUserPolicy policy)
        => new(
            () => serverUsers,
            [_owner],
            new KitUserMarks(_fx.Directory.Combine("userratings", "kit-users.json"), _logger, _fx.Clock),
            () => policy,
            _logger,
            _fx.Clock);

    private async Task<RatingRecord> UpsertAsync(RatingRecord record)
    {
        await _fx.Store.UpsertAsync(record, Ct);
        return _fx.Store.Get(record.Id)!;
    }

    private Task RecordNamesAsync(params (Guid Id, string Name)[] names)
        => _fx.Store.WriteAsync(
            w =>
            {
                foreach (var (id, name) in names)
                {
                    RatingsUserDataOwner.RecordName(w, id, name);
                }

                return 0;
            },
            Ct);

    private Guid Original(Guid formerId)
        => Guid.Parse(_fx.Database.Read(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT OriginalUserId FROM FormerUsers WHERE Id = $Id;";
            command.Parameters.AddWithValue("$Id", formerId.ToString("N"));
            return (string)command.ExecuteScalar()!;
        }));
}
