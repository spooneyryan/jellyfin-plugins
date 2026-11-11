using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Sqlite;
using Jellyfin.Plugin.Kit.Storage;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Sqlite;

/// <summary>
/// The clock moved back (as the JSON store handles it, applied to the SQLite database): a restore tries the snapshots
/// dated more than a day after now last and names them; every incident (a restore at open, an admin's recovery, a start
/// empty) sets <c>.keep-snapshots</c> to the later of now and the newest snapshot's time plus a second, never earlier
/// than it was; and the daily and pre-operation rotation neither delete nor count a snapshot dated in the future.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteClockTests : IDisposable
{
    private readonly SqliteFixture _fx = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Now => _fx.Clock.GetUtcNow().UtcDateTime;

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task AMissingDatabase_WithTheClockMovedBackIntoItsSnapshots_TriesTheFutureOnesLast_AndKeepsEverySnapshot()
    {
        var start = Now;
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 1);
        var s0 = Path.GetFileName((await database.SnapshotAsync(7, Ct))!);
        _fx.Clock.Advance(TimeSpan.FromDays(3));
        SqliteFixture.AddFamilies(database, 2, from: 10);
        var s1 = Path.GetFileName((await database.SnapshotAsync(7, Ct))!);
        _fx.Clock.Advance(TimeSpan.FromDays(1));
        SqliteFixture.AddFamilies(database, 1, from: 20);
        var s2 = (await database.SnapshotAsync(7, Ct))!;
        database.Dispose();
        File.Delete(_fx.File);

        // Back to the day after the first snapshot: the other two are dated more than a day ahead.
        _fx.Clock.Set(new DateTimeOffset(start.AddDays(1)));
        var restarted = _fx.Open();

        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        Assert.Equal(s0, restarted.LastRecovery!.RestoredFrom);
        Assert.Equal(1, SqliteFixture.Count(restarted, "Parents"));
        Assert.Equal([Path.GetFileName(s2), s1], restarted.LastRecovery.DatedLater);
        Assert.Contains("Dated later than now, so tried last: " + Path.GetFileName(s2) + ", " + s1 + ".", restarted.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("they may hold newer data than the snapshot restored", restarted.StatusMessage, StringComparison.Ordinal);

        // The marker is the newest snapshot's time and a second, past now: every snapshot counts as earlier and is kept, by
        // daily rotations while the clock is behind and after it is right again.
        var marker = start.AddDays(4).AddSeconds(1);
        Assert.Equal(marker, restarted.KeepSnapshotsBeforeUtc);
        for (var i = 0; i < 3; i++)
        {
            _fx.Clock.Advance(TimeSpan.FromHours(1));
            await restarted.SnapshotAsync(1, Ct);
        }

        _fx.Clock.Set(new DateTimeOffset(start.AddDays(6)));
        for (var i = 0; i < 2; i++)
        {
            _fx.Clock.Advance(TimeSpan.FromHours(1));
            await restarted.SnapshotAsync(1, Ct);
        }

        var names = restarted.ListSnapshots().Select(s => s.Name).ToHashSet();
        Assert.Contains(s0, names);
        Assert.Contains(s1, names);
        Assert.Contains(Path.GetFileName(s2), names);
        Assert.All(restarted.ListSnapshots().Where(s => s.Utc < marker), s => Assert.True(s.Kept, s.Name));

        // An admin's restore of one of them releases them.
        await restarted.RestoreSnapshotAsync(Path.GetFileName(s2), Ct);
        Assert.Null(restarted.KeepSnapshotsBeforeUtc);
        Assert.Equal(4, SqliteFixture.Count(restarted, "Parents"));
    }

    [Fact]
    public async Task EveryIncident_SetsTheMarker_ForwardOnly()
    {
        var database = _fx.Open();
        SqliteFixture.AddBulk(database);
        SqliteFixture.AddFamilies(database, 2);
        var root = SqliteFixture.RootPage(database, "Bulk");
        var first = await database.SnapshotAsync(7, Ct);
        var firstUtc = Now;
        _fx.Clock.Advance(TimeSpan.FromHours(1));

        // An admin's recovery of damage found at run time is an incident: the marker is now (later than the snapshot).
        await DamageAsync(database, root);
        await database.RecoverAsync(null, Ct);
        var afterFirst = Now;
        Assert.Equal(afterFirst, database.KeepSnapshotsBeforeUtc);

        // The clock moves back two days, then another incident: the marker never moves back.
        _fx.Clock.Set(new DateTimeOffset(firstUtc.AddDays(-2)));
        await DamageAsync(database, root);
        await database.RecoverAsync(Path.GetFileName(first), Ct);
        Assert.Equal(afterFirst, database.KeepSnapshotsBeforeUtc);

        // A snapshot taken a day after the first incident, then a start empty: forward to that snapshot's time and a second.
        _fx.Clock.Set(new DateTimeOffset(afterFirst.AddDays(1)));
        await database.SnapshotAsync(7, Ct);
        var newest = Now;
        _fx.Clock.Set(new DateTimeOffset(afterFirst.AddHours(2)));
        await DamageAsync(database, root);
        await database.RecoverAsync(null, startEmpty: true, Ct);
        Assert.Equal(SqliteDatabaseStatus.RecoveredEmpty, database.Status);
        Assert.Equal(newest.AddSeconds(1), database.KeepSnapshotsBeforeUtc);
        Assert.True(File.Exists(database.KeepSnapshotsMarkerPath));
    }

    [Fact]
    public async Task DailyRotation_NeitherDeletesNorCountsASnapshotDatedInTheFuture()
    {
        var start = Now;
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 1);
        _fx.Clock.Advance(TimeSpan.FromDays(10));
        var future = Path.GetFileName((await database.SnapshotAsync(7, Ct))!);

        // The clock is put right (ten days back); two daily snapshots keeping one.
        _fx.Clock.Set(new DateTimeOffset(start));
        await database.SnapshotAsync(1, Ct);
        _fx.Clock.Advance(TimeSpan.FromHours(1));
        var newest = Path.GetFileName((await database.SnapshotAsync(1, Ct))!);

        Assert.Equal(new[] { future, newest }.Order(StringComparer.Ordinal), database.ListSnapshots().Select(s => s.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task PreOperationRotation_NeitherDeletesNorCountsASnapshotDatedInTheFuture()
    {
        var start = Now;
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 1);
        _fx.Clock.Advance(TimeSpan.FromDays(10));
        var future = Path.GetFileName((await database.PreOpSnapshotAsync(SnapshotOperations.Import, Ct))!);

        _fx.Clock.Set(new DateTimeOffset(start));
        for (var i = 0; i < SnapshotOperations.ToKeep + 1; i++)
        {
            _fx.Clock.Advance(TimeSpan.FromMinutes(1));
            await database.PreOpSnapshotAsync(SnapshotOperations.Import, Ct);
        }

        var preOp = database.ListSnapshots().Where(s => s.Kind == SnapshotKind.PreOp).ToList();
        Assert.Equal(SnapshotOperations.ToKeep + 1, preOp.Count);
        Assert.Contains(preOp, s => s.Name == future);
        Assert.Equal(SnapshotOperations.ToKeep, preOp.Count(s => s.Utc <= Now));
    }

    [Fact]
    public void TheRotationsFutureTolerance_IsADay()
    {
        var now = new DateTime(2026, 10, 20, 3, 20, 0, DateTimeKind.Utc);
        var within = new StoreSnapshot("s-20261021-032000.json", "x", now.AddDays(1), 0, 0, null);
        var beyond = new StoreSnapshot("s-20261021-032001.json", "x", now.AddDays(1).AddSeconds(1), 0, 0, null);

        Assert.False(SnapshotRotation.IsFutureDated(within, now));
        Assert.True(SnapshotRotation.IsFutureDated(beyond, now));
    }

    private static async Task DamageAsync(SqliteDatabase database, long root)
    {
        database.Release("test");
        SqliteFixture.CorruptPage(database.FilePath, root);
        Assert.False(database.QuickCheck()!.Ok);
        await Task.CompletedTask;
    }
}
