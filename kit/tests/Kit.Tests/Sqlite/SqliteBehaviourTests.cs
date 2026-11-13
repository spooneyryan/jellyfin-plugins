using System;
using System.IO;
using Jellyfin.Plugin.Kit.Sqlite;
using Jellyfin.Plugin.Kit.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Sqlite;

/// <summary>
/// The SQLite behaviours <see cref="SqliteDatabase"/> is built on, measured inside Jellyfin 10.11.5 and pinned here on the
/// real Microsoft.Data.Sqlite 9.0.11 package, whose e_sqlite3 is the server's own build.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteBehaviourTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void TheTestPackage_IsTheServersSqliteBuild_WithForeignKeysOnByDefault()
    {
        // Same native build as the server of the build's line ships (Jellyfin 10.11.5: SQLite 3.46.1; 12.4: 3.53.3, both
        // DEFAULT_FOREIGN_KEYS), so these tests describe the server. The kit runs its pragmas anyway, so behaviour doesn't
        // depend on the build.
        using var connection = Open(_directory.Combine("defaults.db"), pooling: false);

        Assert.Equal(JellyfinLine.Is12 ? "3.53.3" : "3.46.1", SqliteFixture.Text(connection, "SELECT sqlite_version();"));
        Assert.Equal(1L, SqliteFixture.Scalar(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(2L, SqliteFixture.Scalar(connection, "PRAGMA synchronous;"));
        Assert.Equal("delete", SqliteFixture.Text(connection, "PRAGMA journal_mode;"));
    }

    [Fact]
    public void APooledConnection_KeepsTheFileOpen_UntilItsPoolIsCleared()
    {
        // Why every file operation clears the database's pool first, and why snapshot connections are unpooled.
        var path = _directory.Combine("pooled.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            SqliteFixture.Execute(connection, null, "CREATE TABLE T (A INTEGER);");
        }

        Assert.Equal("locked", SqliteFiles.TryOpenExclusive(path));
        Assert.Throws<IOException>(() => File.Delete(path));

        using (var connection = new SqliteConnection(connectionString))
        {
            SqliteConnection.ClearPool(connection);
        }

        Assert.Equal("free", SqliteFiles.TryOpenExclusive(path));
        File.Delete(path);
    }

    [Fact]
    public void ClearPool_IsPerConnectionString_AndClearAllPools_ClosesEveryPoolInTheProcess()
    {
        // In Jellyfin the process-wide pools include the server's own jellyfin.db connections: the kit never calls
        // ClearAllPools (Repo.Tests greps for it).
        var ours = _directory.Combine("ours.db");
        var theirs = _directory.Combine("theirs.db");
        foreach (var path in new[] { ours, theirs })
        {
            using var connection = new SqliteConnection("Data Source=" + path);
            connection.Open();
            SqliteFixture.Execute(connection, null, "CREATE TABLE T (A INTEGER);");
        }

        using (var connection = new SqliteConnection("Data Source=" + ours))
        {
            SqliteConnection.ClearPool(connection);
        }

        Assert.Equal("free", SqliteFiles.TryOpenExclusive(ours));
        Assert.Equal("locked", SqliteFiles.TryOpenExclusive(theirs));

        SqliteConnection.ClearAllPools();
        Assert.Equal("free", SqliteFiles.TryOpenExclusive(theirs));
    }

    [Fact]
    public void APooledConnection_KeepsItsPragmaState_ForTheNextUser()
    {
        // Why the kit runs its pragmas on every open, and why a rebuild with foreign keys off uses an unpooled connection.
        var connectionString = "Data Source=" + _directory.Combine("state.db") + ";Pooling=True";
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            SqliteFixture.Execute(connection, null, "PRAGMA foreign_keys=OFF; PRAGMA busy_timeout=1234;");
        }

        using (var again = new SqliteConnection(connectionString))
        {
            again.Open();
            Assert.Equal(0L, SqliteFixture.Scalar(again, "PRAGMA foreign_keys;"));
            Assert.Equal(1234L, SqliteFixture.Scalar(again, "PRAGMA busy_timeout;"));
        }

        using var clear = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(clear);
    }

    [Fact]
    public void Control_WithARollbackJournal_ACommitUnderAnOpenReader_FailsBusy()
    {
        // The positive control of the WAL isolation test in SqliteDatabaseTests: without WAL the same sequence can't commit.
        var path = _directory.Combine("delete-mode.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 1, Pooling = false }.ToString();
        using var reader = new SqliteConnection(connectionString);
        using var writer = new SqliteConnection(connectionString);
        reader.Open();
        writer.Open();
        SqliteFixture.Execute(writer, null, "PRAGMA journal_mode=DELETE; PRAGMA busy_timeout=100; CREATE TABLE T (A INTEGER); INSERT INTO T VALUES (1);");

        using var read = reader.BeginTransaction(deferred: true);
        using (var command = reader.CreateCommand())
        {
            command.Transaction = read;
            command.CommandText = "SELECT COUNT(*) FROM T;";
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
        }

        using var write = writer.BeginTransaction();
        SqliteFixture.Execute(writer, write, "INSERT INTO T VALUES (2);");
        var ex = Assert.Throws<SqliteException>(() => write.Commit());

        Assert.Equal(5, ex.SqliteErrorCode);
    }

    [Fact]
    public void Control_ABackupOfAWalDatabase_IsAWalFile_ThatAReadOnlyCheckLeavesSidecarsBeside()
    {
        // Why the kit switches every snapshot to journal_mode=DELETE before it checks it.
        var live = _directory.Combine("live.db");
        var copy = _directory.Combine("copy.db");
        using (var source = Open(live, pooling: false))
        {
            SqliteFixture.Execute(source, null, "PRAGMA journal_mode=WAL; CREATE TABLE T (A INTEGER); INSERT INTO T VALUES (1);");
            using var destination = Open(copy, pooling: false);
            source.BackupDatabase(destination);
        }

        var header = SqliteFiles.ReadHeader(copy)!.Value;
        Assert.True(header.IsWal);
        using (var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            check.Open();
            Assert.Equal("ok", SqliteFixture.Text(check, "PRAGMA integrity_check;"));
        }

        Assert.Equal(["copy.db", "copy.db-shm", "copy.db-wal", "live.db"], SqliteFixture.Names(_directory.Path));
    }

    [Fact]
    public void ClosingTheLastConnection_CheckpointsTheWal_UnlessAReadOnlyConnectionStaysOpenUntilAfter()
    {
        // Why the corruption restore holds a read-only connection open while it clears the pools: otherwise the last of
        // its pooled connections to close checkpoints the WAL into the damaged file and deletes it, and the quarantine no
        // longer holds the files as they were found.
        foreach (var guarded in new[] { false, true })
        {
            var path = _directory.Combine(guarded ? "guarded.db" : "plain.db");
            var connectionString = "Data Source=" + path + ";Pooling=True";
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                SqliteFixture.Execute(connection, null, "PRAGMA journal_mode=WAL; CREATE TABLE T (A TEXT); INSERT INTO T VALUES ('one'), ('two');");
            }

            var wal = new FileInfo(path + "-wal").Length;
            Assert.True(wal > 0);
            SqliteConnection? guard = null;
            if (guarded)
            {
                guard = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False");
                guard.Open();
                SqliteFixture.Scalar(guard, "PRAGMA user_version;");
            }

            using (var clear = new SqliteConnection(connectionString))
            {
                SqliteConnection.ClearPool(clear);
            }

            guard?.Dispose();
            if (guarded)
            {
                Assert.Equal(wal, new FileInfo(path + "-wal").Length);
                Assert.True(File.Exists(path + "-shm"));
            }
            else
            {
                Assert.False(File.Exists(path + "-wal"), "Checkpointed into the database and deleted.");
            }

            Assert.Equal(2L, SqliteFixture.FileRows(path, "T"));
        }
    }

    private static SqliteConnection Open(string path, bool pooling)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = pooling }.ToString());
        connection.Open();
        return connection;
    }
}
