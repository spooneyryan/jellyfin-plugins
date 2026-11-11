using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Sqlite;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Sqlite;

/// <summary>
/// The SQLite integrity, quarantine and restore tests of the kit and User Ratings, on real files with pages
/// overwritten in place: a corrupt page found after open (<c>quick_check</c> and the <c>SQLITE_CORRUPT</c> handler), a
/// corrupt snapshot discarded by <c>integrity_check</c>, the corruption restore at open and at run time with a hot WAL and
/// open pools (the database, <c>-wal</c> and <c>-shm</c> quarantined together, the checked snapshot installed with no
/// sidecars), no good snapshot (nothing moved, read-only, empty only when an admin chooses it, the earlier snapshots
/// kept), and the daily and pre-operation classes rotating independently.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteIntegrityTests : IDisposable
{
    private readonly SqliteFixture _fx = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fx.Dispose();

    // The daily snapshots and the pre-operation ones, by name.
    private string[] AllSnapshots()
        => [.. SqliteFixture.Names(_fx.Snapshots), .. SqliteFixture.Names(Path.Combine(_fx.Snapshots, SnapshotOperations.Folder))];

    // ---------------------------------------------------------------------------------------------- the snapshots

    [Fact]
    public async Task ASnapshot_IsOneRollbackModeFile_CheckedWithIntegrityCheck_WithNothingBesideIt()
    {
        // BackupDatabase, then journal_mode=DELETE on the copy: header bytes 18/19 = 1/1, nothing beside it after the check.
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 12);
        SqliteFixture.AddBulk(database, 300);

        var path = (await database.SnapshotAsync(7, Ct))!;

        Assert.Equal("userratings-20261019-032000.db", Path.GetFileName(path));
        var header = SqliteFiles.ReadHeader(path)!.Value;
        Assert.Equal((1, 1, 2), (header.WriteVersion, header.ReadVersion, header.UserVersion));
        Assert.Equal(["userratings-20261019-032000.db"], SqliteFixture.Names(_fx.Snapshots));
        Assert.Equal(12, SqliteFixture.FileRows(path, "Parents"));
        Assert.Equal(300, SqliteFixture.FileRows(path, "Bulk"));
        Assert.Equal(["userratings-20261019-032000.db"], SqliteFixture.Names(_fx.Snapshots));
        Assert.Equal(_fx.Clock.GetUtcNow().UtcDateTime, database.LastSnapshotUtc);
        var listed = Assert.Single(database.ListSnapshots());
        Assert.Equal((SnapshotKind.Daily, 2, false), (listed.Kind, listed.SchemaVersion, listed.Kept));
    }

    [Fact]
    public async Task DailyAndPreOperationSnapshots_RotateIndependently()
    {
        // Six pre-operation snapshots never remove a daily one; each class keeps its own number.
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 1);
        for (var day = 0; day < 4; day++)
        {
            Assert.NotNull(await database.SnapshotAsync(3, Ct));
            _fx.Clock.Advance(TimeSpan.FromDays(1));
        }

        for (var i = 0; i < 6; i++)
        {
            Assert.NotNull(await database.PreOpSnapshotAsync(SnapshotOperations.Import, Ct));
            _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var snapshots = database.ListSnapshots();
        Assert.Equal(3, snapshots.Count(s => s.Kind == SnapshotKind.Daily));
        Assert.Equal(SnapshotOperations.ToKeep, snapshots.Count(s => s.Kind == SnapshotKind.PreOp));
        Assert.Equal(["userratings-20261020-032000.db", "userratings-20261021-032000.db", "userratings-20261022-032000.db"], snapshots.Where(s => s.Kind == SnapshotKind.Daily).Select(s => s.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("userratings-import-20261023-032000.db", snapshots.Select(s => s.Name));
    }

    [Fact]
    public async Task PreOpKeepWithin_KeepsThePreOperationSnapshotsOfThatTime_AndNoneOlder()
    {
        // Opt-in: a burst of eight keeps all eight for 7 days; none older, not even the newest five.
        var database = _fx.Open(preOpKeepWithin: TimeSpan.FromDays(7));
        SqliteFixture.AddFamilies(database, 1);
        for (var i = 0; i < 8; i++)
        {
            Assert.NotNull(await database.PreOpSnapshotAsync(SnapshotOperations.UserDeleted, Ct));
            _fx.Clock.Advance(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(8, database.ListSnapshots().Count(s => s.Kind == SnapshotKind.PreOp));
        _fx.Clock.Advance(TimeSpan.FromDays(8));
        var remap = await database.PreOpSnapshotAsync(SnapshotOperations.Remap, Ct);
        Assert.Equal(Path.GetFileName(remap), Assert.Single(database.ListSnapshots(), s => s.Kind == SnapshotKind.PreOp).Name);
    }

    [Fact]
    public async Task TheDailySnapshot_DeletesThePreOperationSnapshotsAndCopiesKeptByAge_OnceTheyAreOlder()
    {
        // They used to wait for the next pre-operation snapshot, which may never come. The daily snapshot expires them,
        // and the copies of PreOpCopies (the archive's) with them; without PreOpKeepWithin it leaves the class alone
        // (the newest five, rotated when one is taken).
        var database = new SqliteDatabase(_fx.File, SqliteFixture.V2, _fx.Logger, _fx.Snapshots, _fx.Clock) { PreOpKeepWithin = TimeSpan.FromDays(7), PreOpCopies = ["userratings-archived.jsonl"] };
        try
        {
            SqliteFixture.AddFamilies(database, 1);
            for (var i = 0; i < 3; i++)
            {
                Assert.NotNull(await database.PreOpSnapshotAsync(SnapshotOperations.UserDeleted, Ct));
                _fx.Clock.Advance(TimeSpan.FromSeconds(2));
            }

            var preOp = Path.Combine(_fx.Snapshots, SnapshotOperations.Folder);
            var copy = Path.Combine(preOp, "userratings-archived-user-deleted-" + _fx.Clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl");
            await File.WriteAllTextAsync(copy, "{}", Ct);
            var otherStore = Path.Combine(preOp, "bookmarks-user-deleted-20261019-032000.json");
            await File.WriteAllTextAsync(otherStore, "{}", Ct);

            Assert.NotNull(await database.SnapshotAsync(7, Ct));
            Assert.Equal(3, database.ListSnapshots().Count(s => s.Kind == SnapshotKind.PreOp));
            Assert.True(File.Exists(copy));

            _fx.Clock.Advance(TimeSpan.FromDays(8));
            Assert.NotNull(await database.SnapshotAsync(7, Ct));
            Assert.DoesNotContain(database.ListSnapshots(), s => s.Kind == SnapshotKind.PreOp);
            Assert.False(File.Exists(copy));
            Assert.True(File.Exists(otherStore));
            Assert.Equal(2, database.ListSnapshots().Count(s => s.Kind == SnapshotKind.Daily));

            // A day with SnapshotsToKeep 0 writes no daily snapshot but still expires them.
            Assert.NotNull(await database.PreOpSnapshotAsync(SnapshotOperations.Remap, Ct));
            _fx.Clock.Advance(TimeSpan.FromDays(8));
            Assert.Null(await database.SnapshotAsync(0, Ct));
            Assert.DoesNotContain(database.ListSnapshots(), s => s.Kind == SnapshotKind.PreOp);
        }
        finally
        {
            database.Dispose();
        }
    }

    [Fact]
    public async Task Rotation_DeletesASnapshotsSidecarsWithIt()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 1);
        var oldest = (await database.SnapshotAsync(2, Ct))!;
        await File.WriteAllTextAsync(oldest + "-wal", string.Empty, Ct);
        await File.WriteAllTextAsync(oldest + "-shm", "x", Ct);
        _fx.Clock.Advance(TimeSpan.FromDays(1));
        await database.SnapshotAsync(2, Ct);
        _fx.Clock.Advance(TimeSpan.FromDays(1));
        await database.SnapshotAsync(2, Ct);

        Assert.Equal(["userratings-20261020-032000.db", "userratings-20261021-032000.db"], SqliteFixture.Names(_fx.Snapshots));
    }

    [Fact]
    public async Task ASnapshotOfADamagedDatabase_FailsItsIntegrityCheck_IsDeleted_AndTheGoodOnesAreKept()
    {
        // A snapshot of a deliberately corrupted DB fails integrity_check and is discarded; a corrupt live
        // database is never rotated over the good snapshots, and it is read-only from then on.
        var database = _fx.Open();
        SqliteFixture.AddBulk(database);
        var good = Path.GetFileName((await database.SnapshotAsync(1, Ct))!);
        var root = SqliteFixture.RootPage(database, "Bulk");
        database.Release("test");
        SqliteFixture.CorruptPage(_fx.File, root);
        _fx.Clock.Advance(TimeSpan.FromDays(1));

        Assert.Null(await database.SnapshotAsync(1, Ct));

        Assert.Equal([good], SqliteFixture.Names(_fx.Snapshots));
        Assert.Contains(_fx.Logger.At(LogLevel.Error), e => e.Message.Contains("failed its integrity check", StringComparison.Ordinal) && e.Message.Contains("the earlier snapshots are kept", StringComparison.Ordinal));
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Status);
        Assert.False(database.LastQuickCheck!.Ok);
        Assert.Null(await database.SnapshotAsync(1, Ct));
        Assert.Equal([good], SqliteFixture.Names(_fx.Snapshots));
    }

    // -------------------------------------------------------------------------------------- damage found at run time

    [Fact]
    public async Task ACorruptPageFoundAfterOpen_IsCaughtByQuickCheck_AndTheDatabaseGoesReadOnly()
    {
        var database = _fx.Open();
        SqliteFixture.AddBulk(database);
        var root = SqliteFixture.RootPage(database, "Bulk");
        database.Release("test");
        SqliteFixture.CorruptPage(_fx.File, root);

        var check = database.QuickCheck()!;

        Assert.False(check.Ok);
        Assert.NotEmpty(check.Messages);
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Status);
        Assert.True(database.IsReadOnly);
        Assert.Contains("failed its integrity check", database.StatusMessage, StringComparison.Ordinal);
        var write = await Assert.ThrowsAsync<PluginException>(() => database.WriteAsync((_, _) => 0, Ct));
        Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.Corrupt), (write.Kind, write.Message));
        Assert.Equal(SqliteMessages.ExportCorrupt, Assert.Throws<PluginException>(() => database.ExportData((_, _) => new System.Text.Json.Nodes.JsonObject())).Message);

        // Undamaged tables can still be read.
        Assert.Equal(0, SqliteFixture.Count(database, "Parents"));
    }

    [Fact]
    public async Task QuickCheck_FindsDamageThePoolsPageCacheHides()
    {
        // A pooled connection keeps its page cache while the WAL index is unchanged, so damage written to the file from
        // outside passed the maintenance's quick_check. Its check now has a connection of its own.
        var database = _fx.Open();
        SqliteFixture.AddBulk(database);
        Assert.True(await database.CheckpointAsync(Ct));
        Assert.Equal(2000, SqliteFixture.Count(database, "Bulk"));
        SqliteFixture.CorruptPage(_fx.File, SqliteFixture.RootPage(database, "Bulk"));

        Assert.False(database.QuickCheck()!.Ok);
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Status);
    }

    [Fact]
    public async Task ARestore_OnDamageNoCheckFoundYet_IsRefusedAsDamaged_WithNoSnapshotOfTheDamagedFile()
    {
        // The restore's safety snapshot used to be taken of the damaged file (through the pool's page cache), then the
        // restore answered 500 with a raw SqliteException. Now the live database is checked before any pre-operation
        // snapshot: damaged, no snapshot (a damaged copy never pushes a good one out of the rotation), the database
        // marked damaged, and the 409 of a damaged database with its code; the damaged database's restore then goes on
        // as usual.
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 1);
        var first = Path.GetFileName((await database.SnapshotAsync(7, Ct))!);
        SqliteFixture.AddBulk(database);
        Assert.True(await database.CheckpointAsync(Ct));
        Assert.Equal(2000, SqliteFixture.Count(database, "Bulk"));
        SqliteFixture.CorruptPage(_fx.File, SqliteFixture.RootPage(database, "Bulk"));
        var before = AllSnapshots();
        Assert.NotEmpty(before);

        var refused = await Assert.ThrowsAsync<PluginException>(() => database.RestoreSnapshotAsync(first, Ct));

        Assert.Equal((PluginErrorKind.Conflict, SqliteDatabase.DamagedCode), (refused.Kind, refused.Code));
        Assert.Contains("failed its integrity check", refused.Message, StringComparison.Ordinal);
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Status);
        Assert.Equal(before, AllSnapshots());
        Assert.Contains(_fx.Logger.At(LogLevel.Error), e => e.Message.Contains("before the restore snapshot failed", StringComparison.Ordinal));

        var result = await database.RestoreSnapshotAsync(first, Ct);

        Assert.Equal((first, (string?)null), (result.RestoredFrom, result.SafetySnapshot));
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, database.Status);
        Assert.Equal(1, SqliteFixture.Count(database, "Parents"));
    }

    [Fact]
    public async Task APreOperationSnapshot_OfADamagedDatabase_IsNotTaken_AndItsWriteIsRefused()
    {
        var database = _fx.Open();
        SqliteFixture.AddBulk(database);
        Assert.NotNull(await database.PreOpSnapshotAsync(SnapshotOperations.Import, Ct));
        Assert.True(await database.CheckpointAsync(Ct));
        Assert.Equal(2000, SqliteFixture.Count(database, "Bulk"));
        SqliteFixture.CorruptPage(_fx.File, SqliteFixture.RootPage(database, "Bulk"));
        var before = AllSnapshots();
        Assert.NotEmpty(before);
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));

        var write = await Assert.ThrowsAsync<PluginException>(() => database.RunWriteAsync((_, _) => 0, commit: true, SnapshotOperations.Purge, Ct));

        Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.Corrupt), (write.Kind, write.Message));
        Assert.Equal(before, AllSnapshots());
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Status);
        var admin = Assert.Throws<PluginException>(() => database.ThrowIfReadOnlyForAdmin());
        Assert.Equal((PluginErrorKind.Conflict, SqliteDatabase.DamagedCode), (admin.Kind, admin.Code));
    }

    [Fact]
    public void ACorruptPageFoundByAQuery_SwitchesTheDatabaseToReadOnly()
    {
        var database = _fx.Open();
        SqliteFixture.AddBulk(database);
        var root = SqliteFixture.RootPage(database, "Bulk");
        database.Release("test");
        SqliteFixture.CorruptPage(_fx.File, root);

        var read = Assert.Throws<PluginException>(() => SqliteFixture.Count(database, "Bulk"));

        Assert.Equal(PluginErrorKind.Unavailable, read.Kind);
        Assert.Equal(SqliteMessages.Corrupt, read.Message);
        Assert.Equal(11, Assert.IsType<Microsoft.Data.Sqlite.SqliteException>(read.InnerException).SqliteErrorCode);
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Status);
        Assert.Single(_fx.Logger.At(LogLevel.Error), e => e.Message.Contains("is damaged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCorruptionRestore_WithAHotWalAndOpenPools_QuarantinesTheFilesTogether_AndInstallsTheCheckedSnapshot()
    {
        // With pooled connections open and uncheckpointed frames in -wal, the restore moves db, -wal and -shm
        // together into quarantine-<ts>/, installs the verified snapshot with no sidecars, and the reopened store passes
        // integrity_check and contains exactly the snapshot's rows.
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 5);
        SqliteFixture.AddBulk(database);
        var snapshot = Path.GetFileName((await database.SnapshotAsync(7, Ct))!);
        var root = SqliteFixture.RootPage(database, "Bulk");
        database.Release("test");
        SqliteFixture.AddRecent(database, 7);
        Assert.True(new FileInfo(_fx.File + "-wal").Length > 0, "Uncommitted frames are in the WAL.");
        Assert.Equal("locked", SqliteFiles.TryOpenExclusive(_fx.File));
        SqliteFixture.CorruptPage(_fx.File, root);
        var damaged = SqliteFixture.ReadOpenFile(_fx.File);
        Assert.False(database.QuickCheck()!.Ok);
        _fx.Clock.Advance(TimeSpan.FromHours(6));

        var recovery = await database.RecoverAsync(null, Ct);

        Assert.Equal(snapshot, recovery.RestoredFrom);
        Assert.Equal(6.0, recovery.LostHours);
        Assert.Empty(recovery.SnapshotsTried);
        Assert.Equal("quarantine-20261019-092000", recovery.QuarantineFolder);
        Assert.Equal(["userratings.db", "userratings.db-shm", "userratings.db-wal"], recovery.QuarantinedFiles.Order(StringComparer.Ordinal));
        var quarantine = Path.Combine(_fx.Folder, recovery.QuarantineFolder!);
        Assert.Equal(["reason.txt", "userratings.db", "userratings.db-shm", "userratings.db-wal"], SqliteFixture.Names(quarantine));
        Assert.Equal(damaged, await File.ReadAllBytesAsync(Path.Combine(quarantine, "userratings.db"), Ct));
        Assert.True(new FileInfo(Path.Combine(quarantine, "userratings.db-wal")).Length > 0, "The WAL went with its database, unchanged.");

        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, database.Status);
        Assert.False(database.IsReadOnly);
        Assert.Contains("restored from the snapshot " + snapshot, database.StatusMessage, StringComparison.Ordinal);
        Assert.True(database.QuickCheck()!.Ok);
        Assert.Equal((5L, 2000L, 0L), (SqliteFixture.Count(database, "Parents"), SqliteFixture.Count(database, "Bulk"), SqliteFixture.Count(database, "Recent")));
        Assert.Equal("wal", database.Read(c => SqliteFixture.Text(c, "PRAGMA journal_mode;")));
        Assert.Equal("free", database.Release("test"));
        // The marker of the incident keeps every snapshot (V3), beside the restored database and its last-restore note.
        Assert.Equal(["userratings.db", "userratings.db.keep-snapshots", "userratings.db.last-restore"], SqliteFixture.Names(_fx.Folder));
        Assert.StartsWith(snapshot + "\n", await File.ReadAllTextAsync(database.LastRestoreMarkerPath, Ct), StringComparison.Ordinal);
        await database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Recent (Payload) VALUES ('after');"); return 0; }, Ct);

        var described = database.Describe();
        Assert.Equal("RestoredFromSnapshot", described["Status"]);
        Assert.Equal(1, described["QuarantineCount"]);
        Assert.Equal(snapshot, ((System.Collections.Generic.IDictionary<string, object?>)described["Recovery"]!)["RestoredFrom"]);
    }

    [Fact]
    public async Task ADamagedDatabaseAtOpen_AfterACrash_IsQuarantinedAsFound_AndTheNewestGoodSnapshotTakesItsPlace()
    {
        // The automatic restore at start: a crash image (database, -wal, -shm) with a page overwritten in the database.
        var database = _fx.Open();
        SqliteFixture.AddBulk(database);
        await database.SnapshotAsync(7, Ct);
        _fx.Clock.Advance(TimeSpan.FromHours(1));
        SqliteFixture.AddFamilies(database, 3);
        var newest = Path.GetFileName((await database.PreOpSnapshotAsync(SnapshotOperations.Import, Ct))!);
        var root = SqliteFixture.RootPage(database, "Bulk");
        database.Release("test");
        SqliteFixture.AddRecent(database, 4);
        var image = _fx.Directory.Combine("crash", "userratings.db");
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            SqliteFixture.CopyOpenFile(_fx.File + suffix, image + suffix);
        }

        foreach (var file in Directory.GetFiles(_fx.Snapshots, "*", SearchOption.AllDirectories))
        {
            SqliteFixture.CopyOpenFile(file, Path.Combine(Path.GetDirectoryName(image)!, "snapshots", Path.GetRelativePath(_fx.Snapshots, file)));
        }

        SqliteFixture.CorruptPage(image, root);
        var wal = await File.ReadAllBytesAsync(image + "-wal", Ct);
        _fx.Clock.Advance(TimeSpan.FromHours(2));

        var restarted = _fx.Open(file: image);

        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        var recovery = restarted.LastRecovery!;
        Assert.Equal(newest, recovery.RestoredFrom);
        Assert.Equal(2.0, recovery.LostHours);
        var quarantine = Path.Combine(Path.GetDirectoryName(image)!, recovery.QuarantineFolder!);
        Assert.Equal(["reason.txt", "userratings.db", "userratings.db-shm", "userratings.db-wal"], SqliteFixture.Names(quarantine));
        Assert.Equal(wal, await File.ReadAllBytesAsync(Path.Combine(quarantine, "userratings.db-wal"), Ct));
        Assert.Contains("quick_check", await File.ReadAllTextAsync(Path.Combine(quarantine, "reason.txt"), Ct), StringComparison.Ordinal);
        Assert.Equal((3L, 2000L, 0L), (SqliteFixture.Count(restarted, "Parents"), SqliteFixture.Count(restarted, "Bulk"), SqliteFixture.Count(restarted, "Recent")));
        Assert.True(restarted.QuickCheck()!.Ok);
        Assert.Single(_fx.Logger.At(LogLevel.Error), e => e.Message.Contains("was damaged", StringComparison.Ordinal) && e.Message.Contains(newest, StringComparison.Ordinal));
        Assert.Contains(newest, restarted.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileThatIsNoDatabase_AtOpen_IsRestoredFromTheNewestSnapshotThatPasses_SkippingADamagedOne()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 2);
        var older = Path.GetFileName((await database.SnapshotAsync(7, Ct))!);
        _fx.Clock.Advance(TimeSpan.FromDays(1));
        SqliteFixture.AddFamilies(database, 2, from: 10);
        var newer = (await database.SnapshotAsync(7, Ct))!;
        database.Dispose();
        SqliteFixture.CorruptPage(newer, 2);
        await File.WriteAllTextAsync(_fx.File, "this is not a database, but it is long enough to have a header of a hundred bytes or more, really", Ct);

        var restarted = _fx.Open();

        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        Assert.Equal(older, restarted.LastRecovery!.RestoredFrom);
        Assert.Equal([Path.GetFileName(newer)], restarted.LastRecovery.SnapshotsTried);
        Assert.Contains("SQLite error 26", restarted.LastRecovery.Reason, StringComparison.Ordinal);
        Assert.Equal(2, SqliteFixture.Count(restarted, "Parents"));
        Assert.True(File.Exists(newer), "A snapshot that fails at a restore is skipped, not deleted.");
    }

    [Fact]
    public async Task NoSnapshotPassesAtOpen_NothingIsMoved_AndOnlyTheAdminStartsEmpty_KeepingTheEarlierSnapshots()
    {
        // The database stays ReadOnlyCorrupt with its undamaged tables readable; RecoveredEmpty is the admin's
        // explicit RecoverAsync(null, startEmpty: true), never the kit's own choice.
        var database = _fx.Open();
        SqliteFixture.AddBulk(database);
        SqliteFixture.AddFamilies(database, 3);
        var root = SqliteFixture.RootPage(database, "Bulk");
        var only = (await database.SnapshotAsync(1, Ct))!;
        database.Dispose();
        _fx.Clock.Advance(TimeSpan.FromHours(1));
        SqliteFixture.CorruptPage(only, root);
        SqliteFixture.CorruptPage(_fx.File, root);
        var damaged = await File.ReadAllBytesAsync(_fx.File, Ct);

        var restarted = _fx.Open();

        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, restarted.Open());
        Assert.True(restarted.IsReadOnly);
        Assert.Null(restarted.LastRecovery);
        Assert.Empty(restarted.ListQuarantines());
        Assert.Equal(damaged, SqliteFixture.ReadOpenFile(_fx.File));
        Assert.Contains("no snapshot passed its own (tried: " + Path.GetFileName(only) + ")", restarted.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("start empty", restarted.StatusMessage, StringComparison.Ordinal);
        Assert.Single(_fx.Logger.At(LogLevel.Error), e => e.Message.Contains("no snapshot passed its integrity check", StringComparison.Ordinal));
        Assert.Contains(_fx.Logger.At(LogLevel.Warning), e => e.Message.Contains("checked again with integrity_check", StringComparison.Ordinal));
        Assert.Equal(3, SqliteFixture.Count(restarted, "Parents"));
        Assert.Equal(SqliteMessages.Corrupt, (await Assert.ThrowsAsync<PluginException>(() => restarted.WriteAsync((_, _) => 0, Ct))).Message);
        Assert.Null(await restarted.SnapshotAsync(1, Ct));
        Assert.True(File.Exists(only));

        // Recover without the choice to start empty changes nothing; a snapshot and the choice together are refused.
        Assert.Equal(SqliteMessages.NoSnapshotPassed, (await Assert.ThrowsAsync<PluginException>(() => restarted.RecoverAsync(null, Ct))).Message);
        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<PluginException>(() => restarted.RecoverAsync(Path.GetFileName(only), startEmpty: true, Ct))).Kind);
        Assert.Empty(restarted.ListQuarantines());
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, restarted.Status);

        var recovery = await restarted.RecoverAsync(null, startEmpty: true, Ct);

        Assert.Equal(SqliteDatabaseStatus.RecoveredEmpty, restarted.Status);
        Assert.False(restarted.IsReadOnly);
        Assert.Null(recovery.RestoredFrom);
        Assert.Empty(recovery.SnapshotsTried);
        Assert.Contains("userratings.db", recovery.QuarantinedFiles);
        Assert.Equal(damaged, await File.ReadAllBytesAsync(Path.Combine(_fx.Folder, recovery.QuarantineFolder!, "userratings.db"), Ct));
        Assert.Contains("start empty", await File.ReadAllTextAsync(Path.Combine(_fx.Folder, recovery.QuarantineFolder!, "reason.txt"), Ct), StringComparison.Ordinal);
        Assert.StartsWith("An admin chose to start empty", restarted.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(0, SqliteFixture.Count(restarted, "Bulk"));
        Assert.Equal(_fx.Clock.GetUtcNow().UtcDateTime, restarted.KeepSnapshotsBeforeUtc);
        Assert.True(File.Exists(restarted.KeepSnapshotsMarkerPath));
        await restarted.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Parents (Id, Name) VALUES (1, 'new');"); return 0; }, Ct);

        // The daily rotation keeps the snapshot from before the empty start, whatever keep says, across a restart.
        for (var day = 1; day <= 3; day++)
        {
            _fx.Clock.Advance(TimeSpan.FromDays(1));
            await restarted.SnapshotAsync(1, Ct);
        }

        restarted.Dispose();
        var again = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.Ok, again.Open());
        Assert.NotNull(again.KeepSnapshotsBeforeUtc);
        _fx.Clock.Advance(TimeSpan.FromDays(1));
        await again.SnapshotAsync(1, Ct);
        var daily = again.ListSnapshots().Where(s => s.Kind == SnapshotKind.Daily).ToList();
        Assert.Equal(2, daily.Count);
        Assert.Contains(daily, s => s.Name == Path.GetFileName(only) && s.Kept);
    }

    [Fact]
    public async Task AfterARestore_TheCorruptionRestore_PrefersTheRestoredData_OverTheStateTheRestoreReplaced()
    {
        // Found in a sandbox run: the "restore" pre-operation snapshot is the newest file but holds the state an admin
        // replaced. The corruption restore takes the snapshots taken since the restore, then the restored one.
        var database = _fx.Open();
        SqliteFixture.AddBulk(database);
        SqliteFixture.AddFamilies(database, 2);
        var chosen = Path.GetFileName((await database.SnapshotAsync(7, Ct))!);
        _fx.Clock.Advance(TimeSpan.FromMinutes(10));
        SqliteFixture.AddFamilies(database, 3, from: 10);
        var restore = await database.RestoreSnapshotAsync(chosen, Ct);
        Assert.True(File.Exists(database.LastRestoreMarkerPath));
        Assert.Equal(2, SqliteFixture.Count(database, "Parents"));
        var root = SqliteFixture.RootPage(database, "Bulk");
        database.Release("test");
        SqliteFixture.CorruptPage(_fx.File, root);
        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(database.QuickCheck()!.Ok);

        var first = await database.RecoverAsync(null, Ct);

        Assert.Equal(chosen, first.RestoredFrom);
        Assert.Empty(first.SnapshotsTried);
        Assert.Equal(2, SqliteFixture.Count(database, "Parents"));
        Assert.True(File.Exists(Path.Combine(_fx.Snapshots, "pre-op", restore.SafetySnapshot!)), "The replaced state is still there to restore by name.");

        // A snapshot taken after that comes first again.
        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        SqliteFixture.AddFamilies(database, 1, from: 20);
        var later = Path.GetFileName((await database.SnapshotAsync(7, Ct))!);
        root = SqliteFixture.RootPage(database, "Bulk");
        database.Release("test");
        SqliteFixture.CorruptPage(_fx.File, root);
        Assert.False(database.QuickCheck()!.Ok);

        Assert.Equal(later, (await database.RecoverAsync(null, Ct)).RestoredFrom);
        Assert.Equal(3, SqliteFixture.Count(database, "Parents"));
    }

    [Fact]
    public void TheCheckSummary_AndTheLostSpan_ReadAsOneLine()
    {
        // As SQLite 3.46.1 answered quick_check for an overwritten root page in a sandbox run: one row, many lines.
        var check = new SqliteCheckResult(
            SqliteCheckResult.QuickCheck,
            DateTime.UtcNow,
            false,
            ["*** in database main ***\nTree 4 page 4: btreeInitPage() returns error code 11\nPage 6: never used\nPage 7: never used\nPage 8: never used"]);

        Assert.Equal("Tree 4 page 4: btreeInitPage() returns error code 11; Page 6: never used (and 2 more)", check.Summary);
        Assert.Equal("ok", new SqliteCheckResult(SqliteCheckResult.QuickCheck, DateTime.UtcNow, true, ["ok"]).Summary);

        var at = new DateTime(2026, 10, 20, 0, 44, 11, DateTimeKind.Utc);
        Assert.Equal("1 minute", Recovery(at, at.AddSeconds(-42)).LostSpan);
        Assert.Equal("17 minutes", Recovery(at, at.AddMinutes(-16.5)).LostSpan);
        Assert.Equal("1 hour", Recovery(at, at.AddMinutes(-61)).LostSpan);
        Assert.Equal("6.5 hours", Recovery(at, at.AddHours(-6.5)).LostSpan);
        Assert.Null(Recovery(at, null).LostSpan);

        static SqliteRecoveryResult Recovery(DateTime utc, DateTime? snapshot) => new(utc, "x", "quarantine-x", [], snapshot is null ? null : "s.db", snapshot, []);
    }

    [Fact]
    public async Task RestoreSnapshot_OnADamagedDatabase_IsTheCorruptionRestoreWithThatSnapshot()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 1);
        var first = Path.GetFileName((await database.SnapshotAsync(7, Ct))!);
        _fx.Clock.Advance(TimeSpan.FromDays(1));
        SqliteFixture.AddFamilies(database, 1, from: 2);
        SqliteFixture.AddBulk(database);
        await database.SnapshotAsync(7, Ct);
        var root = SqliteFixture.RootPage(database, "Bulk");
        database.Release("test");
        SqliteFixture.CorruptPage(_fx.File, root);
        Assert.False(database.QuickCheck()!.Ok);

        var result = await database.RestoreSnapshotAsync(first, Ct);

        Assert.Equal(first, result.RestoredFrom);
        Assert.Null(result.SafetySnapshot);
        Assert.Equal(first, database.LastRecovery!.RestoredFrom);
        Assert.Equal(1, SqliteFixture.Count(database, "Parents"));
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, database.Status);
        Assert.Equal(PluginErrorKind.Conflict, (await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(null, Ct))).Kind);
    }
}
