using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Sqlite;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Sqlite;

/// <summary>
/// The JSON store's hardening, for the SQLite database: the kept snapshots recorded by name in the JSON store's
/// <c>.keep-snapshots</c> format (and an older marker of a time alone read and recorded by name), a good database back
/// in place never quarantined by an admin's recovery, and a recovery that moved the database aside and then failed
/// putting everything back.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteStoreHardeningTests : IDisposable
{
    private readonly SqliteFixture _fx = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Marker => _fx.File + ".keep-snapshots";

    public void Dispose() => _fx.Dispose();

    // ------------------------------------------------------------------------------------ 1: the kept snapshots by name

    [Fact]
    public async Task ClockBehind_RestoringASnapshotTakenAfterTheIncident_KeepsTheKeptOnes_AndOnlyTheNewOnesRotate()
    {
        // The JSON review's scenario: daily snapshots of October 5 to 12, each holding one more parent, the clock behind them
        // on October 9 and the database file gone. The restore at open takes October 10's (11 and 12 are dated more than a
        // day ahead) and the marker's time is October 12 03:10:01, so with the time rule every snapshot taken while the
        // clock was behind was dated before it: restoring one released the kept ones, which then rotated out.
        var database = OpenAt(At(5, 3, 10));
        var before = new List<string>();
        for (var day = 5; day <= 12; day++)
        {
            _fx.Clock.Set(At(day, 3, 10));
            SqliteFixture.AddFamilies(database, 1, from: day);
            before.Add(Path.GetFileName((await database.SnapshotAsync(10, Ct))!));
        }

        database.Dispose();
        File.Delete(_fx.File);

        var restarted = OpenAt(At(9, 12, 0));
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        Assert.Equal("userratings-20261010-031000.db", restarted.LastRecovery!.RestoredFrom);
        Assert.Equal(6, SqliteFixture.Count(restarted, "Parents"));
        Assert.Equal(new DateTime(2026, 10, 12, 3, 10, 1, DateTimeKind.Utc), restarted.KeepSnapshotsBeforeUtc);
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.KeptSnapshotsIncident);
        Assert.All(restarted.ListSnapshots(), s => Assert.True(s.Kept, s.Name));
        Assert.Equal(before.Order(StringComparer.Ordinal), RecordedNames());

        // The next day, the clock still behind: a daily snapshot and an import's are dated before the marker's time, and
        // restoring either leaves the kept ones kept, with a Warning each.
        _fx.Clock.Set(At(10, 12, 0));
        var daily = Path.GetFileName((await restarted.SnapshotAsync(3, Ct))!);
        Assert.Equal("userratings-20261010-120000.db", daily);
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        var import = Path.GetFileName((await restarted.PreOpSnapshotAsync(SnapshotOperations.Import, Ct))!);
        foreach (var later in new[] { import, daily })
        {
            _fx.Clock.Advance(TimeSpan.FromMinutes(1));
            var result = await restarted.RestoreSnapshotAsync(later, Ct);
            Assert.NotNull(result.KeepSnapshotsBeforeUtc);
            Assert.True(File.Exists(Marker), "Restoring " + later + " released the kept snapshots.");
            Assert.False(restarted.ListSnapshots().Single(s => s.Name == later).Kept, later);
        }

        Assert.Equal(2, _fx.Logger.At(LogLevel.Warning).Count(e => e.Message.Contains("still kept", StringComparison.Ordinal)));

        // Five daily runs keeping 3: only the snapshots taken since rotate, so all those from before the incident stay.
        for (var i = 0; i < 5; i++)
        {
            _fx.Clock.Advance(TimeSpan.FromDays(1));
            await restarted.SnapshotAsync(3, Ct);
        }

        Assert.All(before, b => Assert.True(File.Exists(Path.Combine(_fx.Snapshots, b)), b + " was rotated out."));
        var dailies = restarted.ListSnapshots().Where(s => s.Kind == SnapshotKind.Daily).ToList();
        Assert.Equal(before.Count + 3, dailies.Count);
        Assert.Equal(3, dailies.Count(s => !s.Kept));
        restarted.Dispose();

        // A restart with the clock right again: the marker still names them, and only a restore of one of them ends it.
        var reopened = OpenAt(At(30, 3, 10));
        for (var i = 0; i < 4; i++)
        {
            await reopened.SnapshotAsync(3, Ct);
            _fx.Clock.Advance(TimeSpan.FromDays(1));
        }

        Assert.All(before, b => Assert.Contains(reopened.ListSnapshots(), s => s.Name == b && s.Kept));
        await reopened.RestoreSnapshotAsync(before[3], Ct);
        Assert.Equal(4, SqliteFixture.Count(reopened, "Parents"));
        Assert.Null(reopened.KeepSnapshotsBeforeUtc);
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public async Task AnOlderMarkerOfATimeAlone_KeepsTheSnapshotsDatedBeforeIt_RecordedByName_AndOnlyOneOfThemReleasesThem()
    {
        // Before the names, the marker held the later of the incident and the newest snapshot's time plus a second, as
        // text ("O"), and kept every snapshot dated before it. Read now: the ones there are dated before it are recorded
        // by name once (the marker rewritten in the JSON store's format, the time kept), so the snapshots taken since
        // rotate even while the clock is behind that time, and only one of them dated before it releases them.
        var database = OpenAt(At(1, 3, 10));
        SqliteFixture.AddFamilies(database, 2);
        var source = (await database.SnapshotAsync(10, Ct))!;
        string[] kept = ["userratings-20261001-031000.db", "userratings-20261002-031000.db", "userratings-20261003-031000.db", "userratings-20261005-031000.db"];
        foreach (var name in kept.Skip(1))
        {
            File.Copy(source, Path.Combine(_fx.Snapshots, name));
        }

        database.Dispose();
        var time = new DateTime(2026, 10, 5, 3, 10, 1, DateTimeKind.Utc);
        await File.WriteAllTextAsync(Marker, time.ToString("O", CultureInfo.InvariantCulture), Ct);

        var reopened = OpenAt(At(4, 12, 0));
        Assert.Equal(SqliteDatabaseStatus.Ok, reopened.Open());
        Assert.Equal(time, reopened.KeepSnapshotsBeforeUtc);
        Assert.Null(reopened.KeptSnapshotsIncident);
        Assert.Equal(kept, reopened.ListSnapshots().Where(s => s.Kept).Select(s => s.Name).Order(StringComparer.Ordinal));
        var rewritten = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllBytesAsync(Marker, Ct)));
        Assert.Equal(time, (DateTime)rewritten["KeepSnapshotsBeforeUtc"]!);
        Assert.Equal(time, (DateTime)rewritten["ReleaseBeforeUtc"]!);
        Assert.Equal(kept, RecordedNames());

        var taken = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            taken.Add(Path.GetFileName((await reopened.SnapshotAsync(3, Ct))!));
            _fx.Clock.Advance(TimeSpan.FromHours(1));
        }

        Assert.All(kept, k => Assert.True(File.Exists(Path.Combine(_fx.Snapshots, k)), k + " was rotated out."));
        Assert.Equal(3, reopened.ListSnapshots().Count(s => s.Kind == SnapshotKind.Daily && !s.Kept));

        // One taken since (dated before the marker's time: the time rule released them here) leaves them kept.
        await reopened.RestoreSnapshotAsync(taken[^1], Ct);
        Assert.True(File.Exists(Marker), "Restoring " + taken[^1] + " released the kept snapshots.");

        await reopened.RestoreSnapshotAsync(kept[1], Ct);
        Assert.Null(reopened.KeepSnapshotsBeforeUtc);
        Assert.False(File.Exists(Marker));
    }

    // ------------------------------------------------------------- 2: a good database back in place is never moved

    [Fact]
    public async Task Recover_WhileAGoodDatabaseWasPutBack_IsRefused_AndMovesNothing_NorTouchesItsWal()
    {
        // The status says what was found at open (here the file missing and its only snapshot damaged), and the
        // recovery moved whatever was in place: a good userratings.db an admin copied back while the server runs, then
        // Start empty (or a snapshot restored), went into a quarantine folder as damaged and the data started empty. No -shm
        // appears beside it: the check opens a copy, never the files in place. The -wal found without its database is a
        // real one of another database with the same page size (the storage hardening): the open moves it aside
        // before anything else, so the good file copied back later never replays it (it would replace the schema page).
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 5);
        var root = SqliteFixture.RootPage(database, "Parents");
        var snapshot = Path.GetFileName((await database.SnapshotAsync(3, Ct))!);
        database.Dispose();
        var good = _fx.Directory.Combine("good.db");
        File.Copy(_fx.File, good);
        SqliteFixture.CorruptPage(Path.Combine(_fx.Snapshots, snapshot), root);
        File.Delete(_fx.File);
        var foreign = SqliteFixture.ForeignWal(_fx.Directory.Combine("other.db"));
        File.Copy(foreign, _fx.File + "-wal");

        var restarted = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, restarted.Open());
        Assert.False(File.Exists(_fx.File + "-wal"), "The -wal found without its database stayed beside its place.");
        var orphans = Assert.Single(restarted.ListQuarantines());
        Assert.Equal(["reason.txt", "userratings.db-wal"], orphans.Files);
        Assert.Equal(await File.ReadAllBytesAsync(foreign, Ct), await File.ReadAllBytesAsync(Path.Combine(_fx.Folder, orphans.Name, "userratings.db-wal"), Ct));
        File.Copy(good, _fx.File);
        var before = Listing();

        var refused = await Assert.ThrowsAsync<PluginException>(() => restarted.RecoverAsync(null, startEmpty: true, Ct));
        Assert.Equal((PluginErrorKind.Conflict, StoreMessages.ReadableFileInPlace("userratings.db")), (refused.Kind, refused.Message));
        Assert.Equal(before, Listing());
        Assert.Equal([orphans.Name], restarted.ListQuarantines().Select(q => q.Name));
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, restarted.Status);

        // A snapshot's restore of the damaged database is the corruption restore: the same.
        refused = await Assert.ThrowsAsync<PluginException>(() => restarted.RestoreSnapshotAsync(snapshot, Ct));
        Assert.Equal((PluginErrorKind.Conflict, StoreMessages.ReadableFileInPlace("userratings.db")), (refused.Kind, refused.Message));
        Assert.Equal(before, Listing());
        Assert.Equal(2, _fx.Logger.At(LogLevel.Warning).Count(e => e.Message.Contains("recovery of", StringComparison.Ordinal) && e.Message.Contains("was refused", StringComparison.Ordinal)));

        // The restart the refusal names opens it, as it is.
        restarted.Dispose();
        var again = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.Ok, again.Open());
        Assert.Equal(5, SqliteFixture.Count(again, "Parents"));
        Assert.True(again.QuickCheck()!.Ok);
    }

    [Fact]
    public async Task Recover_OfDamageFoundAtRunTime_WhoseFileReadsWellNow_IsRefused_AndTheFilesStayAsFound()
    {
        // The damage a query or the maintenance's quick_check finds at run time is marked at once (only the open confirms it
        // a second time): when the file reads well by the time an admin recovers it (a read that failed, or a good copy
        // written over it in place), it is never quarantined as damaged, nor its -wal, which holds the latest writes.
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 5);
        SqliteFixture.AddBulk(database);
        var snapshot = Path.GetFileName((await database.SnapshotAsync(7, Ct))!);
        var root = SqliteFixture.RootPage(database, "Bulk");
        database.Release("test");
        SqliteFixture.AddRecent(database, 7);
        var page = ReadPage(_fx.File, root);
        SqliteFixture.CorruptPage(_fx.File, root);
        Assert.False(database.QuickCheck()!.Ok);
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Status);

        WritePage(_fx.File, root, page);
        var file = SqliteFixture.ReadOpenFile(_fx.File);
        var wal = SqliteFixture.ReadOpenFile(_fx.File + "-wal");
        Assert.NotEmpty(wal);

        var refused = await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(null, Ct));
        Assert.Equal((PluginErrorKind.Conflict, StoreMessages.ReadableFileInPlace("userratings.db")), (refused.Kind, refused.Message));
        Assert.Equal(file, SqliteFixture.ReadOpenFile(_fx.File));
        Assert.Equal(wal, SqliteFixture.ReadOpenFile(_fx.File + "-wal"));
        Assert.Empty(database.ListQuarantines());
        Assert.False(File.Exists(_fx.File + ".recheck.tmp"));

        // Damaged again: the recovery goes on, the snapshot in its place.
        SqliteFixture.CorruptPage(_fx.File, root);
        var recovery = await database.RecoverAsync(null, Ct);
        Assert.Equal(snapshot, recovery.RestoredFrom);
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, database.Status);
    }

    [Fact]
    public async Task Recover_WhenTheDatabaseInPlaceCantBeReadAgain_Answers503_AndMovesNothing()
    {
        // A file that can't be opened just then (another program holds it without sharing) is never moved unchecked.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Holding a file so that it can't be read is Windows' share mode.");
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 3);
        var root = SqliteFixture.RootPage(database, "Parents");
        var snapshot = Path.GetFileName((await database.SnapshotAsync(3, Ct))!);
        database.Dispose();
        var good = _fx.Directory.Combine("good.db");
        File.Copy(_fx.File, good);
        SqliteFixture.CorruptPage(Path.Combine(_fx.Snapshots, snapshot), root);
        File.Delete(_fx.File);
        var restarted = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, restarted.Open());
        File.Copy(good, _fx.File);
        var before = Listing();

        using (new FileStream(_fx.File, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failed = await Assert.ThrowsAsync<PluginException>(() => restarted.RecoverAsync(null, startEmpty: true, Ct));
            Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.RecheckFailed), (failed.Kind, failed.Message));
        }

        Assert.Equal(before, Listing());
        Assert.Empty(restarted.ListQuarantines());
    }

    // ------------------------------------------------------------------ 3: a recovery that fails is put back as it was

    [Fact]
    public async Task ARecoveryWhoseMoveAsideFailsPartWay_PutsBackWhatItMoved_AndLeavesNoFolder()
    {
        // The -wal held open without delete sharing: the database went into the folder, its -wal couldn't, so the database
        // comes back and the folder goes; the 503 says the move aside failed.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Holding a file so that it can't be moved is Windows' share mode.");
        var database = _fx.Open();
        var snapshot = await DamagedAtRunTimeAsync(database);
        var before = Listing();
        using (new FileStream(_fx.File + "-wal", FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var failed = await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(null, Ct));
            Assert.Equal((PluginErrorKind.Unavailable, "The damaged database couldn't be moved aside, so nothing was changed. The server log has the details."), (failed.Kind, failed.Message));
        }

        Assert.Equal(before, Listing());
        Assert.Empty(database.ListQuarantines());
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Status);

        Assert.Equal(snapshot, (await database.RecoverAsync(null, Ct)).RestoredFrom);
    }

    [Fact]
    public async Task ARecoveryWhoseSnapshotCantBePutInPlace_PutsTheDatabaseAndItsWalBack_AndSaysSo()
    {
        // The copy's rename into place fails (all three tries) once the database, its -wal and -shm are in the
        // quarantine folder. They go back as they were, the folder and the copy go, and the answer says what happened.
        var fail = true;
        var database = _fx.Open(fault: step =>
        {
            if (fail && step == "install")
            {
                throw new IOException("Simulated: the copy can't be renamed.");
            }
        });
        var snapshot = await DamagedAtRunTimeAsync(database);
        var before = Listing();

        var failed = await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(null, Ct));

        Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.RecoveryPutBack("The snapshot " + snapshot + " couldn't be put in place of the database", movedAny: true)), (failed.Kind, failed.Message));
        Assert.Equal("The snapshot " + snapshot + " couldn't be put in place of the database, so what had been moved aside was put back as it was and nothing was changed. The server log has the details.", failed.Message);
        Assert.Equal(before, Listing());
        Assert.Empty(database.ListQuarantines());
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Status);
        Assert.Null(database.LastRecovery);
        Assert.Single(_fx.Logger.At(LogLevel.Error), e => e.Message.Contains("Did not recover", StringComparison.Ordinal) && e.Message.Contains("put back as it was", StringComparison.Ordinal));

        // Once it can be, the same recovery goes through, the hot WAL quarantined with its database.
        fail = false;
        var recovery = await database.RecoverAsync(null, Ct);
        Assert.Equal(snapshot, recovery.RestoredFrom);
        Assert.Equal(["userratings.db", "userratings.db-shm", "userratings.db-wal"], recovery.QuarantinedFiles.Order(StringComparer.Ordinal));
        Assert.Equal(5, SqliteFixture.Count(database, "Parents"));
    }

    [Fact]
    public async Task ARecoveryWhoseSnapshotCantBeOpenedInPlace_PutsBackTheDatabase_AndTheMarkersAsTheyWere()
    {
        // The copy is in place and the markers written (the kept snapshots, the last restore), then the open fails: the copy
        // goes, the database comes back, .keep-snapshots is gone again and .last-restore holds what it held.
        var fail = true;
        var database = _fx.Open(fault: step =>
        {
            if (fail && step == "open")
            {
                throw new IOException("Simulated: the database in place can't be opened.");
            }
        });
        var snapshot = await DamagedAtRunTimeAsync(database);
        await File.WriteAllTextAsync(database.LastRestoreMarkerPath, "userratings-20261001-031000.db\n2026-10-01T03:10:00.0000000Z\n", Ct);
        var before = Listing();
        Assert.DoesNotContain(before, e => e.StartsWith("userratings.db.keep-snapshots", StringComparison.Ordinal));

        var failed = await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(snapshot, Ct));

        Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.RecoveryPutBack("The snapshot " + snapshot + " couldn't be opened in place of the database", movedAny: true)), (failed.Kind, failed.Message));
        Assert.Equal(before, Listing());
        Assert.Null(database.KeepSnapshotsBeforeUtc);
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Status);

        fail = false;
        Assert.Equal(snapshot, (await database.RecoverAsync(snapshot, Ct)).RestoredFrom);
        Assert.NotNull(database.KeepSnapshotsBeforeUtc);
    }

    [Fact]
    public async Task ARecoveryWhoseSnapshotCantBeUpgraded_PutsTheDatabaseBack_AndTheUpgradesSnapshotGoes()
    {
        // A schema 1 snapshot restored into a build whose step to 2 fails: the open took its pre-operation snapshot, and the
        // upgrade rolled back (ReadOnlyMigrationFailed). Before, the copy stayed in place read-only and the damaged database
        // in the quarantine folder; now everything is as it was, and the answer is 409 (another snapshot, or start empty).
        var v1 = _fx.Open(SqliteFixture.V1);
        SqliteFixture.AddFamilies(v1, 3, from: 100);
        var old = Path.GetFileName((await v1.SnapshotAsync(7, Ct))!);
        v1.Dispose();
        var v2 = _fx.Open(SqliteFixture.V2);
        Assert.Equal(SqliteDatabaseStatus.Migrated, v2.Open());
        v2.Dispose();
        IReadOnlyList<SqliteMigration> failing = [.. SqliteFixture.V1, new SqliteMigration(2, "add Parents.Extra", SqliteFixture.V2Sql + " SELECT no_such_function();")];
        var database = _fx.Open(failing);
        Assert.Equal(SqliteDatabaseStatus.Ok, database.Open());
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        var snapshot = await DamagedAtRunTimeAsync(database);
        var before = Listing();
        var preOp = PreOp();

        var failed = await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(old, Ct));

        Assert.Equal((PluginErrorKind.Conflict, SqliteMessages.RecoveryPutBack("The snapshot " + old + " couldn't be upgraded to schema 2", movedAny: true)), (failed.Kind, failed.Message));
        Assert.Equal(before, Listing());
        Assert.Equal(preOp, PreOp());
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Status);
        Assert.Null(database.KeepSnapshotsBeforeUtc);

        // The schema 2 snapshot needs no upgrade.
        Assert.Equal(snapshot, (await database.RecoverAsync(snapshot, Ct)).RestoredFrom);
    }

    [Fact]
    public async Task AStartEmptyWhoseNewDatabaseCantBeCreated_PutsTheDatabaseBack_AndKeepsNoMarker()
    {
        // The new database's schema step fails (a build whose step 1 can't run on an empty file): the new file goes, the
        // damaged database and its -wal come back, and the marker the start empty wrote is gone again.
        var v1 = _fx.Open(SqliteFixture.V1);
        Assert.Equal(SqliteDatabaseStatus.Ok, v1.Open());
        v1.Dispose();
        IReadOnlyList<SqliteMigration> failing = [new SqliteMigration(1, "create the tables", SqliteFixture.V1Sql + " SELECT no_such_function();")];
        var database = _fx.Open(failing);
        Assert.Equal(SqliteDatabaseStatus.Ok, database.Open());
        await DamagedAtRunTimeAsync(database);
        var before = Listing();

        var failed = await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(null, startEmpty: true, Ct));

        Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.RecoveryPutBack("The new empty database couldn't be created", movedAny: true)), (failed.Kind, failed.Message));
        Assert.Equal(before, Listing());
        Assert.False(File.Exists(Marker));
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Status);
        Assert.Equal(5, SqliteFixture.Count(database, "Parents"));
    }

    [Fact]
    public async Task TheRestoreAtOpen_WhoseSnapshotCantBePutInPlace_PutsTheDamagedDatabaseBack_AndItsBannerSaysSo()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 5);
        var root = SqliteFixture.RootPage(database, "Parents");
        var snapshot = Path.GetFileName((await database.SnapshotAsync(3, Ct))!);
        database.Dispose();
        SqliteFixture.CorruptPage(_fx.File, root);
        var damaged = await File.ReadAllBytesAsync(_fx.File, Ct);

        var restarted = _fx.Open(fault: step =>
        {
            if (step == "install")
            {
                throw new IOException("Simulated: the copy can't be renamed.");
            }
        });

        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, restarted.Open());
        Assert.StartsWith("The data failed its integrity check (", restarted.StatusMessage, StringComparison.Ordinal);
        Assert.EndsWith(
            "The snapshot " + snapshot + " couldn't be put in place of the database, so what had been moved aside was put back as it was found, and nothing can be changed. Restore a snapshot or start empty from the plugin's settings page, or restart the server to try again. The server log has the details.",
            restarted.StatusMessage,
            StringComparison.Ordinal);
        Assert.Equal(damaged, SqliteFixture.ReadOpenFile(_fx.File));
        Assert.Empty(restarted.ListQuarantines());
        Assert.False(File.Exists(Marker));
        Assert.False(File.Exists(restarted.LastRestoreMarkerPath));
        Assert.False(File.Exists(_fx.File + ".restore.tmp"));
        Assert.Null(restarted.LastRecovery);
    }

    [Fact]
    public async Task ARecoveryWhoseDatabaseCantBeMovedBack_SaysWhereItIs_AndKeepsItTogetherWithItsWal()
    {
        // The rename fails because something took the database's place (a folder of its name), so the database can't go
        // back either: it stays in its quarantine folder with its -wal and -shm, the answer names the folder, and nothing
        // reads or creates the file there until an admin's recovery or a restart.
        var database = _fx.Open(fault: step =>
        {
            if (step == "install")
            {
                Directory.CreateDirectory(_fx.File);
            }
        });
        var snapshot = await DamagedAtRunTimeAsync(database);
        var wal = SqliteFixture.ReadOpenFile(_fx.File + "-wal");

        var failed = await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(null, Ct));

        var folder = Assert.Single(database.ListQuarantines());
        Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.RecoveryNotPutBack("The snapshot " + snapshot + " couldn't be put in place of the database", folder.Name)), (failed.Kind, failed.Message));
        Assert.Equal(["reason.txt", "userratings.db", "userratings.db-shm", "userratings.db-wal"], folder.Files);
        Assert.Equal(wal, await File.ReadAllBytesAsync(Path.Combine(_fx.Folder, folder.Name, "userratings.db-wal"), Ct));
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Status);
        Assert.Equal(failed.Message, database.StatusMessage);
        Assert.Equal(SqliteMessages.Missing, Assert.Throws<PluginException>(() => SqliteFixture.Count(database, "Parents")).Message);
        Assert.False(File.Exists(Marker));
        Directory.Delete(_fx.File);
    }

    private static DateTimeOffset At(int day, int hour, int minute) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    private static byte[] ReadPage(string path, long page, int pageSize = 4096)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek((page - 1) * pageSize, SeekOrigin.Begin);
        var bytes = new byte[pageSize];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void WritePage(string path, long page, byte[] bytes, int pageSize = 4096)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek((page - 1) * pageSize, SeekOrigin.Begin);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    // Every file and folder in the database's folder (the snapshots' folder by name only), each file with its bytes' hash;
    // the -shm by name only, since a reader's connection writes its read marks there.
    private List<string> Listing()
    {
        var entries = Directory.GetDirectories(_fx.Folder).Select(d => Path.GetFileName(d) + "/").ToList();
        foreach (var file in Directory.GetFiles(_fx.Folder))
        {
            entries.Add(file.EndsWith("-shm", StringComparison.Ordinal)
                ? Path.GetFileName(file)
                : Path.GetFileName(file) + " " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(SqliteFixture.ReadOpenFile(file))));
        }

        return entries.Order(StringComparer.Ordinal).ToList();
    }

    // The pre-operation snapshots' names.
    private string[] PreOp() => SqliteFixture.Names(Path.Combine(_fx.Snapshots, "pre-op"));

    // A database with five families and the many-page Bulk table, a daily snapshot of it, then seven rows in Recent that
    // are only in the -wal, the pools open, and Bulk's root page overwritten: damaged at run time (quick_check), with a
    // hot WAL. Returns the snapshot's name.
    private async Task<string> DamagedAtRunTimeAsync(SqliteDatabase database)
    {
        SqliteFixture.AddFamilies(database, 5);
        SqliteFixture.AddBulk(database);
        var snapshot = Path.GetFileName((await database.SnapshotAsync(7, Ct))!);
        var root = SqliteFixture.RootPage(database, "Bulk");
        database.Release("test");
        SqliteFixture.AddRecent(database, 7);
        SqliteFixture.CorruptPage(_fx.File, root);
        Assert.False(database.QuickCheck()!.Ok);
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Status);
        Assert.True(new FileInfo(_fx.File + "-wal").Length > 0);
        return snapshot;
    }

    private SqliteDatabase OpenAt(DateTimeOffset time, IReadOnlyList<SqliteMigration>? migrations = null)
    {
        _fx.Clock.Set(time);
        return _fx.Open(migrations);
    }

    private IOrderedEnumerable<string> RecordedNames()
        => Assert.IsType<JsonArray>(JsonNode.Parse(File.ReadAllBytes(Marker))!["Snapshots"]).Select(n => (string)n!).Order(StringComparer.Ordinal);
}
