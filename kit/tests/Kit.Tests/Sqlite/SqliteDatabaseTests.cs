using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// <see cref="SqliteDatabase"/> on real SQLite files: the pragmas on every open, the
/// <c>user_version</c> migrations with their rollback, read-only for a newer version, the FK-safe rebuild, one writer, WAL
/// reads, release of the file, a crash image's rows, and the logical restore.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteDatabaseTests : IDisposable
{
    private readonly SqliteFixture _fx = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fx.Dispose();

    // ------------------------------------------------------------------------------------------------------- open

    [Fact]
    public void Open_CreatesTheDatabase_MigratesItToTheCurrentVersion_AndEveryConnectionHasThePragmas()
    {
        var database = _fx.Open();

        Assert.Equal(SqliteDatabaseStatus.Ok, database.Open());
        Assert.Null(database.LoadedVersion);
        Assert.Equal(2, database.SchemaVersion);
        Assert.Null(database.StatusMessage);
        Assert.False(database.IsReadOnly);
        Assert.True(File.Exists(_fx.File));

        // Read back inside the server: journal_mode=wal, synchronous=1 (NORMAL), foreign_keys=1, busy_timeout=5000.
        var pragmas = database.Read(c => (
            SqliteFixture.Text(c, "PRAGMA journal_mode;"),
            SqliteFixture.Scalar(c, "PRAGMA synchronous;"),
            SqliteFixture.Scalar(c, "PRAGMA foreign_keys;"),
            SqliteFixture.Scalar(c, "PRAGMA busy_timeout;"),
            SqliteFixture.Scalar(c, "PRAGMA user_version;")));
        Assert.Equal(("wal", 1L, 1L, 5000L, 2L), pragmas);

        // A new database has nothing to keep: no pre-operation snapshot.
        Assert.Empty(database.ListSnapshots());
        Assert.True(database.LastQuickCheck is null || database.LastQuickCheck.Ok);
    }

    [Fact]
    public void Open_OfAnExistingDatabase_ChecksItAndMigratesNothing()
    {
        var first = _fx.Open();
        SqliteFixture.AddFamilies(first, 3);
        first.Dispose();

        var again = _fx.Open();

        Assert.Equal(SqliteDatabaseStatus.Ok, again.Open());
        Assert.Equal(2, again.LoadedVersion);
        Assert.Null(again.MigratedFrom);
        Assert.Equal(3, SqliteFixture.Count(again, "Parents"));
        Assert.True(again.LastQuickCheck!.Ok);
        Assert.Equal(SqliteCheckResult.QuickCheck, again.LastQuickCheck.Kind);
        Assert.Empty(again.ListSnapshots());
    }

    [Fact]
    public async Task APooledConnection_LeftWithForeignKeysOff_GetsThemBackOnTheNextOpen()
    {
        // A pooled connection keeps its pragma state, so the kit's pragmas run on every open.
        var database = _fx.Open();
        database.Read(c =>
        {
            SqliteFixture.Execute(c, null, "PRAGMA foreign_keys=OFF; PRAGMA busy_timeout=1;");
            return 0;
        });

        Assert.Equal((1L, 5000L), database.Read(c => (SqliteFixture.Scalar(c, "PRAGMA foreign_keys;"), SqliteFixture.Scalar(c, "PRAGMA busy_timeout;"))));
        var orphan = await Assert.ThrowsAsync<SqliteException>(() => database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Children (ParentId, Note) VALUES (999, 'x');"); return 0; }, Ct));
        Assert.Equal(787, orphan.SqliteExtendedErrorCode);
    }

    // ------------------------------------------------------------------------------------------------- migrations

    [Fact]
    public void Upgrade_TakesAPreOperationSnapshotFirst_ThenMigrates()
    {
        var v1 = _fx.Open(SqliteFixture.V1);
        SqliteFixture.AddFamilies(v1, 2);
        v1.Dispose();

        var v2 = _fx.Open();

        Assert.Equal(SqliteDatabaseStatus.Migrated, v2.Open());
        Assert.Equal(1, v2.MigratedFrom);
        Assert.Equal(2, v2.SchemaVersion);
        Assert.Contains("upgraded from schema 1 to 2", v2.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(2, SqliteFixture.Count(v2, "Parents"));
        Assert.Equal(0L, v2.Read(c => SqliteFixture.Scalar(c, "SELECT COUNT(*) FROM Parents WHERE Extra IS NOT NULL;")));

        var snapshot = Assert.Single(v2.ListSnapshots());
        Assert.Equal(SnapshotKind.PreOp, snapshot.Kind);
        Assert.Equal(SnapshotOperations.Migrate, snapshot.Operation);
        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.Equal(2, SqliteFixture.FileRows(Path.Combine(_fx.Snapshots, "pre-op", snapshot.Name), "Parents"));
    }

    [Fact]
    public async Task AFailingStep_RollsBackEveryStepAndUserVersion_AndTheDatabaseIsReadOnly()
    {
        // A failing step rolls back every step and the user_version, through the kit.
        var failing = SqliteFixture.V2.Append(new SqliteMigration(3, "fails halfway", "CREATE TABLE Extra (A INTEGER); SELECT * FROM NoSuchTable;")).ToList();
        var database = _fx.Open(failing);

        Assert.Equal(SqliteDatabaseStatus.ReadOnlyMigrationFailed, database.Open());
        Assert.True(database.IsReadOnly);
        Assert.Contains("the step to version 3 (fails halfway) failed", database.StatusMessage, StringComparison.Ordinal);
        Assert.Single(_fx.Logger.At(LogLevel.Error), e => e.Message.Contains("step to version 3", StringComparison.Ordinal));

        await using (var connection = new SqliteConnection("Data Source=" + _fx.File + ";Mode=ReadOnly;Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            Assert.Equal(0L, SqliteFixture.Scalar(connection, "PRAGMA user_version;"));
            Assert.Equal(0L, SqliteFixture.Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';"));
        }

        var write = await Assert.ThrowsAsync<PluginException>(() => database.WriteAsync((_, _) => 0, Ct));
        Assert.Equal(PluginErrorKind.Unavailable, write.Kind);
        Assert.Equal(SqliteMessages.MigrationFailed, write.Message);
        Assert.Equal(SqliteMessages.MigrationFailed, Assert.Throws<PluginException>(() => database.Read(_ => 0)).Message);
        Assert.Equal(SqliteMessages.ExportMigrationFailed, Assert.Throws<PluginException>(() => database.ExportData((_, _) => new System.Text.Json.Nodes.JsonObject())).Message);
        Assert.Null(await database.SnapshotAsync(7, Ct));
        Assert.Equal(PluginErrorKind.Conflict, Assert.Throws<PluginException>(database.ThrowIfReadOnlyForAdmin).Kind);
    }

    [Fact]
    public void AFailingUpgradeOfAnExistingDatabase_KeepsItsRowsAndVersion_AndThePreUpgradeSnapshot()
    {
        var v1 = _fx.Open(SqliteFixture.V1);
        SqliteFixture.AddFamilies(v1, 4);
        v1.Dispose();
        var failing = new List<SqliteMigration>(SqliteFixture.V1) { new(2, "breaks", "ALTER TABLE Parents ADD COLUMN Extra TEXT; ALTER TABLE NoSuchTable ADD COLUMN X TEXT;") };

        var database = _fx.Open(failing);

        Assert.Equal(SqliteDatabaseStatus.ReadOnlyMigrationFailed, database.Open());
        Assert.Equal(1, database.LoadedVersion);
        Assert.Equal(4, SqliteFixture.FileRows(_fx.File, "Parents"));
        using (var connection = new SqliteConnection("Data Source=" + _fx.File + ";Mode=ReadOnly;Pooling=False"))
        {
            connection.Open();
            Assert.Equal(1L, SqliteFixture.Scalar(connection, "PRAGMA user_version;"));
            Assert.Equal(0L, SqliteFixture.Scalar(connection, "SELECT COUNT(*) FROM pragma_table_info('Parents') WHERE name = 'Extra';"));
        }

        Assert.Equal(SnapshotOperations.Migrate, Assert.Single(database.ListSnapshots()).Operation);
    }

    [Fact]
    public async Task ANewerUserVersion_IsReadOnly_AndTheFileIsUntouched()
    {
        // A newer user_version is read-only and untouched. A snapshot copied back by hand is in rollback mode;
        // even its journal mode stays as it is.
        var first = _fx.Open();
        SqliteFixture.AddFamilies(first, 2);
        first.Read(c =>
        {
            SqliteFixture.Execute(c, null, "PRAGMA user_version = 99;");
            return 0;
        });
        first.Dispose();
        await using (var connection = new SqliteConnection("Data Source=" + _fx.File + ";Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            SqliteFixture.Execute(connection, null, "PRAGMA journal_mode=DELETE;");
        }

        var before = await File.ReadAllBytesAsync(_fx.File, Ct);
        var database = _fx.Open();

        Assert.Equal(SqliteDatabaseStatus.ReadOnlyNewerVersion, database.Open());
        Assert.Equal(99, database.LoadedVersion);
        Assert.Equal(SqliteMessages.NewerVersion, database.StatusMessage);
        Assert.Equal(2, SqliteFixture.Count(database, "Parents"));
        Assert.Equal(SqliteMessages.NewerVersion, (await Assert.ThrowsAsync<PluginException>(() => database.WriteAsync((_, _) => 0, Ct))).Message);
        Assert.Equal(SqliteMessages.ExportNewerVersion, Assert.Throws<PluginException>(() => database.ExportData((_, _) => new System.Text.Json.Nodes.JsonObject())).Message);
        Assert.Null(await database.SnapshotAsync(7, Ct));
        Assert.Null(await database.PreOpSnapshotAsync(SnapshotOperations.Import, Ct));
        database.Dispose();

        Assert.Equal(before, await File.ReadAllBytesAsync(_fx.File, Ct));
        Assert.Equal(["userratings.db"], SqliteFixture.Names(_fx.Folder));
    }

    [Fact]
    public async Task ARebuildMigration_KeepsEveryChildRow_AndForeignKeysAreOnAgainAfterwards()
    {
        // A migration that rebuilds the parent table through the kit's FK-safe procedure keeps every child row.
        var v2 = _fx.Open();
        SqliteFixture.AddFamilies(v2, 25);
        v2.Dispose();
        var rebuild = new List<SqliteMigration>(SqliteFixture.V2) { new(3, "rebuild Parents with a CHECK", SqliteFixture.RebuildParentsSql) { RequiresRebuild = true } };

        var database = _fx.Open(rebuild);

        Assert.Equal(SqliteDatabaseStatus.Migrated, database.Open());
        Assert.Equal(25, SqliteFixture.Count(database, "Parents"));
        Assert.Equal(50, SqliteFixture.Count(database, "Children"));
        Assert.Contains("CHECK (length(Name) > 0)", database.Read(c => SqliteFixture.Text(c, "SELECT sql FROM sqlite_master WHERE name = 'Parents';")), StringComparison.Ordinal);

        // The children still point at the rebuilt table, and the next pooled connection has foreign keys on.
        Assert.Equal(1L, database.Read(c => SqliteFixture.Scalar(c, "PRAGMA foreign_keys;")));
        await database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "DELETE FROM Parents WHERE Id = 1;"); return 0; }, Ct);
        Assert.Equal(48, SqliteFixture.Count(database, "Children"));
        await Assert.ThrowsAsync<SqliteException>(() => database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Children (ParentId, Note) VALUES (999, 'x');"); return 0; }, Ct));
    }

    [Fact]
    public void Control_TheSameRebuildWithoutRequiresRebuild_CascadesAndDeletesTheChildRows()
    {
        // Why RequiresRebuild exists: with foreign_keys=ON, DROP TABLE Parents deletes every child row.
        var v2 = _fx.Open();
        SqliteFixture.AddFamilies(v2, 25);
        v2.Dispose();
        var careless = new List<SqliteMigration>(SqliteFixture.V2) { new(3, "rebuild Parents, carelessly", SqliteFixture.RebuildParentsSql) };

        var database = _fx.Open(careless);

        Assert.Equal(SqliteDatabaseStatus.Migrated, database.Open());
        Assert.Equal(25, SqliteFixture.Count(database, "Parents"));
        Assert.Equal(0, SqliteFixture.Count(database, "Children"));
    }

    [Fact]
    public void ARebuildThatBreaksAForeignKey_FailsTheCheck_AndRollsEverythingBack()
    {
        var v2 = _fx.Open();
        SqliteFixture.AddFamilies(v2, 10);
        v2.Dispose();
        const string LosesParents =
            "CREATE TABLE new_Parents (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Extra TEXT NULL);"
            + "INSERT INTO new_Parents SELECT Id, Name, Extra FROM Parents WHERE Id <= 5;"
            + "DROP TABLE Parents;"
            + "ALTER TABLE new_Parents RENAME TO Parents;";
        var broken = new List<SqliteMigration>(SqliteFixture.V2) { new(3, "rebuild that loses parents", LosesParents) { RequiresRebuild = true } };

        var database = _fx.Open(broken);

        Assert.Equal(SqliteDatabaseStatus.ReadOnlyMigrationFailed, database.Open());
        var error = Assert.Single(_fx.Logger.At(LogLevel.Error));
        Assert.Contains("foreign_key_check found 10 rows", error.Exception!.Message, StringComparison.Ordinal);
        Assert.Equal(10, SqliteFixture.FileRows(_fx.File, "Parents"));
        Assert.Equal(20, SqliteFixture.FileRows(_fx.File, "Children"));
        using var connection = new SqliteConnection("Data Source=" + _fx.File + ";Mode=ReadOnly;Pooling=False");
        connection.Open();
        Assert.Equal(2L, SqliteFixture.Scalar(connection, "PRAGMA user_version;"));
    }

    [Fact]
    public void TheConstructor_RefusesMigrationsWithGaps()
    {
        Assert.Throws<ArgumentException>(() => new SqliteDatabase(_fx.File, [], _fx.Logger));
        Assert.Throws<ArgumentException>(() => new SqliteDatabase(_fx.File, [new SqliteMigration(2, "two", "SELECT 1;")], _fx.Logger));
        Assert.Throws<ArgumentException>(() => new SqliteDatabase(_fx.File, [.. SqliteFixture.V1, new SqliteMigration(1, "again", "SELECT 1;")], _fx.Logger));
        Assert.Throws<ArgumentException>(() => new SqliteDatabase(_fx.File, [new SqliteMigration(1, "empty", " ")], _fx.Logger));
        Assert.False(Directory.Exists(_fx.Folder), "Nothing is created before the database is used.");
    }

    // ------------------------------------------------------------------------------------------- reads and writes

    [Fact]
    public async Task Writes_RunOneAtATime()
    {
        var database = _fx.Open();
        await database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Parents (Id, Name) VALUES (1, '0');"); return 0; }, Ct);

        // Each write reads the value and writes it back one higher; with two writers at once some increments would be lost.
        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() => database.WriteAsync(
            (c, t) =>
            {
                using var read = c.CreateCommand();
                read.Transaction = t;
                read.CommandText = "SELECT CAST(Name AS INTEGER) FROM Parents WHERE Id = 1;";
                var value = (long)read.ExecuteScalar()!;
                SqliteFixture.Execute(c, t, "UPDATE Parents SET Name = '" + (value + 1) + "' WHERE Id = 1;");
                return value;
            },
            Ct))));

        Assert.Equal("40", database.Read(c => SqliteFixture.Text(c, "SELECT Name FROM Parents WHERE Id = 1;")));
        Assert.Equal(41, database.Generation);
    }

    [Fact]
    public async Task AReaderInATransaction_KeepsItsView_WhileAWriterCommits()
    {
        // In WAL mode a commit doesn't wait for an open reader (the rollback-journal control fails busy).
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 1);
        var reading = new SemaphoreSlim(0);
        var committed = new SemaphoreSlim(0);

        var reader = Task.Run(() => database.Read(c =>
        {
            using var transaction = c.BeginTransaction(deferred: true);
            using var count = c.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM Parents;";
            var before = (long)count.ExecuteScalar()!;
            reading.Release();
            committed.Wait(TimeSpan.FromSeconds(10));
            var during = (long)count.ExecuteScalar()!;
            transaction.Commit();
            return (before, during);
        }));
        await reading.WaitAsync(Ct);
        await database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Parents (Id, Name) VALUES (2, 'b');"); return 0; }, Ct);
        committed.Release();

        Assert.Equal((1L, 1L), await reader);
        Assert.Equal(2, SqliteFixture.Count(database, "Parents"));
    }

    [Fact]
    public async Task AWriteThatThrows_IsRolledBack()
    {
        var database = _fx.Open();

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.WriteAsync<int>(
            (c, t) =>
            {
                SqliteFixture.Execute(c, t, "INSERT INTO Parents (Id, Name) VALUES (1, 'a');");
                throw new InvalidOperationException("no");
            },
            Ct));

        Assert.Equal(0, SqliteFixture.Count(database, "Parents"));
        Assert.Equal(0, database.Generation);
    }

    // A check run before the pre-operation snapshot, in the same hold of the writer lock and rolled back. One that
    // throws (an import whose plan changed) writes nothing and leaves no snapshot; one that passes sees the state the
    // write will, its own work undone, and the write runs after the snapshot as before.
    [Fact]
    public async Task ACheckBeforeTheSnapshot_ThatRefuses_WritesNothing_AndLeavesNoSnapshot_OneThatPasses_LetsTheWriteRun()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 3);
        var generation = database.Generation;
        var writes = 0;
        static long Rows(SqliteConnection c, SqliteTransaction t, string table)
        {
            using var command = c.CreateCommand();
            command.Transaction = t;
            command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
            return (long)command.ExecuteScalar()!;
        }

        var refused = await Assert.ThrowsAsync<PluginException>(() => database.RunWriteAsync(
            (c, t) =>
            {
                writes++;
                SqliteFixture.Execute(c, t, "DELETE FROM Parents;");
                return 1;
            },
            commit: true,
            SnapshotOperations.Import,
            check: (c, t) =>
            {
                SqliteFixture.Execute(c, t, "DELETE FROM Children;");
                throw PluginException.Conflict("The plan changed.");
            },
            Ct));

        Assert.Equal((PluginErrorKind.Conflict, "The plan changed."), (refused.Kind, refused.Message));
        Assert.Equal(0, writes);
        Assert.Empty(database.ListSnapshots());
        Assert.False(Directory.Exists(_fx.Snapshots) && Directory.EnumerateFiles(_fx.Snapshots, "*", SearchOption.AllDirectories).Any());
        Assert.Equal((3L, 6L), (SqliteFixture.Count(database, "Parents"), SqliteFixture.Count(database, "Children")));
        Assert.Equal(generation, database.Generation);

        long? checkSaw = null;
        var (result, safety) = await database.RunWriteAsync(
            (c, t) =>
            {
                writes++;
                var children = Rows(c, t, "Children");
                SqliteFixture.Execute(c, t, "DELETE FROM Parents WHERE Id = 1;");
                return children;
            },
            commit: true,
            SnapshotOperations.Import,
            check: (c, t) =>
            {
                SqliteFixture.Execute(c, t, "DELETE FROM Children;");
                checkSaw = Rows(c, t, "Children");
            },
            Ct);

        Assert.Equal((0L, 6L, 1), (checkSaw, result, writes));
        Assert.Equal(SnapshotOperations.Import, Assert.Single(database.ListSnapshots()).Operation);
        Assert.Equal(6, SqliteFixture.FileRows(Path.Combine(_fx.Snapshots, "pre-op", Path.GetFileName(safety!)), "Children"));
        Assert.Equal((2L, 4L), (SqliteFixture.Count(database, "Parents"), SqliteFixture.Count(database, "Children")));
        Assert.Equal(generation + 1, database.Generation);

        // With no snapshot asked for (a dry run), there is no check: the write's own refuses it, rolled back.
        await database.RunWriteAsync((_, _) => 0, commit: false, preOpOperation: null, check: (_, _) => throw new InvalidOperationException("never run"), Ct);
    }

    // --------------------------------------------------------------------------------------------- the file itself

    [Fact]
    public void Release_FreesOnlyThisDatabasesFile_AndCheckpointsTheWal()
    {
        // ClearPool is per connection string: the server's jellyfin.db, pooled in the same process, stays open.
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 5);
        var server = _fx.Directory.Combine("jellyfin.db");
        using (var connection = new SqliteConnection("Data Source=" + server + ";Pooling=True"))
        {
            connection.Open();
            SqliteFixture.Execute(connection, null, "PRAGMA journal_mode=WAL; CREATE TABLE T (A INTEGER);");
        }

        Assert.Equal("locked", SqliteFiles.TryOpenExclusive(_fx.File));
        Assert.NotEmpty(SqliteFiles.ListSidecars(_fx.File));

        Assert.Equal("free", database.Release("test"));

        Assert.Empty(SqliteFiles.ListSidecars(_fx.File));
        Assert.Equal("locked", SqliteFiles.TryOpenExclusive(server));
        Assert.Equal(5, SqliteFixture.Count(database, "Parents"));

        using var clear = new SqliteConnection("Data Source=" + server + ";Pooling=True");
        SqliteConnection.ClearPool(clear);
    }

    [Fact]
    public void ACrashImage_KeepsEveryCommittedRow()
    {
        // A hard kill: the files as a crash leaves them, the committed rows still in -wal, replayed at the next open.
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 30);
        Assert.NotEmpty(SqliteFiles.ListSidecars(_fx.File));
        var image = _fx.Directory.Combine("crash", "userratings.db");
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            SqliteFixture.CopyOpenFile(_fx.File + suffix, image + suffix);
        }

        Assert.True(new FileInfo(image + "-wal").Length > 0);
        var restarted = _fx.Open(file: image);

        Assert.Equal(SqliteDatabaseStatus.Ok, restarted.Open());
        Assert.Equal(30, SqliteFixture.Count(restarted, "Parents"));
        Assert.Equal(60, SqliteFixture.Count(restarted, "Children"));
        Assert.True(restarted.QuickCheck()!.Ok);
    }

    [Fact]
    public void SidecarsWithoutTheirDatabase_AreMovedAside_BeforeANewDatabaseIsCreated()
    {
        Directory.CreateDirectory(_fx.Folder);
        File.WriteAllText(_fx.File + "-wal", "another database's log");
        var database = _fx.Open();

        Assert.Equal(SqliteDatabaseStatus.Ok, database.Open());
        var quarantine = Assert.Single(database.ListQuarantines());
        Assert.Contains("userratings.db-wal", quarantine.Files);
        Assert.Equal("another database's log", File.ReadAllText(Path.Combine(_fx.Folder, quarantine.Name, "userratings.db-wal")));
    }

    // -------------------------------------------------------------------------------------------- logical restore

    [Fact]
    public async Task RestoreSnapshot_IntoTheLiveWalDatabase_WithOpenPooledConnections_StaysInWalMode()
    {
        // Restore snapshot into a live WAL database with open pooled connections; afterwards the live
        // journal_mode is still wal (the snapshot is in rollback mode, so their headers differ).
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 3);
        var snapshot = Path.GetFileName((await database.SnapshotAsync(7, Ct))!);
        _fx.Clock.Advance(TimeSpan.FromHours(5));
        SqliteFixture.AddFamilies(database, 4, from: 10);
        Assert.Equal(7, SqliteFixture.Count(database, "Parents"));
        Assert.Equal(1, SqliteFiles.ReadHeader(Path.Combine(_fx.Snapshots, snapshot))!.Value.WriteVersion);
        var (restores, generation) = (database.RestoreCount, database.Generation);

        var result = await database.RestoreSnapshotAsync(snapshot, Ct);

        // A restore moves RestoreCount (and the generation); the writes before moved the generation only.
        Assert.Equal(0L, restores);
        Assert.Equal((1L, generation + 1), (database.RestoreCount, database.Generation));

        Assert.Equal(snapshot, result.RestoredFrom);
        Assert.Equal(2, result.SchemaVersion);
        Assert.Null(result.MigratedFrom);
        Assert.StartsWith("userratings-restore-", result.SafetySnapshot, StringComparison.Ordinal);
        Assert.Equal(SqliteDatabaseStatus.Ok, database.Status);
        Assert.Equal(3, SqliteFixture.Count(database, "Parents"));
        Assert.Equal("wal", database.Read(c => SqliteFixture.Text(c, "PRAGMA journal_mode;")));
        Assert.True(database.QuickCheck()!.Ok);
        Assert.Equal(7, SqliteFixture.FileRows(Path.Combine(_fx.Snapshots, "pre-op", result.SafetySnapshot!), "Parents"));

        Assert.Equal("free", database.Release("test"));
        Assert.True(SqliteFiles.ReadHeader(_fx.File)!.Value.IsWal);
        Assert.Empty(SqliteFiles.ListSidecars(_fx.File));
        await database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Parents (Id, Name) VALUES (50, 'after');"); return 0; }, Ct);
        Assert.Equal(4, SqliteFixture.Count(database, "Parents"));
    }

    [Fact]
    public async Task RestoreSnapshot_OfAnOlderVersion_IsUpgraded()
    {
        var v1 = _fx.Open(SqliteFixture.V1);
        SqliteFixture.AddFamilies(v1, 2);
        var old = Path.GetFileName((await v1.SnapshotAsync(7, Ct))!);
        v1.Dispose();
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        var v2 = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.Migrated, v2.Open());
        SqliteFixture.AddFamilies(v2, 3, from: 10);

        var result = await v2.RestoreSnapshotAsync(old, Ct);

        Assert.Equal(1, result.SchemaVersion);
        Assert.Equal(1, result.MigratedFrom);
        Assert.Equal(2, v2.SchemaVersion);
        Assert.Equal(2, SqliteFixture.Count(v2, "Parents"));
        Assert.Equal(0L, v2.Read(c => SqliteFixture.Scalar(c, "SELECT COUNT(*) FROM Parents WHERE Extra IS NOT NULL;")));
    }

    [Fact]
    public async Task RestoreSnapshot_Refuses_AnUnknownName_ANewerSnapshot_AndADamagedOne()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 2);
        var good = Path.GetFileName((await database.SnapshotAsync(7, Ct))!);

        Assert.Equal(PluginErrorKind.NotFound, (await Assert.ThrowsAsync<PluginException>(() => database.RestoreSnapshotAsync("..\\userratings.db", Ct))).Kind);

        _fx.Clock.Advance(TimeSpan.FromSeconds(1));
        var newer = Path.Combine(_fx.Snapshots, "userratings-20261019-040000.db");
        File.Copy(Path.Combine(_fx.Snapshots, good), newer);
        await using (var connection = new SqliteConnection("Data Source=" + newer + ";Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            SqliteFixture.Execute(connection, null, "PRAGMA user_version = 7;");
        }

        var refused = await Assert.ThrowsAsync<PluginException>(() => database.RestoreSnapshotAsync(Path.GetFileName(newer), Ct));
        Assert.Equal(PluginErrorKind.Conflict, refused.Kind);
        Assert.Contains("newer plugin version", refused.Message, StringComparison.Ordinal);

        var damaged = Path.Combine(_fx.Snapshots, "userratings-20261019-050000.db");
        File.Copy(Path.Combine(_fx.Snapshots, good), damaged);
        SqliteFixture.CorruptPage(damaged, 2);
        var broken = await Assert.ThrowsAsync<PluginException>(() => database.RestoreSnapshotAsync(Path.GetFileName(damaged), Ct));
        Assert.Equal(PluginErrorKind.Conflict, broken.Kind);
        Assert.Contains("failed its integrity check", broken.Message, StringComparison.Ordinal);

        Assert.Equal(2, SqliteFixture.Count(database, "Parents"));
        Assert.DoesNotContain(database.ListSnapshots(), s => s.Operation == SnapshotOperations.Restore);
    }

    [Fact]
    public async Task ManualRestore_WithTheServerStopped_AfterDeletingWalAndShm()
    {
        // The manual restore, server stopped: delete -wal and -shm, copy the snapshot over the database, start.
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 3);
        var snapshot = (await database.SnapshotAsync(7, Ct))!;
        SqliteFixture.AddFamilies(database, 5, from: 10);
        var image = _fx.Directory.Combine("stopped", "userratings.db");
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            SqliteFixture.CopyOpenFile(_fx.File + suffix, image + suffix);
        }

        File.Delete(image + "-wal");
        File.Delete(image + "-shm");
        File.Copy(snapshot, image, overwrite: true);
        var restarted = _fx.Open(file: image);

        Assert.Equal(SqliteDatabaseStatus.Ok, restarted.Open());
        Assert.Equal(3, SqliteFixture.Count(restarted, "Parents"));
        Assert.Equal("wal", restarted.Read(c => SqliteFixture.Text(c, "PRAGMA journal_mode;")));
    }

    // ------------------------------------------------------------------------------------------- export and import

    [Fact]
    public void ExportData_ReadsOneStateOfTheData_AtTheCurrentVersion()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 2);

        var export = database.ExportData((c, t) =>
        {
            using var command = c.CreateCommand();
            command.Transaction = t;
            command.CommandText = "SELECT COUNT(*) FROM Parents;";
            return new System.Text.Json.Nodes.JsonObject { ["Parents"] = (long)command.ExecuteScalar()! };
        });

        Assert.Equal(2, export.SchemaVersion);
        Assert.Equal(2L, (long)export.Data["Parents"]!);
    }

    [Fact]
    public void IsBindingFailure_TellsAMissingSqliteAssembly_FromAMissingFile()
    {
        Assert.True(SqliteDatabase.IsBindingFailure(new FileNotFoundException("x", "Microsoft.Data.Sqlite, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60")));
        Assert.True(SqliteDatabase.IsBindingFailure(new FileLoadException("x")));
        Assert.True(SqliteDatabase.IsBindingFailure(new TypeLoadException("x")));
        Assert.True(SqliteDatabase.IsBindingFailure(new MissingMethodException("x")));
        Assert.False(SqliteDatabase.IsBindingFailure(new FileNotFoundException("x", @"C:\data\userratings\snapshots\userratings-20261019-032000.db")));
        Assert.False(SqliteDatabase.IsBindingFailure(new IOException("x")));
    }
}
