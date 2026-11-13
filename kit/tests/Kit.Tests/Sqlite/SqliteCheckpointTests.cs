using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Sqlite;

/// <summary>
/// The requested checkpoint: <see cref="SqliteDatabase.RequestCheckpoint"/> returns at once and the checkpoint runs in
/// the background, holding the writer lock only while it copies frames, never while another connection's older snapshot
/// is waited for, so a write queued behind it is never a 503. One a reader kept from finishing is tried again; when its
/// tries run out it is logged at Warning and stays pending (<c>Checkpoint.Pending</c> in Diagnostics) until a later
/// write or the maintenance completes it, and only then is the deleted text out of the <c>-wal</c>.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteCheckpointTests : IDisposable
{
    private const string Marker = "Kit checkpoint marker: the kettle sang twice.";

    private readonly SqliteFixture _fx = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task ARequestedCheckpoint_ReturnsAtOnce_ThenCutsTheWal_SoTheDeletedTextIsInNoFile()
    {
        var database = Open();
        await WriteAndDeleteMarkerAsync(database);
        Assert.Contains(Path.GetFileName(_fx.File) + "-wal", FilesHolding(_fx.File + "-wal"));

        var watch = Stopwatch.StartNew();
        var checkpoint = database.RequestCheckpoint();
        Assert.True(watch.ElapsedMilliseconds < 100, "RequestCheckpoint waited for the checkpoint");
        Assert.True(database.CheckpointPending);

        Assert.True(await checkpoint.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.False(database.CheckpointPending);
        Assert.Equal(0L, new FileInfo(_fx.File + "-wal").Length);
        Assert.Empty(FilesHolding(_fx.File, _fx.File + "-wal"));
        var described = database.DescribeCheckpoint();
        Assert.Equal((false, false, "Complete", 0), ((bool)described["Pending"]!, (bool)described["Running"]!, (string?)described["LastOutcome"], (int)described["FailedRounds"]!));
        Assert.Equal(described["Requested"], described["Completed"]);
        Assert.True(await database.WaitForCheckpointAsync(Ct));
    }

    [Fact]
    public async Task WithAReaderOnAnOlderSnapshot_AWriteQueuedBehindTheCheckpointGoesThrough_AndTheCheckpointStaysPendingWithAWarning()
    {
        // Regression: before, CheckpointAsync held the writer lock through its 5 s busy wait for the reader, and a
        // write queued behind it was a 503 after 5 s, while every caller ignored the false it answered.
        // The round's last try (the first and its two retries) waits after it tried until the writes are done: a round
        // that ran out while they went on would let the next of them start a second round (the pending checkpoint tried
        // again after a write), and the round below would be Running, on a slow runner. The hook runs outside the
        // writer lock, so the writes are never held by it.
        using var writesDone = new ManualResetEventSlim();
        var tries = 0;
        var database = Open(
            retries: [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)],
            hook: step =>
            {
                if (step == "tried" && Interlocked.Increment(ref tries) == 3)
                {
                    writesDone.Wait(TimeSpan.FromSeconds(30));
                }
            });
        await WriteAndDeleteMarkerAsync(database);
        using var reader = HoldReader();

        var checkpoint = database.RequestCheckpoint();
        var slowest = TimeSpan.Zero;
        for (var i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            await database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Recent (Payload) VALUES ('queued');"); return 0; }, Ct);
            slowest = watch.Elapsed > slowest ? watch.Elapsed : slowest;
            await Task.Delay(40, Ct);
        }

        writesDone.Set();

        // None was a 503 (above), and none waited out the 5 s busy wait the regression held the writer lock through.
        Assert.True(slowest < TimeSpan.FromSeconds(4), "a write waited for the checkpoint: " + slowest.TotalMilliseconds + " ms");
        Assert.False(await checkpoint.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        // Pending, and said once at Warning; the -wal still holds the earlier version of the page.
        Assert.True(database.CheckpointPending);
        var described = database.DescribeCheckpoint();
        Assert.Equal((true, false, 1), ((bool)described["Pending"]!, (bool)described["Running"]!, (int)described["FailedRounds"]!));
        Assert.Contains((string?)described["LastOutcome"], new[] { "Readers", "Writer" });
        Assert.Single(_fx.Logger.At(LogLevel.Warning), e => e.Message.Contains("couldn't finish", StringComparison.Ordinal) && e.Message.Contains("stays pending", StringComparison.Ordinal));
        Assert.NotEmpty(FilesHolding(_fx.File + "-wal"));
        Assert.False(await database.WaitForCheckpointAsync(Ct));

        // The reader goes; the next write tries the pending checkpoint again, and it completes.
        reader.Dispose();
        await database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Recent (Payload) VALUES ('later');"); return 0; }, Ct);
        Assert.True(await database.WaitForCheckpointAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.False(database.CheckpointPending);
        Assert.Empty(FilesHolding(_fx.File, _fx.File + "-wal"));
    }

    [Fact]
    public async Task AReaderThatGoesBeforeTheTriesRunOut_LetsTheSameRoundComplete()
    {
        var database = Open(retries: [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400)]);
        await WriteAndDeleteMarkerAsync(database);
        var reader = HoldReader();

        var checkpoint = database.RequestCheckpoint();
        await Task.Delay(150, Ct);
        Assert.True(database.CheckpointPending);
        reader.Dispose();

        Assert.True(await checkpoint.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Empty(FilesHolding(_fx.File, _fx.File + "-wal"));
        Assert.Empty(_fx.Logger.At(LogLevel.Warning));
    }

    [Fact]
    public async Task ABurstOfRequests_IsOneRound_AndCheckpointAsync_TheMaintenancesBackstop_CompletesAPendingOne()
    {
        var database = Open(retries: [TimeSpan.FromMilliseconds(50)], delay: TimeSpan.FromMilliseconds(100));
        await WriteAndDeleteMarkerAsync(database);

        var rounds = Enumerable.Range(0, 5).Select(_ => database.RequestCheckpoint()).ToList();
        Assert.True(rounds.All(r => ReferenceEquals(r, rounds[0])), "one round for the burst");
        Assert.True(await rounds[0].WaitAsync(TimeSpan.FromSeconds(10), Ct));

        // A pending one whose tries ran out: the maintenance's CheckpointAsync completes it once the reader is gone.
        await WriteAndDeleteMarkerAsync(database);
        using (HoldReader())
        {
            Assert.False(await database.RequestCheckpoint().WaitAsync(TimeSpan.FromSeconds(10), Ct));
        }

        Assert.True(database.CheckpointPending);
        Assert.True(await database.CheckpointAsync(Ct));
        Assert.False(database.CheckpointPending);
        Assert.Empty(FilesHolding(_fx.File, _fx.File + "-wal"));
    }

    [Fact]
    public async Task AWriteHoldingTheWriterLock_IsNeverWaitedFor_TheTryIsWriter_AndTheRoundCompletesAfterIt()
    {
        // The try takes the writer lock with Wait(0): a write that holds it makes the try "Writer" at once, to be tried
        // again, never a checkpoint thread queued on the lock (a negative control: queued on the lock, this test fails).
        var database = Open(retries: [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2)]);
        await WriteAndDeleteMarkerAsync(database);
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var holder = Task.Run(() => database.WriteAsync((c, t) => { entered.Set(); gate.Wait(TimeSpan.FromSeconds(20)); return 0; }, Ct), Ct);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), Ct));

        var checkpoint = database.RequestCheckpoint();
        var watch = Stopwatch.StartNew();
        while ((string?)database.DescribeCheckpoint()["LastOutcome"] is null && watch.ElapsedMilliseconds < 1000)
        {
            await Task.Delay(10, Ct);
        }

        var described = database.DescribeCheckpoint();
        Assert.Equal(("Writer", true, true), ((string?)described["LastOutcome"], (bool)described["Pending"]!, (bool)described["Running"]!));
        Assert.False(checkpoint.IsCompleted);

        gate.Set();
        await holder;
        Assert.True(await checkpoint.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.False(database.CheckpointPending);
        Assert.Empty(FilesHolding(_fx.File, _fx.File + "-wal"));
        Assert.Empty(_fx.Logger.At(LogLevel.Warning));
    }

    [Fact]
    public async Task WhileReadOnly_NothingIsWritten_AndTheRequestStaysPending()
    {
        var database = Open();
        SqliteFixture.AddFamilies(database, 1);
        database.Dispose();
        var newer = _fx.Open(SqliteFixture.V1);
        Assert.True(newer.IsReadOnly);

        Assert.False(await newer.RequestCheckpoint().WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.True(newer.CheckpointPending);
        Assert.Equal("ReadOnly", newer.DescribeCheckpoint()["LastOutcome"]);
        Assert.False(await newer.CheckpointAsync(Ct));
    }

    [Fact]
    public async Task ADatabaseDisposedDuringARound_EndsIt_WithNoException()
    {
        var database = Open(retries: [TimeSpan.FromSeconds(30)]);
        await WriteAndDeleteMarkerAsync(database);
        Task<bool> checkpoint;
        using (HoldReader())
        {
            checkpoint = database.RequestCheckpoint();
            await Task.Delay(200, Ct);
            database.Dispose();
        }

        Assert.False(await checkpoint.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.False(await database.RequestCheckpoint().WaitAsync(TimeSpan.FromSeconds(1), Ct));
        Assert.Empty(_fx.Logger.At(LogLevel.Warning));
    }

    [Fact]
    public async Task AKillWhileACheckpointIsPending_TheNextOpenAsksForOne_AndTheTextLeavesTheFileAndTheWal()
    {
        // A request is in memory only. Before, a process killed while one waited left the deleted text in the database
        // file, and the next start knew of nothing due, so only the next deletion, the daily maintenance or SQLite's
        // automatic checkpoint at 1000 pages took it out.
        var database = Open(delay: TimeSpan.FromSeconds(30), retries: [TimeSpan.FromMilliseconds(50)]);
        SqliteFixture.AddRecent(database, 20);
        await database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Recent (Payload) VALUES ('" + Marker + "');"); return 0; }, Ct);
        CheckpointOnOwnConnection(_fx.File);
        Assert.Equal([Path.GetFileName(_fx.File)], FilesHolding(_fx.File, _fx.File + "-wal"));
        await database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "DELETE FROM Recent WHERE Payload LIKE 'Kit checkpoint marker%';"); return 0; }, Ct);
        _ = database.RequestCheckpoint();
        Assert.True(database.CheckpointPending);

        // The kill: the files as they are now.
        var crashed = Path.Combine(_fx.Directory.Path, "crashed");
        var file = Path.Combine(crashed, "userratings.db");
        SqliteFixture.CopyOpenFile(_fx.File, file);
        SqliteFixture.CopyOpenFile(_fx.File + "-wal", file + "-wal");
        Assert.Contains(Path.GetFileName(file), FilesHolding(file));
        Assert.True(new FileInfo(file + "-wal").Length > 0);

        var restarted = Open(file: file, delay: TimeSpan.FromMilliseconds(200));
        Assert.Equal(20L, SqliteFixture.Count(restarted, "Recent"));
        Assert.Equal(1L, (long)restarted.DescribeCheckpoint()["Requested"]!);
        Assert.True(await restarted.WaitForCheckpointAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.False(restarted.CheckpointPending);
        Assert.Equal(0L, new FileInfo(file + "-wal").Length);
        Assert.Empty(FilesHolding(file, file + "-wal"));

        // An open that finds the -wal empty asks for nothing.
        restarted.Dispose();
        var again = Open(file: file);
        Assert.Equal(20L, SqliteFixture.Count(again, "Recent"));
        Assert.Equal(0L, (long)again.DescribeCheckpoint()["Requested"]!);
    }

    [Fact]
    public async Task ARequestMadeAfterATryLetTheWriterLockGo_IsNotCountedAsDone_UntilACheckpointAfterItCompletes()
    {
        // A try counts the requests made before it began. A write that commits after the try let the writer lock go,
        // and the request that follows it, before the try's outcome is counted, isn't covered by that checkpoint:
        // marking every request done would leave the write's frames, and a deleted row's earlier page versions, in the
        // -wal with nothing pending.
        var interleaved = 0;
        SqliteDatabase? database = null;
        database = Open(hook: stage =>
        {
            if (stage == "tried" && Interlocked.Exchange(ref interleaved, 1) == 0)
            {
                database!.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Recent (Payload) VALUES ('" + Marker + " again');"); return 0; }, CancellationToken.None).GetAwaiter().GetResult();
                database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "DELETE FROM Recent WHERE Payload LIKE 'Kit checkpoint marker%';"); return 0; }, CancellationToken.None).GetAwaiter().GetResult();
                _ = database.RequestCheckpoint();
            }
        });
        await WriteAndDeleteMarkerAsync(database);

        Assert.True(await database.RequestCheckpoint().WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(1, interleaved);
        var described = database.DescribeCheckpoint();
        Assert.Equal((2L, 2L, false), ((long)described["Requested"]!, (long)described["Completed"]!, (bool)described["Pending"]!));
        Assert.Equal(0L, new FileInfo(_fx.File + "-wal").Length);
        Assert.Empty(FilesHolding(_fx.File, _fx.File + "-wal"));
    }

    [Fact]
    public async Task Dispose_WaitsForATryThatHoldsTheWriterLock_AndForAWriteThatHoldsIt()
    {
        // Dispose waits for the writer lock (at most CommandTimeoutSeconds), so a checkpoint's try or a write running
        // now ends before the connections are released and the lock is disposed.
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var tryEnded = 0L;
        var database = Open(hook: stage =>
        {
            if (stage == "locked")
            {
                entered.Set();
                gate.Wait(TimeSpan.FromSeconds(10));
                Interlocked.Exchange(ref tryEnded, Stopwatch.GetTimestamp());
            }
        });
        await WriteAndDeleteMarkerAsync(database);
        var checkpoint = database.RequestCheckpoint();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), Ct));

        var disposing = Task.Run(database.Dispose, Ct);
        await Task.Delay(300, Ct);
        Assert.False(disposing.IsCompleted, "Dispose didn't wait for the try that holds the writer lock");
        gate.Set();
        await disposing.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.InRange(Interlocked.Read(ref tryEnded), 1, Stopwatch.GetTimestamp());
        Assert.False(await checkpoint.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        // A write: Dispose returns after it committed, and the write ends without an exception.
        using var writing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var otherFile = Path.Combine(_fx.Directory.Path, "other", "userratings.db");
        var other = Open(file: otherFile);
        SqliteFixture.AddRecent(other, 1);
        var write = Task.Run(() => other.WriteAsync((c, t) => { writing.Set(); release.Wait(TimeSpan.FromSeconds(10)); SqliteFixture.Execute(c, t, "INSERT INTO Recent (Payload) VALUES ('last');"); return 0; }, Ct), Ct);
        Assert.True(writing.Wait(TimeSpan.FromSeconds(10), Ct));
        var disposingOther = Task.Run(other.Dispose, Ct);
        await Task.Delay(300, Ct);
        Assert.False(disposingOther.IsCompleted, "Dispose didn't wait for the write that holds the writer lock");
        release.Set();
        await write.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await disposingOther.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(2L, SqliteFixture.Count(Open(file: otherFile), "Recent"));
    }

    [Fact]
    public void Diagnostics_DescribesTheCheckpoint()
    {
        var database = Open();
        var checkpoint = Assert.IsAssignableFrom<IDictionary<string, object?>>(database.Describe()["Checkpoint"]);
        Assert.Equal(
            ["Completed", "FailedRounds", "LastCompleteUtc", "LastOutcome", "LastTryUtc", "Pending", "Requested", "Running"],
            checkpoint.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((false, 0L, 0L), ((bool)checkpoint["Pending"]!, (long)checkpoint["Requested"]!, (long)checkpoint["Completed"]!));
    }

    private SqliteDatabase Open(IReadOnlyList<TimeSpan>? retries = null, TimeSpan? delay = null, string? file = null, Action<string>? hook = null)
        => _fx.Open(file: file, checkpointDelay: delay ?? TimeSpan.FromMilliseconds(10), checkpointRetries: retries ?? [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)], checkpointHook: hook);

    // The database file made to hold what the -wal has, on a connection of the test's own.
    private static void CheckpointOnOwnConnection(string file)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read() && reader.GetInt64(0) == 0);
    }

    private static async Task WriteAndDeleteMarkerAsync(SqliteDatabase database)
    {
        SqliteFixture.AddRecent(database, 20);
        await database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Recent (Payload) VALUES ('" + Marker + "');"); return 0; }, Ct);
        await database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "DELETE FROM Recent WHERE Payload LIKE 'Kit checkpoint marker%';"); return 0; }, Ct);
    }

    // Another connection in a read transaction on the snapshot before the delete, as a long read or a backup would hold.
    private Reader HoldReader()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _fx.File, Pooling = false }.ToString());
        connection.Open();
        var transaction = connection.BeginTransaction(deferred: true);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM Recent;";
            command.ExecuteScalar();
        }

        return new Reader(connection, transaction);
    }

    private static List<string> FilesHolding(params string[] files)
    {
        var needle = Encoding.UTF8.GetBytes("Kit checkpoint marker");
        return files.Where(File.Exists).Where(f => SqliteFixture.ReadOpenFile(f).AsSpan().IndexOf(needle) >= 0).Select(Path.GetFileName).ToList()!;
    }

    private sealed class Reader(SqliteConnection connection, SqliteTransaction transaction) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            transaction.Rollback();
            transaction.Dispose();
            connection.Dispose();
        }
    }
}
