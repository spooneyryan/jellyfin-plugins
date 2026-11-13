using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Sqlite;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Sqlite;

/// <summary>
/// Regression tests of the SQLite recovery: a database is never moved, replaced or started empty on uncertain evidence.
/// A transient read error (a byte-range lock another handle holds: on Windows SQLite retries about 1.4 s, then a check
/// reports "unable to get the page. error code=266") is <c>Unavailable</c> and tried again, never damage; a snapshot
/// that can't be read just then is "try again later"; with no snapshot passing nothing is moved and only the admin
/// starts empty; a missing or empty database with snapshots of its data is restored from them; a newer version's
/// upgrade still only in the <c>-wal</c> is never checkpointed; the safety snapshot of a restore or an import is taken
/// under the writer lock; a damaged database's WAL stays as found while it waits; a full disk; and the rebuild recipe
/// with a dependent view.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteRecoveryTests : IDisposable
{
    private const string LocksOnlyOnWindows = "A byte-range lock blocks other handles' reads only on Windows.";

    private readonly SqliteFixture _fx = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fx.Dispose();

    // ----------------------------------------------------------------------------------- a transient read error

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task AReadErrorAtOpen_IsUnavailable_NothingIsMoved_AndTheNextUseOpensTheDatabaseAsItIs()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), LocksOnlyOnWindows);
        var database = _fx.Open();
        SqliteFixture.AddBulk(database);
        SqliteFixture.AddFamilies(database, 3);
        await database.SnapshotAsync(7, Ct);
        _fx.Clock.Advance(TimeSpan.FromHours(20));
        SqliteFixture.AddFamilies(database, 5, from: 10);
        var root = SqliteFixture.RootPage(database, "Bulk");
        Assert.Equal("free", database.Release("test"));
        database.Dispose();
        var before = await File.ReadAllBytesAsync(_fx.File, Ct);
        var reopened = _fx.Open();

        using (var holder = new FileStream(_fx.File, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            holder.Lock((root - 1) * 4096, 4096);

            Assert.Equal(SqliteDatabaseStatus.Unavailable, reopened.Open());
            Assert.Null(reopened.LastRecovery);
            Assert.Empty(reopened.ListQuarantines());
            Assert.True(reopened.LastQuickCheck!.IsInconclusive);
            Assert.Contains("unable to get the page. error code=266", reopened.LastQuickCheck.IoError, StringComparison.Ordinal);
            Assert.Contains("unable to get the page", (string)reopened.Describe()["Error"]!, StringComparison.Ordinal);
            holder.Unlock((root - 1) * 4096, 4096);
        }

        Assert.Equal(before, SqliteFixture.ReadOpenFile(_fx.File));
        Assert.Equal(["userratings.db"], SqliteFixture.Names(_fx.Folder));
        Assert.DoesNotContain(_fx.Logger.At(LogLevel.Error), e => e.Message.Contains("damaged", StringComparison.Ordinal));

        _fx.Clock.Advance(SqliteDatabase.OpenRetryInterval);
        Assert.Equal(SqliteDatabaseStatus.Ok, reopened.Open());
        Assert.Equal((8L, 2000L), (SqliteFixture.Count(reopened, "Parents"), SqliteFixture.Count(reopened, "Bulk")));
        Assert.Empty(reopened.ListQuarantines());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AtRunTime_AQuickCheckOrAReadThatCantReadAPage_IsNoDamage_AndShowsNoBanner()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), LocksOnlyOnWindows);
        var database = _fx.Open();
        SqliteFixture.AddBulk(database);
        SqliteFixture.AddFamilies(database, 3);
        var root = SqliteFixture.RootPage(database, "Bulk");
        database.Release("test");

        using (var holder = new FileStream(_fx.File, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            holder.Lock((root - 1) * 4096, 4096);

            var check = database.QuickCheck()!;
            Assert.False(check.Ok);
            Assert.True(check.IsInconclusive);
            Assert.False(check.IsDamaged);
            Assert.Equal(check.IoError, check.ToDictionary()["IoError"]);
            var read = Assert.Throws<PluginException>(() => SqliteFixture.Count(database, "Bulk"));
            Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.ReadFailed), (read.Kind, read.Message));
            holder.Unlock((root - 1) * 4096, 4096);
        }

        Assert.Equal(SqliteDatabaseStatus.Ok, database.Status);
        Assert.False(database.IsReadOnly);
        Assert.Null(database.StatusMessage);
        Assert.Contains(_fx.Logger.At(LogLevel.Warning), e => e.Message.Contains("couldn't read part of the file", StringComparison.Ordinal));
        Assert.DoesNotContain(_fx.Logger.At(LogLevel.Error), e => e.Message.Contains("damaged", StringComparison.Ordinal));
        Assert.True(database.QuickCheck()!.Ok);
        Assert.Equal(2000, SqliteFixture.Count(database, "Bulk"));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task DamageThatTheSecondCheckCantRead_IsUnavailable_NothingIsMoved()
    {
        // Damage that quick_check finds is confirmed after a pause by integrity_check on a fresh connection before
        // anything moves; when that second check can't read a page, the open is tried again later.
        Assert.SkipWhen(!OperatingSystem.IsWindows(), LocksOnlyOnWindows);
        var database = _fx.Open();
        SqliteFixture.AddBulk(database);
        SqliteFixture.AddFamilies(database, 3);
        await database.SnapshotAsync(7, Ct);
        var bulk = SqliteFixture.RootPage(database, "Bulk");
        var parents = SqliteFixture.RootPage(database, "Parents");
        database.Dispose();
        SqliteFixture.CorruptPage(_fx.File, bulk);
        using var holder = new FileStream(_fx.File, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        // The lock comes after the first check and before the second: taken where the open says the first one failed
        // (RecoveryStepFault's "confirm"), not by a timer racing the pause, which a slow runner lost.
        var locked = 0;
        using var reopened = new SqliteDatabase(_fx.File, SqliteFixture.V2, _fx.Logger, _fx.Snapshots, _fx.Clock)
        {
            DamageRecheckDelay = TimeSpan.FromSeconds(1),
            RecoveryStepFault = step =>
            {
                // The first open's only: the open tried again later finds the file readable.
                if (step == "confirm" && ++locked == 1)
                {
                    holder.Lock((parents - 1) * 4096, 4096);
                }
            }
        };
        var status = reopened.Open();
        Assert.Equal(1, locked);
        holder.Unlock((parents - 1) * 4096, 4096);

        Assert.Equal(SqliteDatabaseStatus.Unavailable, status);
        Assert.Empty(reopened.ListQuarantines());
        Assert.Null(reopened.LastRecovery);
        Assert.Contains("that confirms damage couldn't read part of the file", (string)reopened.Describe()["Error"]!, StringComparison.Ordinal);
        Assert.Equal(["userratings.db"], SqliteFixture.Names(_fx.Folder));
        _fx.Clock.Advance(SqliteDatabase.OpenRetryInterval);
        Assert.Equal(0L, reopened.RestoreCount);
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, reopened.Open());
        Assert.Equal(1L, reopened.RestoreCount);

        // A cleanly stopped database: the empty -wal and the -shm our own connections made, in either attempt, stay out.
        Assert.Equal(["userratings.db"], reopened.LastRecovery!.QuarantinedFiles);
    }

    // ----------------------------------------------------------------- a snapshot that can't be read just then

    [Fact]
    public async Task SnapshotsThatCantBeReadAtOpen_AreTriedAgainLater_NeverAReasonToStartEmpty()
    {
        var database = _fx.Open();
        SqliteFixture.AddBulk(database);
        SqliteFixture.AddFamilies(database, 3);
        var daily = (await database.SnapshotAsync(7, Ct))!;
        _fx.Clock.Advance(TimeSpan.FromHours(1));
        var preOp = (await database.PreOpSnapshotAsync(SnapshotOperations.Import, Ct))!;
        var root = SqliteFixture.RootPage(database, "Bulk");
        database.Release("test");
        database.Dispose();
        SqliteFixture.CorruptPage(_fx.File, root);
        var reopened = _fx.Open();

        // Held open by another program (a backup tool); a full disk fails File.Copy and integrity_check the same way.
        using (new FileStream(daily, FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(preOp, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(SqliteDatabaseStatus.Unavailable, reopened.Open());
            Assert.Empty(reopened.ListQuarantines());
            Assert.Null(reopened.LastRecovery);
            Assert.True(File.Exists(_fx.File));
            Assert.Contains(Path.GetFileName(preOp) + " couldn't be read just now", (string)reopened.Describe()["Error"]!, StringComparison.Ordinal);
        }

        _fx.Clock.Advance(SqliteDatabase.OpenRetryInterval);
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, reopened.Open());
        Assert.Equal(Path.GetFileName(preOp), reopened.LastRecovery!.RestoredFrom);
        Assert.Empty(reopened.LastRecovery.SnapshotsTried);
        Assert.Equal(["userratings.db"], reopened.LastRecovery.QuarantinedFiles);
        Assert.Equal((3L, 2000L), (SqliteFixture.Count(reopened, "Parents"), SqliteFixture.Count(reopened, "Bulk")));
    }

    [Fact]
    public async Task AtRunTime_ASnapshotThatCantBeRead_Answers503_AndNothingIsMoved()
    {
        var database = _fx.Open();
        SqliteFixture.AddBulk(database);
        var snapshot = (await database.SnapshotAsync(7, Ct))!;
        var root = SqliteFixture.RootPage(database, "Bulk");
        database.Release("test");
        SqliteFixture.CorruptPage(_fx.File, root);
        Assert.False(database.QuickCheck()!.Ok);

        using (new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var recover = await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(null, Ct));
            Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.SnapshotInUse(Path.GetFileName(snapshot))), (recover.Kind, recover.Message));
            var named = await Assert.ThrowsAsync<PluginException>(() => database.RestoreSnapshotAsync(Path.GetFileName(snapshot), Ct));
            Assert.Equal(PluginErrorKind.Unavailable, named.Kind);
        }

        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Status);
        Assert.Empty(database.ListQuarantines());
        Assert.Equal(Path.GetFileName(snapshot), (await database.RecoverAsync(null, Ct)).RestoredFrom);
        Assert.Equal(2000, SqliteFixture.Count(database, "Bulk"));
    }

    // ----------------------------------------------------- no snapshot yet, damage at open: read-only, readable

    [Fact]
    public async Task NoSnapshotYet_DamageAtOpen_StaysReadOnlyCorrupt_WithItsUndamagedTablesReadable()
    {
        var database = _fx.Open();
        SqliteFixture.AddBulk(database);
        SqliteFixture.AddFamilies(database, 3);
        var root = SqliteFixture.RootPage(database, "Bulk");
        database.Release("test");
        SqliteFixture.CorruptPage(_fx.File, root);
        Assert.False(database.QuickCheck()!.Ok);
        Assert.Equal(3, SqliteFixture.Count(database, "Parents"));
        database.Dispose();

        var reopened = _fx.Open();

        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, reopened.Open());
        Assert.Equal(2, reopened.LoadedVersion);
        Assert.Contains("there is no snapshot yet", reopened.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(3, SqliteFixture.Count(reopened, "Parents"));
        Assert.Equal(SqliteMessages.Corrupt, Assert.Throws<PluginException>(() => SqliteFixture.Count(reopened, "Bulk")).Message);
        Assert.Equal(SqliteMessages.Corrupt, (await Assert.ThrowsAsync<PluginException>(() => reopened.WriteAsync((_, _) => 0, Ct))).Message);
        Assert.Equal(SqliteMessages.ExportCorrupt, Assert.Throws<PluginException>(() => reopened.ExportData((_, _) => new System.Text.Json.Nodes.JsonObject())).Message);
        Assert.Empty(reopened.ListQuarantines());
        Assert.Equal(PluginErrorKind.Conflict, (await Assert.ThrowsAsync<PluginException>(() => reopened.RecoverAsync(null, Ct))).Kind);

        // Only the admin's choice starts it empty.
        await reopened.RecoverAsync(null, startEmpty: true, Ct);
        Assert.Equal(SqliteDatabaseStatus.RecoveredEmpty, reopened.Status);
        await reopened.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Parents (Id, Name) VALUES (1, 'new');"); return 0; }, Ct);
        Assert.Equal(1, SqliteFixture.Count(reopened, "Parents"));
        Assert.Single(reopened.ListQuarantines());
    }

    // --------------------------------------------- while damaged: the WAL stays as found until the restore moves it

    [Fact]
    public async Task WhileDamaged_PooledConnectionsClosingLater_NeverCheckpointTheWalIntoTheDamagedFile()
    {
        // Microsoft.Data.Sqlite closes idle pooled connections after a few minutes (simulated: every pool cleared); the last
        // read-write one to close would checkpoint the WAL into the damaged file. A read-only connection held open while
        // the database is ReadOnlyCorrupt keeps the quarantine exact.
        var database = _fx.Open();
        SqliteFixture.AddBulk(database);
        var snapshot = Path.GetFileName((await database.SnapshotAsync(7, Ct))!);
        var root = SqliteFixture.RootPage(database, "Bulk");
        database.Release("test");
        SqliteFixture.AddRecent(database, 7);
        SqliteFixture.CorruptPage(_fx.File, root);
        var damaged = SqliteFixture.ReadOpenFile(_fx.File);
        var wal = SqliteFixture.ReadOpenFile(_fx.File + "-wal");
        Assert.NotEmpty(wal);

        Assert.False(database.QuickCheck()!.Ok);
        Assert.Equal(7, SqliteFixture.Count(database, "Recent"));
        SqliteConnection.ClearAllPools();
        Assert.Equal(7, SqliteFixture.Count(database, "Recent"));
        SqliteConnection.ClearAllPools();

        Assert.Equal(damaged, SqliteFixture.ReadOpenFile(_fx.File));
        Assert.Equal(wal, SqliteFixture.ReadOpenFile(_fx.File + "-wal"));
        var restores = database.RestoreCount;
        var recovery = await database.RecoverAsync(null, Ct);
        Assert.Equal(snapshot, recovery.RestoredFrom);
        Assert.Equal(restores + 1, database.RestoreCount);
        var quarantine = Path.Combine(_fx.Folder, recovery.QuarantineFolder!);
        Assert.Equal(damaged, await File.ReadAllBytesAsync(Path.Combine(quarantine, "userratings.db"), Ct));
        Assert.Equal(wal, await File.ReadAllBytesAsync(Path.Combine(quarantine, "userratings.db-wal"), Ct));
        Assert.Equal("free", database.Release("test"));
    }

    // ----------------------------------------------------------- a newer version's upgrade only in the -wal

    [Fact]
    public async Task ANewerVersionOnlyInTheWal_IsReadOnly_AndNeitherTheFileNorTheWalChanges()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 3);
        database.Release("test");

        // A newer plugin raises user_version, then the process dies before a checkpoint: the crash image.
        await database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "PRAGMA user_version = 3;"); return 0; }, Ct);
        var image = _fx.Directory.Combine("crash", "userratings.db");
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            SqliteFixture.CopyOpenFile(_fx.File + suffix, image + suffix);
        }

        database.Dispose();
        var before = SHA256.HashData(await File.ReadAllBytesAsync(image, Ct));
        var wal = await File.ReadAllBytesAsync(image + "-wal", Ct);
        Assert.Equal(2, SqliteFiles.ReadHeader(image)!.Value.UserVersion);

        var restarted = _fx.Open(file: image);

        Assert.Equal(SqliteDatabaseStatus.ReadOnlyNewerVersion, restarted.Open());
        Assert.Equal(3, restarted.LoadedVersion);
        Assert.Equal(3, SqliteFixture.Count(restarted, "Parents"));
        restarted.Release("test");
        restarted.Dispose();
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(image, Ct)));
        Assert.Equal(wal, await File.ReadAllBytesAsync(image + "-wal", Ct));
    }

    [Fact]
    public void ACrashImageWithoutItsShm_IsReadThroughItsWal()
    {
        // The read-only check of user_version at open rebuilds the WAL index itself when -shm is gone.
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 30);
        var image = _fx.Directory.Combine("crash", "userratings.db");
        SqliteFixture.CopyOpenFile(_fx.File, image);
        SqliteFixture.CopyOpenFile(_fx.File + "-wal", image + "-wal");
        Assert.True(new FileInfo(image + "-wal").Length > 0);

        var restarted = _fx.Open(file: image);

        Assert.Equal(SqliteDatabaseStatus.Ok, restarted.Open());
        Assert.Equal(30, SqliteFixture.Count(restarted, "Parents"));
    }

    // ------------------------------------------------------ the database missing, or empty, beside its snapshots

    [Fact]
    public async Task AMissingDatabase_WithSnapshots_IsRestoredFromTheNewest_NeverStartedEmpty()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 5);
        for (var day = 0; day < 3; day++)
        {
            await database.SnapshotAsync(3, Ct);
            _fx.Clock.Advance(TimeSpan.FromDays(1));
        }

        var newest = database.ListSnapshots()[0].Name;
        database.Release("test");
        database.Dispose();
        File.Delete(_fx.File);

        var restarted = _fx.Open();

        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        var recovery = restarted.LastRecovery!;
        Assert.Equal(newest, recovery.RestoredFrom);
        Assert.False(recovery.Damaged);
        Assert.Null(recovery.QuarantineFolder);
        Assert.Empty(restarted.ListQuarantines());
        Assert.StartsWith("The data wasn't there (the database file was missing); it was restored from the snapshot " + newest, restarted.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(5, SqliteFixture.Count(restarted, "Parents"));
        Assert.False(restarted.IsReadOnly);
        Assert.Single(_fx.Logger.At(LogLevel.Error), e => e.Message.Contains("had no data", StringComparison.Ordinal));

        // The rotation goes on over snapshots of the restored data.
        for (var day = 0; day < 3; day++)
        {
            await restarted.SnapshotAsync(3, Ct);
            _fx.Clock.Advance(TimeSpan.FromDays(1));
        }

        Assert.All(restarted.ListSnapshots(), s => Assert.Equal(5, SqliteFixture.FileRows(Path.Combine(_fx.Snapshots, s.Name), "Parents")));
    }

    [Fact]
    public async Task AMissingDatabase_WhoseSnapshotsAllFail_StaysReadOnly_CreatesNothing_AndRotatesNothing()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 5);
        var root = SqliteFixture.RootPage(database, "Parents");
        for (var day = 0; day < 3; day++)
        {
            await database.SnapshotAsync(3, Ct);
            _fx.Clock.Advance(TimeSpan.FromDays(1));
        }

        database.Release("test");
        database.Dispose();
        var snapshots = Directory.GetFiles(_fx.Snapshots, "*.db").Order(StringComparer.Ordinal).ToList();
        foreach (var snapshot in snapshots)
        {
            SqliteFixture.CorruptPage(snapshot, root);
        }

        File.Delete(_fx.File);
        await File.WriteAllTextAsync(_fx.File + "-wal", "another database's log", Ct);

        var restarted = _fx.Open();

        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, restarted.Open());
        Assert.Contains("tried: " + string.Join(", ", snapshots.Select(Path.GetFileName).Reverse()), restarted.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(SqliteMessages.Missing, Assert.Throws<PluginException>(() => SqliteFixture.Count(restarted, "Parents")).Message);
        Assert.Equal(SqliteMessages.Missing, (await Assert.ThrowsAsync<PluginException>(() => restarted.WriteAsync((_, _) => 0, Ct))).Message);
        Assert.Equal(SqliteMessages.Missing, Assert.Throws<PluginException>(() => restarted.ExportData((_, _) => new System.Text.Json.Nodes.JsonObject())).Message);
        Assert.False(restarted.QuickCheck()!.Ok);
        Assert.Null(await restarted.SnapshotAsync(3, Ct));
        Assert.Null(await restarted.PreOpSnapshotAsync(SnapshotOperations.Import, Ct));
        Assert.False(File.Exists(_fx.File), "Nothing created the database file.");
        Assert.All(snapshots, s => Assert.True(File.Exists(s)));

        // The -wal found without its database went into a quarantine folder first (the storage hardening),
        // though nothing else was moved: a good database copied back later must never replay it.
        Assert.False(File.Exists(_fx.File + "-wal"));
        var orphans = Assert.Single(restarted.ListQuarantines());
        Assert.Equal(["reason.txt", "userratings.db-wal"], orphans.Files);
        Assert.Equal("another database's log", await File.ReadAllTextAsync(Path.Combine(_fx.Folder, orphans.Name, "userratings.db-wal"), Ct));

        // The admin starts it empty: nothing is left to move aside, the snapshots stay kept.
        var recovery = await restarted.RecoverAsync(null, startEmpty: true, Ct);
        Assert.Equal(SqliteDatabaseStatus.RecoveredEmpty, restarted.Status);
        Assert.Empty(recovery.QuarantinedFiles);
        Assert.Null(recovery.QuarantineFolder);
        await restarted.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Parents (Id, Name) VALUES (1, 'new');"); return 0; }, Ct);
        for (var day = 0; day < 3; day++)
        {
            await restarted.SnapshotAsync(3, Ct);
            _fx.Clock.Advance(TimeSpan.FromDays(1));
        }

        Assert.All(snapshots, s => Assert.True(File.Exists(s)));
        Assert.Equal(3, restarted.ListSnapshots().Count(s => s.Kept));
    }

    [Fact]
    public async Task AMissingDatabase_WhoseOnlySnapshotIsInUse_IsUnavailable_NotStartedEmpty()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 5);
        var snapshot = (await database.SnapshotAsync(3, Ct))!;
        database.Dispose();
        File.Delete(_fx.File);
        var restarted = _fx.Open();

        using (new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(SqliteDatabaseStatus.Unavailable, restarted.Open());
            Assert.False(File.Exists(_fx.File));
        }

        _fx.Clock.Advance(SqliteDatabase.OpenRetryInterval);
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        Assert.Equal(5, SqliteFixture.Count(restarted, "Parents"));
    }

    [Fact]
    public async Task AnEmptyDatabaseFile_WithSnapshots_IsQuarantined_AndRestored()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 4);
        var snapshot = Path.GetFileName((await database.SnapshotAsync(3, Ct))!);
        database.Dispose();
        await File.WriteAllBytesAsync(_fx.File, [], Ct);

        var restarted = _fx.Open();

        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        Assert.Equal(snapshot, restarted.LastRecovery!.RestoredFrom);
        Assert.Contains("userratings.db", restarted.LastRecovery.QuarantinedFiles);
        Assert.Contains("schema 0, no tables", restarted.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(4, SqliteFixture.Count(restarted, "Parents"));
    }

    [Fact]
    public async Task SnapshotsWithoutData_DontStopANewDatabase()
    {
        // Only snapshots of this store with user_version 1 or more count: another store's, or a non-database file, don't.
        Directory.CreateDirectory(_fx.Snapshots);
        await File.WriteAllTextAsync(Path.Combine(_fx.Snapshots, "userratings-20261018-032000.db"), "not a database", Ct);
        await File.WriteAllTextAsync(Path.Combine(_fx.Snapshots, "bookmarks-20261018-032000.db"), "another store's", Ct);

        var database = _fx.Open();

        Assert.Equal(SqliteDatabaseStatus.Ok, database.Open());
        Assert.Null(database.LoadedVersion);
    }

    // ----------------------------------------------- the safety snapshot under the writer lock, a write racing it

    [Fact]
    public async Task RestoreSnapshot_WaitsForAWriteInProgress_AndItsSafetySnapshotHoldsThatWrite()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 3);
        var daily = Path.GetFileName((await database.SnapshotAsync(7, Ct))!);
        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        SqliteFixture.AddFamilies(database, 2, from: 4);
        using var inside = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var write = Task.Run(
            () => database.WriteAsync(
                (c, t) =>
                {
                    SqliteFixture.Execute(c, t, "INSERT INTO Parents (Id, Name) VALUES (100, 'committed while the admin restores');");
                    inside.Set();
                    go.Wait(Ct);
                    return 0;
                },
                Ct),
            Ct);
        inside.Wait(Ct);

        var restore = Task.Run(() => database.RestoreSnapshotAsync(daily, Ct), Ct);
        await Task.Delay(300, Ct);

        // No safety snapshot while the write holds the writer lock.
        Assert.False(restore.IsCompleted);
        Assert.Empty(SqliteFixture.Names(Path.Combine(_fx.Snapshots, "pre-op")));
        go.Set();
        await write;
        var result = await restore;

        var safety = Path.Combine(_fx.Snapshots, "pre-op", result.SafetySnapshot!);
        Assert.Equal(6, SqliteFixture.FileRows(safety, "Parents"));
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = safety, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(Ct);
            Assert.Equal(1L, SqliteFixture.Scalar(connection, "SELECT COUNT(*) FROM Parents WHERE Id = 100;"));
        }

        Assert.Equal(3, SqliteFixture.Count(database, "Parents"));
    }

    // ------------------------------------------------------------------------------------------- a full disk

    [Fact]
    public async Task AWriteThatFillsTheDisk_IsRolledBack_With503_AndTheDatabaseStaysWritable()
    {
        // SQLITE_FULL (13), as max_page_count gives it: the write is rolled back, nothing is marked, the next one works.
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 2);
        var generation = database.Generation;

        var full = await Assert.ThrowsAsync<PluginException>(() => database.WriteAsync(
            (c, t) =>
            {
                using (var count = c.CreateCommand())
                {
                    count.Transaction = t;
                    count.CommandText = "PRAGMA page_count;";
                    SqliteFixture.Execute(c, t, string.Create(CultureInfo.InvariantCulture, $"PRAGMA max_page_count = {(long)count.ExecuteScalar()!};"));
                }

                // SQLite rolls the whole transaction back itself here; the kit's rollback then has nothing to undo.
                for (var i = 0; i < 200; i++)
                {
                    SqliteFixture.Execute(c, t, "INSERT INTO Bulk (Payload) VALUES ('" + new string('x', 1000) + "');");
                }

                return 0;
            },
            Ct));

        Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.WriteFailed), (full.Kind, full.Message));
        Assert.Equal(13, Assert.IsType<SqliteException>(full.InnerException).SqliteErrorCode);
        Assert.Equal(SqliteDatabaseStatus.Ok, database.Status);
        Assert.False(database.IsReadOnly);
        Assert.Equal(generation, database.Generation);
        Assert.Equal((2L, 0L), (SqliteFixture.Count(database, "Parents"), SqliteFixture.Count(database, "Bulk")));
        Assert.Contains(_fx.Logger.At(LogLevel.Error), e => e.Message.Contains("failed on disk", StringComparison.Ordinal));

        // max_page_count is the pooled connection's own setting (like every pragma): the disk has room again once it goes.
        database.Release("test");
        SqliteFixture.AddBulk(database, 200);
        Assert.Equal(200, SqliteFixture.Count(database, "Bulk"));
        Assert.True(database.QuickCheck()!.Ok);
    }

    // ----------------------------------------------------------------- the rebuild recipe with a dependent view

    [Fact]
    public void ARebuildWithADependentView_FailsSafely_UnlessTheStepDropsAndRecreatesTheView()
    {
        // With legacy_alter_table off, the rename at the end of the 12 steps fails while a view still names the dropped
        // table: the upgrade rolls back (ReadOnlyMigrationFailed, nothing changed). The recipe drops the view first.
        const string View = "CREATE VIEW ParentNames AS SELECT Name FROM Parents;";
        IReadOnlyList<SqliteMigration> v1 = [new SqliteMigration(1, "create", SqliteFixture.V1Sql + View)];
        var first = _fx.Open(v1);
        SqliteFixture.AddFamilies(first, 3);
        first.Dispose();

        var careless = _fx.Open([v1[0], new SqliteMigration(2, "add Parents.Extra", SqliteFixture.V2Sql), new SqliteMigration(3, "rebuild Parents", SqliteFixture.RebuildParentsSql) { RequiresRebuild = true }]);
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyMigrationFailed, careless.Open());
        Assert.Contains("ParentNames", Assert.Single(_fx.Logger.At(LogLevel.Error)).Exception!.Message, StringComparison.Ordinal);
        Assert.Equal(3, SqliteFixture.FileRows(_fx.File, "Parents"));
        careless.Dispose();

        var careful = _fx.Open([v1[0], new SqliteMigration(2, "add Parents.Extra", SqliteFixture.V2Sql), new SqliteMigration(3, "rebuild Parents", "DROP VIEW ParentNames;" + SqliteFixture.RebuildParentsSql + View) { RequiresRebuild = true }]);
        Assert.Equal(SqliteDatabaseStatus.Migrated, careful.Open());
        Assert.Equal(3L, careful.Read(c => SqliteFixture.Scalar(c, "SELECT COUNT(*) FROM ParentNames;")));
        Assert.Equal(6, SqliteFixture.Count(careful, "Children"));
    }

    // ------------------------------------------------------------------------------------- the classification

    [Fact]
    public void TheCheckResult_TellsAReadFailure_FromDamage()
    {
        // As SQLite 3.46.1 answered quick_check with a page locked by another handle, and for damage.
        var locked = Check("*** in database main ***\nTree 5 page 5: unable to get the page. error code=266\nPage 7: never used\nPage 8: never used");
        Assert.True(locked.IsInconclusive);
        Assert.Equal("Tree 5 page 5: unable to get the page. error code=266", locked.IoError);

        Assert.True(Check("*** in database main ***\nTree 4 page 4: btreeInitPage() returns error code 11\nPage 6: never used").IsDamaged);
        Assert.True(Check("Tree 9 page 70000: unable to get the page. error code=11").IsDamaged);
        Assert.True(Check("SQLite error 11 (11): database disk image is malformed").IsDamaged);
        Assert.True(Check("SQLite error 26 (26): file is not a database").IsDamaged);
        Assert.True(Check("SQLite error 10 (266): disk I/O error").IsInconclusive);
        Assert.True(Check("SQLite error 13 (13): database or disk is full").IsInconclusive);
        Assert.True(Check("SQLite error 5 (5): database is locked").IsInconclusive);
        Assert.True(Check("Failed to read ptrmap key=12").IsInconclusive);
        Assert.True(Check("Main freelist: failed to get page 33").IsInconclusive);
        Assert.Null(new SqliteCheckResult(SqliteCheckResult.QuickCheck, DateTime.UtcNow, true, ["ok"]).IoError);

        static SqliteCheckResult Check(string message) => new(SqliteCheckResult.QuickCheck, DateTime.UtcNow, false, [message]);
    }
}
