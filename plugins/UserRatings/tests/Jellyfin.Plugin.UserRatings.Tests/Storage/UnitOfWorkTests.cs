using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Storage;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Storage;

/// <summary>
/// The store's unit of work (<see cref="IRatingsStore.WriteAsync{T}"/>): every operation on the kit
/// writer's open transaction, all or nothing; the keyed upsert (<c>ON CONFLICT(UserId, ItemId) WHERE DetachedUtc IS NULL DO
/// UPDATE</c>), so a double click or two tabs is last-write-wins rather than a 409; <c>EditCount</c> and
/// <c>ReviewUpdatedUtc</c> in the same statement; the write's generation; and <see cref="IRatingsStore.Changed"/> after the
/// commit with the items touched.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class UnitOfWorkTests : IDisposable
{
    private static readonly Guid Alex = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Sam = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly Guid Matrix = Guid.Parse("22222222-0000-0000-0000-000000000001");
    private static readonly Guid Other = Guid.Parse("22222222-0000-0000-0000-000000000002");

    private readonly StoreFixture _fx = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IdentitySnapshot MatrixIdentity => StoreFixture.Movie(Alex, Matrix).Identity;

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task AUnitOfWork_IsOneTransaction_AllOrNothing()
    {
        await _fx.Store.UpsertAsync(StoreFixture.Movie(Sam, Other, 4), Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _fx.Store.WriteAsync<int>(
            w =>
            {
                w.SaveStars(Alex, MatrixIdentity, 7, StoreFixture.T0);
                w.Delete(w.GetAttached(Sam, Other)!.Id, RatingValues.DeletedBy.Moderator, StoreFixture.T0);
                w.AppendModerationLog(new ModerationLogRecord { AtUtc = StoreFixture.T0, Action = RatingValues.ModerationAction.RemoveEntry });
                throw new InvalidOperationException("the unit stops here");
            },
            Ct));

        // Nothing of it: no new row, the deleted one back, no tombstone, no log row.
        Assert.Null(_fx.Store.GetAttached(Alex, Matrix));
        Assert.NotNull(_fx.Store.GetAttached(Sam, Other));
        Assert.Equal((0L, 0L), (_fx.Count("Tombstones"), _fx.Count("ModerationLog")));

        // The same unit, completed: the moderator's delete and its log row together.
        var log = await _fx.Store.WriteAsync(
            w =>
            {
                var row = w.GetAttached(Sam, Other)!;
                w.Delete(row.Id, RatingValues.DeletedBy.Moderator, StoreFixture.T0);
                return w.AppendModerationLog(new ModerationLogRecord { AtUtc = StoreFixture.T0, Action = RatingValues.ModerationAction.RemoveEntry, RatingId = row.Id });
            },
            Ct);
        Assert.Equal(1L, log);
        Assert.Equal((1L, 1L, 0L), (_fx.Count("Tombstones"), _fx.Count("ModerationLog"), _fx.Count("Ratings")));
    }

    [Fact]
    public async Task TheUnitOfWork_ReturnsItsWritesGeneration()
    {
        var before = _fx.Store.Generation;

        var generation = await _fx.Store.WriteAsync(w => w.SaveStars(Alex, MatrixIdentity, 7, StoreFixture.T0) is not null ? w.Generation : -1, Ct);

        Assert.Equal(before + 1, generation);
        Assert.Equal(generation, _fx.Store.Generation);
    }

    [Fact]
    public async Task SaveStars_IsKeyedByMemberAndItem_ADoubleClickIsTheLastWrite_NeverA409()
    {
        // No read first: the SQL's own conflict clause keeps one row and its id.
        var first = await _fx.Store.WriteAsync(w => w.SaveStars(Alex, MatrixIdentity, 7, StoreFixture.T0), Ct);
        var second = await _fx.Store.WriteAsync(w => w.SaveStars(Alex, MatrixIdentity, 9, StoreFixture.T0.AddSeconds(1)), Ct);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal((9, StoreFixture.T0, StoreFixture.T0.AddSeconds(1)), (second.Stars2, second.CreatedUtc, second.UpdatedUtc));
        Assert.Equal(1, _fx.Count("Ratings"));
        Assert.Equal(second, _fx.Store.Get(first.Id));
        Assert.Equal(["Movie|Imdb|tt0133093", "Movie|Tmdb|603"], _fx.Store.GetKeys(first.Id));
    }

    [Fact]
    public async Task TwoTabs_WritingAtOnce_LeaveOneRow_TheLastWrite()
    {
        var writes = Enumerable.Range(1, 10)
            .Select(i => Task.Run(() => _fx.Store.WriteAsync(w => (Row: w.SaveStars(Alex, MatrixIdentity, i, StoreFixture.T0.AddSeconds(i)), w.Generation), Ct), Ct))
            .ToList();
        var results = await Task.WhenAll(writes);

        // Ten writes, none refused, one row; the stored value is the one of the write that committed last.
        Assert.Single(results.Select(r => r.Row.Id).Distinct());
        Assert.Equal(1, _fx.Count("Ratings"));
        Assert.Equal(10, results.Select(r => r.Generation).Distinct().Count());
        var last = results.MaxBy(r => r.Generation);
        Assert.Equal(last.Row, _fx.Store.GetAttached(Alex, Matrix));
        Assert.Equal(last.Generation, _fx.Store.Generation);
    }

    [Fact]
    public async Task SaveStars_KeepsTheReview_AndSaveReview_KeepsTheStars()
    {
        await _fx.Store.WriteAsync(w => w.SaveReview(Alex, MatrixIdentity, "Words.", true, StoreFixture.T0), Ct);
        var row = await _fx.Store.WriteAsync(w => w.SaveStars(Alex, MatrixIdentity, 8, StoreFixture.T0.AddMinutes(1)), Ct);

        Assert.Equal((8, "Words.", true), (row.Stars2, row.ReviewText, row.IsSpoiler));
        Assert.Equal(StoreFixture.T0, row.ReviewUpdatedUtc);

        row = await _fx.Store.WriteAsync(w => w.SaveReview(Alex, MatrixIdentity, "Words.", false, StoreFixture.T0.AddMinutes(2)), Ct);
        Assert.Equal((8, false, 0), (row.Stars2, row.IsSpoiler, row.EditCount));
    }

    [Fact]
    public async Task EditCount_CountsChangesOfAnExistingText_InTheSameStatement()
    {
        // Stars first, then the first review: not an edit.
        await _fx.Store.WriteAsync(w => w.SaveStars(Alex, MatrixIdentity, 7, StoreFixture.T0), Ct);
        var first = await _fx.Store.WriteAsync(w => w.SaveReview(Alex, MatrixIdentity, "First.", false, StoreFixture.T0.AddMinutes(1)), Ct);
        Assert.Equal((0, StoreFixture.T0.AddMinutes(1)), (first.EditCount, first.ReviewUpdatedUtc));

        // The same text again with the spoiler flag changed: no edit ("(edited)" is for other words), but the review's time
        // moves with UpdatedUtc (the flag is part of the review, so a window that read it
        // before gets the 409 of IfReviewUpdatedUtc). The same text and flag again: only UpdatedUtc moves.
        var flagged = await _fx.Store.WriteAsync(w => w.SaveReview(Alex, MatrixIdentity, "First.", true, StoreFixture.T0.AddMinutes(2)), Ct);
        Assert.Equal((0, StoreFixture.T0.AddMinutes(2), StoreFixture.T0.AddMinutes(2), true), (flagged.EditCount, flagged.ReviewUpdatedUtc, flagged.UpdatedUtc, flagged.IsSpoiler));
        var same = await _fx.Store.WriteAsync(w => w.SaveReview(Alex, MatrixIdentity, "First.", true, StoreFixture.T0.AddMinutes(2.5)), Ct);
        Assert.Equal((0, StoreFixture.T0.AddMinutes(2), StoreFixture.T0.AddMinutes(2.5)), (same.EditCount, same.ReviewUpdatedUtc, same.UpdatedUtc));

        // A changed text: one edit, twice: two.
        await _fx.Store.WriteAsync(w => w.SaveReview(Alex, MatrixIdentity, "Second.", true, StoreFixture.T0.AddMinutes(3)), Ct);
        var third = await _fx.Store.WriteAsync(w => w.SaveReview(Alex, MatrixIdentity, "Third.", true, StoreFixture.T0.AddMinutes(4)), Ct);
        Assert.Equal((2, StoreFixture.T0.AddMinutes(4)), (third.EditCount, third.ReviewUpdatedUtc));
        Assert.Equal(7, third.Stars2);
    }

    [Fact]
    public async Task ClearStars_AndClearReview_KeepTheOtherHalf_AndRefuseARowWithoutIt()
    {
        var row = await _fx.Store.WriteAsync(w => w.SaveStars(Alex, MatrixIdentity, 7, StoreFixture.T0), Ct);
        row = await _fx.Store.WriteAsync(w => w.SaveReview(Alex, MatrixIdentity, "Words.", true, StoreFixture.T0), Ct);
        await _fx.Store.WriteAsync(w => w.SaveReview(Alex, MatrixIdentity, "Edited.", true, StoreFixture.T0), Ct);

        var starOnly = await _fx.Store.WriteAsync(w => w.ClearReview(row.Id, StoreFixture.T0.AddHours(1)), Ct);
        Assert.Equal((7, null, false, 0, null), (starOnly.Stars2, starOnly.ReviewText, starOnly.IsSpoiler, starOnly.EditCount, starOnly.ReviewUpdatedUtc));

        // A row without its review can't lose its stars this way: that is a delete (with a tombstone).
        await Assert.ThrowsAsync<InvalidOperationException>(() => _fx.Store.WriteAsync(w => w.ClearStars(row.Id, StoreFixture.T0), Ct));
        Assert.Equal(7, _fx.Store.Get(row.Id)!.Stars2);

        await _fx.Store.WriteAsync(w => w.SaveReview(Alex, MatrixIdentity, "Back.", false, StoreFixture.T0.AddHours(2)), Ct);
        var textOnly = await _fx.Store.WriteAsync(w => w.ClearStars(row.Id, StoreFixture.T0.AddHours(3)), Ct);
        Assert.Equal((null, "Back.", StoreFixture.T0.AddHours(3)), (textOnly.Stars2, textOnly.ReviewText, textOnly.UpdatedUtc));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _fx.Store.WriteAsync(w => w.ClearReview(row.Id, StoreFixture.T0), Ct));
    }

    [Fact]
    public async Task ADetachedRow_IsNoConflictForTheKeyedUpsert()
    {
        var detached = StoreFixture.Movie(Alex, Matrix, 3) with { DetachedUtc = StoreFixture.T0, DetachReason = RatingValues.DetachReason.Removed };
        await _fx.Store.UpsertAsync(detached, Ct);

        var attached = await _fx.Store.WriteAsync(w => w.SaveStars(Alex, MatrixIdentity, 8, StoreFixture.T0), Ct);

        Assert.NotEqual(detached.Id, attached.Id);
        Assert.Equal(2, _fx.Count("Ratings"));
        Assert.Equal(3, _fx.Store.Get(detached.Id)!.Stars2);
    }

    [Fact]
    public async Task ARelinkOntoAnItemTheMemberRatedAlready_IsStillThe409()
    {
        // The relink's detach-and-attach uses Upsert by id, which keeps the 409.
        await _fx.Store.WriteAsync(w => w.SaveStars(Alex, MatrixIdentity, 8, StoreFixture.T0), Ct);
        var detached = StoreFixture.Movie(Alex, Other, 3) with { DetachedUtc = StoreFixture.T0, DetachReason = RatingValues.DetachReason.Ambiguous };
        await _fx.Store.UpsertAsync(detached, Ct);

        var conflict = await Assert.ThrowsAsync<PluginException>(() => _fx.Store.WriteAsync(
            w =>
            {
                w.Upsert(detached with { Identity = detached.Identity with { ItemId = Matrix }, DetachedUtc = null, DetachReason = null });
                return 0;
            },
            Ct));

        Assert.Equal(PluginErrorKind.Conflict, conflict.Kind);
        Assert.Equal(Other, _fx.Store.Get(detached.Id)!.ItemId);
    }

    [Fact]
    public async Task Changed_NamesTheItemsTouched_AfterTheCommitAndTheGenerationsBump()
    {
        var moving = StoreFixture.Movie(Sam, Other, 4);
        await _fx.Store.UpsertAsync(moving, Ct);
        var events = new List<(IReadOnlyCollection<Guid> Items, bool Committed, long GenerationSeen)>();
        _fx.Store.Changed += (_, e) => events.Add((e.ItemIds.ToList(), e.Committed, _fx.Store.Generation));

        var generation = await _fx.Store.WriteAsync(
            w =>
            {
                w.SaveStars(Alex, MatrixIdentity, 7, StoreFixture.T0);
                w.Upsert(moving with { Identity = moving.Identity with { ItemId = Guid.Parse("22222222-0000-0000-0000-000000000003") } });
                return w.Generation;
            },
            Ct);

        var change = Assert.Single(events);
        Assert.True(change.Committed);
        Assert.Equal(generation, change.GenerationSeen);
        Assert.Equal(new[] { Matrix, Other, Guid.Parse("22222222-0000-0000-0000-000000000003") }.Order(), change.Items.Order());

        // A unit that rolls back still names what it touched (a drop for nothing costs one refill); one that only logs, none.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _fx.Store.WriteAsync<int>(
            w =>
            {
                w.SaveStars(Sam, MatrixIdentity, 2, StoreFixture.T0);
                throw new InvalidOperationException("no");
            },
            Ct));
        Assert.False(events[^1].Committed);
        Assert.Equal([Matrix], events[^1].Items);
        await _fx.Store.AppendModerationLogAsync(new ModerationLogRecord { AtUtc = StoreFixture.T0, Action = RatingValues.ModerationAction.ImportJe }, Ct);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task TheSingleOperationMethods_AreUnitsOfWorkToo_AndRaiseChanged()
    {
        var items = new List<Guid>();
        _fx.Store.Changed += (_, e) => items.AddRange(e.ItemIds);
        var record = StoreFixture.Movie(Alex, Matrix);

        await _fx.Store.UpsertAsync(record, Ct);
        await _fx.Store.UpsertManyAsync([StoreFixture.Movie(Sam, Other)], Ct);
        await _fx.Store.DeleteAsync(record.Id, RatingValues.DeletedBy.User, StoreFixture.T0, Ct);

        Assert.Equal([Matrix, Other, Matrix], items);
    }

    [Fact]
    public async Task AWriterOperationRefusesWhatTheStoreRefuses()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _fx.Store.WriteAsync(w => w.SaveStars(Alex, MatrixIdentity, 11, StoreFixture.T0), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _fx.Store.WriteAsync(w => w.SaveReview(Alex, MatrixIdentity, "a\0b", false, StoreFixture.T0), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _fx.Store.WriteAsync(w => w.SaveReview(Alex, MatrixIdentity, string.Empty, false, StoreFixture.T0), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _fx.Store.WriteAsync(w => w.SaveStars(Guid.Empty, MatrixIdentity, 5, StoreFixture.T0), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _fx.Store.WriteAsync(w => w.Delete(Guid.NewGuid(), "Robot", StoreFixture.T0), Ct));
        Assert.Equal(0, _fx.Count("Ratings"));
    }
}
