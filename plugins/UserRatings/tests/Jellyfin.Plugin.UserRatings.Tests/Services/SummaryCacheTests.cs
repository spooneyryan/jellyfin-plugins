using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Services;

/// <summary>
/// <see cref="SummaryCache"/>: exact averages and the histogram,
/// the entry dropped by every write and refilled once, and no stale value under a fill racing a write in any completion order
/// (deterministic interleavings through <see cref="SummaryCache.AfterQuery"/>, a unit of work held open and a handler of
/// the store's <c>Changed</c> that runs before the cache's); hiding a user changes the next summary with no cache call; bulk
/// writes, a deleted-user policy run and a relink leave no stale entry either.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class SummaryCacheTests : IDisposable
{
    private static readonly Guid Alex = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Sam = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly Guid Kim = Guid.Parse("11111111-0000-0000-0000-000000000003");
    private static readonly Guid Matrix = Guid.Parse("22222222-0000-0000-0000-000000000001");

    private readonly StoreFixture _fx = new();
    private readonly SummaryCache _cache;

    public SummaryCacheTests() => _cache = new SummaryCache(_fx.Store);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IdentitySnapshot MatrixIdentity => StoreFixture.Movie(Alex, Matrix).Identity;

    private static VisibilityPolicy Everyone => new(false, false, new Dictionary<Guid, RatingAuthor> { [Alex] = default, [Sam] = default, [Kim] = default }, new Dictionary<Guid, string>());

    public void Dispose()
    {
        _cache.Dispose();
        _fx.Dispose();
    }

    [Fact]
    public async Task Summaries_AreExactAverages_WithTheHistogram()
    {
        await _fx.Store.UpsertManyAsync([StoreFixture.Movie(Alex, Matrix, 7), StoreFixture.Movie(Sam, Matrix, 8, "Words."), StoreFixture.Movie(Kim, Matrix, null, "Only words.")], Ct);

        var summary = Everyone.Summarize(_cache.Get(Matrix));

        Assert.Equal(3.75, summary.Average);
        Assert.Equal((2, 2), (summary.Count, summary.ReviewCount));
        Assert.Equal([0, 0, 0, 0, 0, 0, 1, 1, 0, 0], summary.Distribution);
        Assert.Equal(RatingSummary.Empty, Everyone.Summarize(_cache.Get(Guid.NewGuid())));
        Assert.Null(RatingSummary.Empty.Average);
    }

    [Fact]
    public async Task AnItemsEntry_IsDroppedByEveryWrite_AndRefilledOnce()
    {
        Assert.Empty(_cache.Get(Matrix));
        Assert.Equal((1, 1L), (_cache.Count, _cache.Fills));
        _cache.Get(Matrix);
        Assert.Equal(1L, _cache.Fills);

        // Create, update, delete: each drops the entry (the store's Changed), the next read refills it with what was written.
        var created = await _fx.Store.WriteAsync(w => w.SaveStars(Alex, MatrixIdentity, 7, StoreFixture.T0), Ct);
        Assert.Equal(0, _cache.Count);
        Assert.Equal(3.5, Everyone.Summarize(_cache.Get(Matrix)).Average);

        await _fx.Store.WriteAsync(w => w.SaveStars(Alex, MatrixIdentity, 10, StoreFixture.T0), Ct);
        Assert.Equal(5.0, Everyone.Summarize(_cache.Get(Matrix)).Average);

        await _fx.Store.DeleteAsync(created.Id, RatingValues.DeletedBy.User, StoreFixture.T0, Ct);
        Assert.Equal(RatingSummary.Empty, Everyone.Summarize(_cache.Get(Matrix)));
        Assert.Equal(4L, _cache.Fills);
    }

    [Fact]
    public async Task AFillWhoseQueryRanBeforeAWrite_ButWhichInsertsAfterIt_IsDiscarded()
    {
        // (a) The fill's query sees the old state; the write commits, bumps the generation and drops (nothing yet); then the
        // fill's check finds the generation changed and inserts nothing.
        await _fx.Store.UpsertAsync(StoreFixture.Movie(Sam, Matrix, 2), Ct);
        _cache.AfterQuery = _ => _fx.Store.WriteAsync(w => w.SaveStars(Alex, MatrixIdentity, 10, StoreFixture.T0), Ct).GetAwaiter().GetResult();

        var seen = _cache.Get(Matrix);
        _cache.AfterQuery = null;

        Assert.Single(seen);
        Assert.Equal((0, 1L), (_cache.Count, _cache.Discarded));
        Assert.Equal(3.0, Everyone.Summarize(_cache.Get(Matrix)).Average);
    }

    [Fact]
    public async Task AFillThatCompletesWhileAWritesTransactionIsOpen_IsDroppedWhenTheWriteCommits()
    {
        // Reversed completion order: the write starts first and holds its transaction open; the fill runs entirely meanwhile
        // (WAL: it reads the committed state) and inserts; the write then commits, and its Changed drops that entry.
        await _fx.Store.UpsertAsync(StoreFixture.Movie(Sam, Matrix, 2), Ct);
        using var written = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var write = Task.Run(
            () => _fx.Store.WriteAsync(
                w =>
                {
                    w.SaveStars(Alex, MatrixIdentity, 10, StoreFixture.T0);
                    written.Set();
                    release.Wait(Ct);
                    return 0;
                },
                Ct),
            Ct);
        written.Wait(Ct);

        Assert.Single(_cache.Get(Matrix));
        Assert.Equal(1, _cache.Count);

        release.Set();
        await write;

        Assert.Equal(0, _cache.Count);
        Assert.Equal(2, _cache.Get(Matrix).Count);
        Assert.Equal(0L, _cache.Discarded);
    }

    [Fact]
    public async Task AFillThatInsertsAfterTheCommit_ButBeforeTheDrop_IsDiscarded_AndOneThatReadAfterTheBumpIsKeptThenDropped()
    {
        // The other completion order: the fill queried before the commit and reaches its insert in the window between the
        // write's generation bump and the write's drop (a handler of Changed that runs before the cache's holds the window).
        using var store = new StoreFixture();
        await store.Store.UpsertAsync(StoreFixture.Movie(Sam, Matrix, 2), Ct);
        using var queried = new ManualResetEventSlim();
        using var inWindow = new ManualResetEventSlim();
        using var fillDone = new ManualResetEventSlim();
        var lateFillInserted = -1;
        SummaryCache? cache = null;
        store.Store.Changed += (_, _) =>
        {
            inWindow.Set();
            fillDone.Wait(Ct);

            // Still in the window: a fill now reads the bumped generation, so its query sees the write; it inserts, and the
            // drop that follows removes it (harmless: one refill).
            var late = cache!.Get(Matrix);
            lateFillInserted = cache.Count;
            Assert.Equal(2, late.Count);
        };
        cache = new SummaryCache(store.Store);
        cache.AfterQuery = _ =>
        {
            queried.Set();
            inWindow.Wait(Ct);
        };

        var fill = Task.Run(() => cache.Get(Matrix), Ct);
        queried.Wait(Ct);
        var write = Task.Run(() => store.Store.WriteAsync(w => w.SaveStars(Alex, MatrixIdentity, 10, StoreFixture.T0), Ct), Ct);
        var seen = await fill;
        cache.AfterQuery = null;
        fillDone.Set();
        await write;

        Assert.Single(seen);
        Assert.Equal(1L, cache.Discarded);
        Assert.Equal(1, lateFillInserted);
        Assert.Equal(0, cache.Count);
        Assert.Equal(6.0 / 2, Everyone.Summarize(cache.Get(Matrix)).Average);
        cache.Dispose();
    }

    [Fact]
    public async Task ManyFillsAndWritesAtOnce_LeaveNoStaleEntry()
    {
        var items = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToList();
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(
            () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    _cache.Get(items);
                }
            },
            Ct)).ToList();
        for (var i = 0; i < 60; i++)
        {
            var item = items[i % items.Count];
            var stars = 1 + (i % 10);
            await _fx.Store.WriteAsync(w => w.SaveStars(Alex, MatrixIdentity with { ItemId = item }, stars, StoreFixture.T0), Ct);
        }

        await stop.CancelAsync();
        await Task.WhenAll(readers);

        // Whatever the cache holds now is what the database holds.
        var truth = _fx.Store.GetSummaryRows(items).ToLookup(r => r.ItemId);
        foreach (var (item, rows) in _cache.Get(items))
        {
            Assert.Equal(truth[item].OrderBy(r => r.UserId), rows.OrderBy(r => r.UserId));
        }
    }

    [Fact]
    public async Task HidingAUser_ChangesTheNextSummary_WithoutAnyCacheCall()
    {
        await _fx.Store.UpsertManyAsync([StoreFixture.Movie(Alex, Matrix, 2), StoreFixture.Movie(Sam, Matrix, 10)], Ct);
        var rows = _cache.Get(Matrix);
        var fills = _cache.Fills;

        var hidden = new VisibilityPolicy(true, true, new Dictionary<Guid, RatingAuthor> { [Alex] = new(true, false), [Sam] = default }, new Dictionary<Guid, string>());

        Assert.Equal(3.0, Everyone.Summarize(rows).Average);
        Assert.Equal(5.0, hidden.Summarize(_cache.Get(Matrix)).Average);
        Assert.Equal(fills, _cache.Fills);
    }

    [Fact]
    public async Task ABulkImport_ADeletedUserPolicy_AndARelink_LeaveNoStaleEntry()
    {
        // (c) Warm the cache for 10 items; a JE apply (one unit of work adding rows to them), the Delete policy for a member,
        // and a relink moving a row from one item to another each drop what they touched, so the next reads are right.
        var items = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        _cache.Get(items);
        Assert.Equal(10, _cache.Count);

        await _fx.Store.UpsertManyAsync(items.Select(i => StoreFixture.Movie(Sam, i, 6) with { Origin = RatingValues.Origin.JellyfinEnhanced }).ToList(), Ct);
        Assert.All(_cache.Get(items).Values, rows => Assert.Single(rows));

        await _fx.Store.UpsertManyAsync(items.Take(3).Select(i => StoreFixture.Movie(Kim, i, 4)).ToList(), Ct);
        _cache.Get(items);
        var kims = _fx.Store.GetSummaryRows(items).Where(r => r.UserId == Kim).ToList();
        await _fx.Store.WriteAsync(
            w =>
            {
                foreach (var row in kims)
                {
                    w.Delete(w.GetAttached(Kim, row.ItemId)!.Id, RatingValues.DeletedBy.UserDeleted, StoreFixture.T0);
                }

                return 0;
            },
            Ct);
        Assert.All(_cache.Get(items).Values, rows => Assert.DoesNotContain(rows, r => r.UserId == Kim));

        var moving = _fx.Store.GetAttached(Sam, items[0])!;
        await _fx.Store.WriteAsync(
            w =>
            {
                w.Upsert(moving with { Identity = moving.Identity with { ItemId = items[9] }, UserId = Alex });
                return 0;
            },
            Ct);
        var after = _cache.Get(items);
        Assert.Empty(after[items[0]]);
        Assert.Equal(2, after[items[9]].Count);
    }

    [Fact]
    public async Task Clear_DropsEverything_ForAChangeOutsideTheUnitsOfWork()
    {
        await _fx.Store.UpsertAsync(StoreFixture.Movie(Alex, Matrix, 7), Ct);
        _cache.Get(Matrix);

        // A bulk change outside the units of work: its caller clears (a restore needs no call, below).
        _cache.Clear();

        Assert.Equal(0, _cache.Count);
        Assert.Single(_cache.Get(Matrix));
    }

    [Fact]
    public async Task ARestore_EmptiesTheCache_AtTheNextRead_WhichGetsTheSnapshotsRows()
    {
        // A restore raised the generation, but an entry filled before it used to stay until a write named
        // its item; the kit's RestoreCount, compared on every read, starts the cache over.
        await _fx.Store.UpsertAsync(StoreFixture.Movie(Alex, Matrix, 4), Ct);
        var snapshot = System.IO.Path.GetFileName((await _fx.Database.SnapshotAsync(7, Ct))!);
        _fx.Clock.Advance(TimeSpan.FromHours(1));
        await _fx.Store.WriteAsync(w => w.SaveStars(Sam, MatrixIdentity, 10, StoreFixture.T0), Ct);
        Assert.Equal(3.5, Everyone.Summarize(_cache.Get(Matrix)).Average);
        var fills = _cache.Fills;

        await _fx.Database.RestoreSnapshotAsync(snapshot, Ct);

        var restored = Everyone.Summarize(_cache.Get(Matrix));
        Assert.Equal((1, 2.0), (restored.Count, restored.Average!.Value));
        Assert.Equal(fills + 1, _cache.Fills);

        // A fill whose query ran before a restore, inserting after it, is discarded like one that crossed a write.
        var again = System.IO.Path.GetFileName((await _fx.Database.SnapshotAsync(7, Ct))!);
        _fx.Clock.Advance(TimeSpan.FromHours(1));
        await _fx.Store.WriteAsync(w => w.SaveStars(Kim, MatrixIdentity, 6, StoreFixture.T0), Ct);
        _cache.AfterQuery = _ => _fx.Database.RestoreSnapshotAsync(again, Ct).GetAwaiter().GetResult();
        Assert.Equal(2, _cache.Get(Matrix).Count);
        _cache.AfterQuery = null;
        Assert.Equal(1, _cache.Discarded);
        Assert.Single(_cache.Get(Matrix));
    }
}

