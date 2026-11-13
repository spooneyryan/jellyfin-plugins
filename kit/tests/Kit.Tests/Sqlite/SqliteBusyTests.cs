using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Sqlite;

/// <summary>
/// A database another connection keeps locked, and snapshots a restore can't read: busy for more than
/// <see cref="SqliteDatabase.CommandTimeoutSeconds"/> is a 503 <see cref="SqliteMessages.Busy"/> within about 5 s on
/// reads, writes, the export and the upgrade at open, with the writer lock released and nothing marked; it used to be a
/// raw "database is locked" 500 after about 34 s (Microsoft.Data.Sqlite's 30 s command timeout over
/// <c>busy_timeout=5000</c>). A snapshot the server may not read is passed over and named in the banner with one
/// Warning; one in use is tried again, logged once at Warning and then at Debug, its name in the 503.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteBusyTests : IDisposable
{
    // "Busy > 5 s → 503": the 5 s of SQLite's busy handler, the last 150 ms retry of Microsoft.Data.Sqlite's loop, and
    // room for a slow machine.
    private static readonly TimeSpan BusyLimit = TimeSpan.FromSeconds(7);

    // The 5 s budget with a margin of 4 s for a machine under load, for the write behind BEGIN IMMEDIATE: it answered after
    // 7.0017 s once while the other test projects ran in parallel, a race with BusyLimit that said nothing about the
    // budget. Still far below the 30 s command timeout's 34 s, and below two budgets in a row (a writer lock that wasn't
    // released).
    private static readonly TimeSpan BusyBudgetUnderLoad = TimeSpan.FromSeconds(SqliteDatabase.CommandTimeoutSeconds) + TimeSpan.FromSeconds(4);

    private readonly SqliteFixture _fx = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void EveryConnection_WaitsAtMostTheCommandTimeout()
    {
        var database = _fx.Open();

        Assert.Equal(5, SqliteDatabase.CommandTimeoutSeconds);
        Assert.Equal(5, database.Read(c => c.DefaultTimeout));
        Assert.Equal(5000L, database.Read(c => SqliteFixture.Scalar(c, "PRAGMA busy_timeout;")));

        // A checkpoint that resets the WAL cuts the file to at most 4 MiB.
        Assert.Equal(SqliteDatabase.JournalSizeLimitBytes, database.Read(c => SqliteFixture.Scalar(c, "PRAGMA journal_size_limit;")));
        Assert.Equal(4194304L, SqliteDatabase.JournalSizeLimitBytes);
    }

    [Fact]
    public async Task ThreeWritesAtOnce_WhileAnotherConnectionHoldsBeginImmediate_AllAnswer503Busy_NoneQueuedBehindTheOthers()
    {
        // The writes behind the first used to wait for the writer lock without a limit, so they answered at
        // 5.6, 11.1 and 16.7 s, one after another (a client gives up at 30 s, and one member's queue held up everyone's).
        // Now a queued write waits for the writer lock only for what is left of its 5 s. The first to take the lock
        // keeps it for SQLite's 5 s busy wait and Microsoft.Data.Sqlite's last 150 ms retry, so a queued write that
        // began more than 150 ms after it (a thread-pool start on a loaded runner) gets the lock just before its own 5 s
        // run out and waits out the other connection itself: 503 after about 10 s (9.16 s on CI), not a queue, since the
        // third's 5 s ran out long before. What is checked is that: at least one write was refused for waiting on the
        // writer lock (none was, before), each within its 5 s, and none answered later than two waits in a row.
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 1);
        int waitedOut;

        using (Hold(_fx.File, "BEGIN IMMEDIATE; INSERT INTO Parents (Id, Name) VALUES (900, 'held');"))
        {
            var clock = Stopwatch.StartNew();
            var writes = Enumerable.Range(0, 3).Select(i => Task.Run(async () =>
            {
                var error = await Assert.ThrowsAsync<PluginException>(() => database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Parents (Id, Name) VALUES (" + (910 + i) + ", 'queued');"); return 0; }, Ct));
                return (Error: error, Elapsed: clock.Elapsed);
            })).ToList();
            var answers = await Task.WhenAll(writes);

            Assert.All(answers, a => Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.Busy), (a.Error.Kind, a.Error.Message)));

            // Refused at the writer lock (no SQLite error under it), or by SQLite's busy wait once it held the lock.
            waitedOut = answers.Count(a => a.Error.InnerException is null);
            Assert.InRange(waitedOut, 1, 2);
            Assert.All(answers.Where(a => a.Error.InnerException is null), a => Assert.True(a.Elapsed < BusyBudgetUnderLoad, "A write waiting for the writer lock answered after " + a.Elapsed));
            Assert.All(answers, a => Assert.True(a.Elapsed < BusyBudgetUnderLoad + TimeSpan.FromSeconds(SqliteDatabase.CommandTimeoutSeconds), "A queued write answered after " + a.Elapsed));
        }

        // The two that never got the writer lock changed nothing, marked nothing, and the next write goes through at once.
        Assert.Equal(SqliteDatabaseStatus.Ok, database.Status);
        Assert.Equal(1, SqliteFixture.Count(database, "Parents"));
        await database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Parents (Id, Name) VALUES (920, 'after');"); return 0; }, Ct);
        Assert.Equal(waitedOut, _fx.Logger.Entries.Count(e => e.Level == LogLevel.Information && e.Message.Contains("waited 5 s for the writes before it", StringComparison.Ordinal)));
        Assert.Equal(3 - waitedOut, _fx.Logger.At(LogLevel.Warning).Count(e => e.Message.Contains("locked for more than 5 s", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AWriteBehindALongWrite_Is503Busy_WithinTheBudget_WhileOneBehindAShortWriteGoesThrough()
    {
        var database = _fx.Open();
        using var inside = new SemaphoreSlim(0);
        using var letGo = new SemaphoreSlim(0);

        // A write of this process that keeps the writer lock longer than the budget (a large import, say).
        var longWrite = Task.Run(() => database.WriteAsync(
            (c, t) =>
            {
                SqliteFixture.Execute(c, t, "INSERT INTO Parents (Id, Name) VALUES (1, 'long');");
                inside.Release();
                letGo.Wait(TimeSpan.FromSeconds(30));
                return 0;
            },
            Ct));
        await inside.WaitAsync(Ct);
        var clock = Stopwatch.StartNew();
        var behind = await Assert.ThrowsAsync<PluginException>(() => database.WriteAsync((c, t) => 0, Ct));
        clock.Stop();
        letGo.Release();
        await longWrite;

        Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.Busy), (behind.Kind, behind.Message));
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(4), BusyLimit);
        Assert.Equal(1, SqliteFixture.Count(database, "Parents"));

        // Behind a write that ends within the budget, the next one waits for it and goes through.
        var shortWrite = Task.Run(() => database.WriteAsync(
            (c, t) =>
            {
                inside.Release();
                Thread.Sleep(500);
                SqliteFixture.Execute(c, t, "INSERT INTO Parents (Id, Name) VALUES (2, 'short');");
                return 0;
            },
            Ct));
        await inside.WaitAsync(Ct);
        await database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Parents (Id, Name) VALUES (3, 'next');"); return 0; }, Ct);
        await shortWrite;
        Assert.Equal(3, SqliteFixture.Count(database, "Parents"));
    }

    [Fact]
    public async Task AWrite_WhileAnotherConnectionHoldsBeginImmediate_Is503Busy_WithinItsBudget_AndTheWriterLockIsReleased()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 2);

        PluginException busy;
        using (var holder = Hold(_fx.File, "BEGIN IMMEDIATE; INSERT INTO Parents (Id, Name) VALUES (900, 'held');"))
        {
            var clock = Stopwatch.StartNew();
            busy = await Assert.ThrowsAsync<PluginException>(() => database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Parents (Id, Name) VALUES (901, 'mine');"); return 0; }, Ct));
            clock.Stop();

            Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.Busy), (busy.Kind, busy.Message));
            Assert.True(clock.Elapsed < BusyBudgetUnderLoad, "The busy write took " + clock.Elapsed);
            Assert.True(clock.Elapsed > TimeSpan.FromSeconds(3), "busy_timeout should have waited first: " + clock.Elapsed);
            Assert.IsType<SqliteException>(busy.InnerException);
            Assert.Contains(((SqliteException)busy.InnerException!).SqliteErrorCode, new[] { 5, 6 });

            // The writer lock was let go: a second write waits its own 5 s, not behind the first one's.
            clock.Restart();
            await Assert.ThrowsAsync<PluginException>(() => database.WriteAsync((c, t) => 0, Ct));
            Assert.True(clock.Elapsed < BusyBudgetUnderLoad, "The second busy write took " + clock.Elapsed);
        }

        // Never damage: still writable, no banner, the rows as they were, and the next write goes through at once.
        Assert.Equal(SqliteDatabaseStatus.Ok, database.Status);
        Assert.False(database.IsReadOnly);
        Assert.Null(database.StatusMessage);
        var quick = Stopwatch.StartNew();
        await database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Parents (Id, Name) VALUES (902, 'after');"); return 0; }, Ct);
        Assert.True(quick.Elapsed < TimeSpan.FromSeconds(2), "The write after the lock took " + quick.Elapsed);
        Assert.Equal(3, SqliteFixture.Count(database, "Parents"));

        var warnings = _fx.Logger.At(LogLevel.Warning).Where(e => e.Message.Contains("locked for more than 5 s", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Null(w.Exception));
        Assert.DoesNotContain(_fx.Logger.Entries, e => e.Level >= LogLevel.Error);
        Assert.DoesNotContain(_fx.Folder, busy.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AReadAndTheExport_WhileAnotherConnectionHoldsTheDatabaseExclusively_Are503Busy_InUnderSevenSeconds()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 2);

        // In WAL mode readers wait only for a connection in exclusive locking mode, which needs no other connection open
        // (the pool's idle ones hold the file's shared lock): released first.
        database.Release("test");
        using (Hold(_fx.File, "PRAGMA locking_mode=EXCLUSIVE; BEGIN EXCLUSIVE; INSERT INTO Parents (Id, Name) VALUES (900, 'held');"))
        {
            var clock = Stopwatch.StartNew();
            var read = Assert.Throws<PluginException>(() => SqliteFixture.Count(database, "Parents"));
            Assert.True(clock.Elapsed < BusyLimit, "The busy read took " + clock.Elapsed);
            Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.Busy), (read.Kind, read.Message));

            clock.Restart();
            var export = Assert.Throws<PluginException>(() => database.ExportData((c, t) => new System.Text.Json.Nodes.JsonObject()));
            Assert.True(clock.Elapsed < BusyLimit, "The busy export took " + clock.Elapsed);
            Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.Busy), (export.Kind, export.Message));
        }

        Assert.Equal(SqliteDatabaseStatus.Ok, database.Status);
        Assert.Equal(2, SqliteFixture.Count(database, "Parents"));
        Assert.Equal(2, _fx.Logger.At(LogLevel.Warning).Count(e => e.Message.Contains("locked for more than 5 s", StringComparison.Ordinal)));
        Assert.DoesNotContain(_fx.Logger.Entries, e => e.Level >= LogLevel.Error && e.Message.Contains("damaged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUpgrade_WhileAnotherConnectionHoldsTheDatabase_IsTriedAgainLater_NeverAFailedUpgrade()
    {
        var v1 = _fx.Open(SqliteFixture.V1);
        SqliteFixture.AddFamilies(v1, 2);
        v1.Dispose();
        var v2 = _fx.Open();

        using (Hold(_fx.File, "BEGIN IMMEDIATE; INSERT INTO Parents (Id, Name) VALUES (900, 'held');"))
        {
            Assert.Equal(SqliteDatabaseStatus.Unavailable, v2.Open());
            Assert.Equal(SqliteMessages.Busy, v2.StatusMessage);
            var write = await Assert.ThrowsAsync<PluginException>(() => v2.WriteAsync((c, t) => 0, Ct));
            Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.Busy), (write.Kind, write.Message));
        }

        _fx.Clock.Advance(SqliteDatabase.OpenRetryInterval);
        Assert.Equal(SqliteDatabaseStatus.Migrated, v2.Open());
        Assert.Equal(2, v2.SchemaVersion);
        Assert.Equal(2, SqliteFixture.Count(v2, "Parents"));
        Assert.Contains(_fx.Logger.At(LogLevel.Warning), e => e.Message.Contains("Did not upgrade", StringComparison.Ordinal) && e.Message.Contains("locked", StringComparison.Ordinal));
        Assert.DoesNotContain(_fx.Logger.Entries, e => e.Message.Contains("everything was rolled back and the data is read-only", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------- snapshots a restore can't read

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ASnapshotTheServerMayNotRead_IsPassedOver_AndNamedInTheBanner_WithOneWarning()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Denying a file's reading needs a Windows ACL.");
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 2);
        var older = Path.GetFileName((await database.SnapshotAsync(7, Ct))!);
        _fx.Clock.Advance(TimeSpan.FromDays(1));
        SqliteFixture.AddFamilies(database, 2, from: 10);
        var newer = (await database.SnapshotAsync(7, Ct))!;
        database.Dispose();
        File.Delete(_fx.File);

        using (DenyReading(newer))
        {
            var restarted = _fx.Open();

            Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
            Assert.Equal(older, restarted.LastRecovery!.RestoredFrom);
            Assert.Equal([Path.GetFileName(newer)], restarted.LastRecovery.SnapshotsTried);
            Assert.Equal([Path.GetFileName(newer) + " (no permission to read it)"], restarted.LastRecovery.PassedOver);
            Assert.Contains("Passed over first: " + Path.GetFileName(newer) + " (no permission to read it).", restarted.StatusMessage, StringComparison.Ordinal);
            Assert.Equal(2, SqliteFixture.Count(restarted, "Parents"));
            Assert.Single(_fx.Logger.At(LogLevel.Warning), e => e.Message.Contains("no permission to read it", StringComparison.Ordinal));
            Assert.DoesNotContain(_fx.Logger.Entries, e => e.Message.Contains("couldn't be read for the restore", StringComparison.Ordinal));
            restarted.Dispose();

            // Alone, it leaves the data missing and read-only with the banner: every use answers at once, logged once.
            File.Delete(_fx.File);
            File.Delete(Path.Combine(_fx.Snapshots, older));
            var alone = _fx.Open();
            var warningsBefore = _fx.Logger.At(LogLevel.Warning).Count();
            for (var i = 0; i < 5; i++)
            {
                Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, alone.Open());
                var read = Assert.Throws<PluginException>(() => SqliteFixture.Count(alone, "Parents"));
                Assert.Equal(SqliteMessages.Missing, read.Message);
                _fx.Clock.Advance(SqliteDatabase.OpenRetryInterval);
            }

            Assert.Contains("(tried: " + Path.GetFileName(newer) + " (no permission to read it))", alone.StatusMessage, StringComparison.Ordinal);
            Assert.Equal(1, _fx.Logger.At(LogLevel.Warning).Count() - warningsBefore);
            Assert.False(File.Exists(_fx.File));

            // An admin's restore of it says why, as a conflict (trying again doesn't help).
            var named = await Assert.ThrowsAsync<PluginException>(() => alone.RecoverAsync(Path.GetFileName(newer), Ct));
            Assert.Equal(PluginErrorKind.Conflict, named.Kind);
            Assert.Contains("no permission to read it", named.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task RestoreSnapshot_OfASnapshotTheServerMayNotRead_IsAConflictThatSaysWhy()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Denying a file's reading needs a Windows ACL.");
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 2);
        var snapshot = (await database.SnapshotAsync(7, Ct))!;

        using (DenyReading(snapshot))
        {
            var refused = await Assert.ThrowsAsync<PluginException>(() => database.RestoreSnapshotAsync(Path.GetFileName(snapshot), Ct));
            Assert.Equal(PluginErrorKind.Conflict, refused.Kind);
            Assert.Contains("no permission to read it", refused.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_fx.Folder, refused.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(SqliteDatabaseStatus.Ok, database.Status);
    }

    [Fact]
    public async Task ASnapshotInUse_IsTriedAgain_LoggedOnceAtWarningThenAtDebug_AndThe503NamesIt()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 3);
        var snapshot = (await database.SnapshotAsync(7, Ct))!;
        var name = Path.GetFileName(snapshot);
        database.Dispose();
        File.Delete(_fx.File);
        var restarted = _fx.Open();

        using (new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            for (var i = 0; i < 5; i++)
            {
                Assert.Equal(SqliteDatabaseStatus.Unavailable, restarted.Open());
                var read = Assert.Throws<PluginException>(() => SqliteFixture.Count(restarted, "Parents"));
                Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.SnapshotInUse(name)), (read.Kind, read.Message));
                Assert.Equal(SqliteMessages.SnapshotInUse(name), restarted.StatusMessage);
                _fx.Clock.Advance(SqliteDatabase.OpenRetryInterval);
            }
        }

        Assert.DoesNotContain(_fx.Folder, SqliteMessages.SnapshotInUse(name), StringComparison.OrdinalIgnoreCase);
        Assert.Single(_fx.Logger.At(LogLevel.Warning), e => e.Message.Contains("couldn't be read for the restore", StringComparison.Ordinal));
        Assert.Equal(4, _fx.Logger.At(LogLevel.Debug).Count(e => e.Message.Contains("still couldn't be read for the restore", StringComparison.Ordinal)));
        Assert.Single(_fx.Logger.At(LogLevel.Error), e => e.Message.Contains("Could not open the database", StringComparison.Ordinal));
        Assert.DoesNotContain(_fx.Logger.At(LogLevel.Warning), e => e.Exception is not null);

        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        Assert.Equal(3, SqliteFixture.Count(restarted, "Parents"));
        Assert.Null(restarted.Describe()["Error"]);
        Assert.Contains(_fx.Logger.At(LogLevel.Information), e => e.Message.Contains("after an earlier failure", StringComparison.Ordinal));
    }

    // Another connection, unpooled, that runs the statements and keeps its locks until disposed (its transaction rolls back).
    private static SqliteConnection Hold(string file, string sql)
    {
        var holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        holder.Open();
        SqliteFixture.Execute(holder, null, "PRAGMA busy_timeout=0;");
        SqliteFixture.Execute(holder, null, sql);
        return holder;
    }

    // Denies the current user reading a file until disposed (a Windows ACL), as the JSON store's tests do.
    [SupportedOSPlatform("windows")]
    private static Denial DenyReading(string path)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl();
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        security.AddAccessRule(rule);
        file.SetAccessControl(security);
        return new Denial(file, rule);
    }

    [SupportedOSPlatform("windows")]
    private sealed class Denial(FileInfo file, FileSystemAccessRule rule) : IDisposable
    {
        public void Dispose()
        {
            file.Refresh();
            if (!file.Exists)
            {
                return;
            }

            var security = file.GetAccessControl();
            security.RemoveAccessRule(rule);
            file.SetAccessControl(security);
        }
    }
}
