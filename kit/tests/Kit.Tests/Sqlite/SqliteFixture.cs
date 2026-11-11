using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Kit.Sqlite;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Sqlite;

/// <summary>
/// The SQLite tests share the process-wide connection pools (and one of them calls <c>ClearAllPools</c> on purpose), so
/// they run one at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqliteCollection
{
    public const string Name = "Sqlite";
}

/// <summary>
/// A test database: a schema of parents and children (a foreign key with <c>ON DELETE CASCADE</c>, as User Ratings'
/// <c>RatingKeys</c> has on <c>Ratings</c>), a bulk table spanning many pages and a small one, in a temporary folder, with
/// a manual clock and a logger that keeps every line. Helpers write rows, corrupt pages and copy the files as a crash
/// leaves them.
/// </summary>
internal sealed class SqliteFixture : IDisposable
{
    public const string V1Sql =
        "CREATE TABLE Parents (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);"
        + "CREATE TABLE Children (Id INTEGER PRIMARY KEY, ParentId INTEGER NOT NULL REFERENCES Parents(Id) ON DELETE CASCADE, Note TEXT NOT NULL);"
        + "CREATE INDEX IX_Children_Parent ON Children(ParentId);"
        + "CREATE TABLE Bulk (Id INTEGER PRIMARY KEY, Payload TEXT NOT NULL);"
        + "CREATE TABLE Recent (Id INTEGER PRIMARY KEY, Payload TEXT NOT NULL);";

    public const string V2Sql = "ALTER TABLE Parents ADD COLUMN Extra TEXT NULL;";

    // The 12-step rebuild of Parents (sqlite.org/lang_altertable.html, section 7): a CHECK added to Name.
    public const string RebuildParentsSql =
        "CREATE TABLE new_Parents (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL CHECK (length(Name) > 0), Extra TEXT NULL);"
        + "INSERT INTO new_Parents (Id, Name, Extra) SELECT Id, Name, Extra FROM Parents;"
        + "DROP TABLE Parents;"
        + "ALTER TABLE new_Parents RENAME TO Parents;";

    private readonly List<SqliteDatabase> _databases = [];

    public SqliteFixture()
    {
        Folder = Directory.Combine("userratings");
    }

    public TempDirectory Directory { get; } = new();

    public ListLogger Logger { get; } = new();

    public ManualTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 19, 3, 20, 0, TimeSpan.Zero));

    public string Folder { get; }

    public string File => Path.Combine(Folder, "userratings.db");

    public string Snapshots => Path.Combine(Folder, "snapshots");

    public static IReadOnlyList<SqliteMigration> V1 { get; } = [new SqliteMigration(1, "create the tables", V1Sql)];

    public static IReadOnlyList<SqliteMigration> V2 { get; } = [.. V1, new SqliteMigration(2, "add Parents.Extra", V2Sql)];

    // The second check at open runs at once: what it guards against (a transient failure that looks like damage) is
    // simulated by the tests that need it, not waited for.
    // fault: a recovery's later step made to fail (SqliteDatabase.RecoveryStepFault).
    // checkpointDelay, checkpointRetries: a requested checkpoint's waits (SqliteDatabase.CheckpointDelay and
    // CheckpointRetryDelays), short here by default; checkpointHook: SqliteDatabase.CheckpointTryHook.
    public SqliteDatabase Open(IReadOnlyList<SqliteMigration>? migrations = null, string? file = null, Action<string>? fault = null, TimeSpan? preOpKeepWithin = null, TimeSpan? checkpointDelay = null, IReadOnlyList<TimeSpan>? checkpointRetries = null, Action<string>? checkpointHook = null)
    {
        var database = new SqliteDatabase(file ?? File, migrations ?? V2, Logger, file is null ? Snapshots : null, Clock)
        {
            DamageRecheckDelay = TimeSpan.Zero,
            RecoveryStepFault = fault,
            PreOpKeepWithin = preOpKeepWithin,
            CheckpointDelay = checkpointDelay ?? TimeSpan.FromMilliseconds(10),
            CheckpointRetryDelays = checkpointRetries ?? [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)],
            CheckpointTryHook = checkpointHook
        };
        _databases.Add(database);
        return database;
    }

    public static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static long Count(SqliteDatabase database, string table)
        => database.Read(c => Scalar(c, "SELECT COUNT(*) FROM " + table + ";"));

    public static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public static string Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)!;
    }

    // Parents 1..n, each with two children.
    public static void AddFamilies(SqliteDatabase database, int parents, int from = 1)
        => database.WriteAsync(
            (c, t) =>
            {
                for (var i = from; i < from + parents; i++)
                {
                    Execute(c, t, string.Create(CultureInfo.InvariantCulture, $"INSERT INTO Parents (Id, Name) VALUES ({i}, 'parent {i}');"));
                    Execute(c, t, string.Create(CultureInfo.InvariantCulture, $"INSERT INTO Children (ParentId, Note) VALUES ({i}, 'a'), ({i}, 'b');"));
                }

                return 0;
            },
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    // Enough rows for Bulk to span many pages, so its root page is an interior page nothing else uses.
    public static void AddBulk(SqliteDatabase database, int rows = 2000)
        => database.WriteAsync(
            (c, t) =>
            {
                for (var i = 1; i <= rows; i++)
                {
                    Execute(c, t, string.Create(CultureInfo.InvariantCulture, $"INSERT INTO Bulk (Payload) VALUES ('{new string('x', 90)} {i} Zoë – 星 ✓');"));
                }

                return 0;
            },
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    public static void AddRecent(SqliteDatabase database, int rows)
        => database.WriteAsync(
            (c, t) =>
            {
                for (var i = 0; i < rows; i++)
                {
                    Execute(c, t, "INSERT INTO Recent (Payload) VALUES ('recent');");
                }

                return 0;
            },
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    public static long RootPage(SqliteDatabase database, string table)
        => database.Read(c => Scalar(c, "SELECT rootpage FROM sqlite_master WHERE name = '" + table + "';"));

    // Overwrites one page of a database file in place, as a bad sector or a stray write would; SQLite keeps its files open
    // with read and write sharing, so this works while connections are open.
    public static void CorruptPage(string path, long page, int pageSize = 4096)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek((page - 1) * pageSize, SeekOrigin.Begin);
        var garbage = new byte[pageSize];
        Array.Fill(garbage, (byte)0xFF);
        stream.Write(garbage);
        stream.Flush(flushToDisk: true);
    }

    // Copies a file that SQLite holds open, as a backup tool copying a live folder (or a crash) would leave it.
    public static void CopyOpenFile(string from, string to)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
    }

    // A real -wal of another database with the same page size (4096), as a crash leaves it: its frames, never checkpointed,
    // replace page 1 (the schema) and the pages after it, so a database replaying it is no longer the one it was (the SQLite
    // storage hardening). Returns the copy's path; the other database is at the given path.
    public static string ForeignWal(string other)
    {
        var copy = other + "-wal.copy";
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = other, Pooling = false }.ToString());
        connection.Open();
        Execute(connection, null, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
        Execute(connection, null, "CREATE TABLE Other (Id INTEGER PRIMARY KEY, Payload TEXT NOT NULL);");
        using (var transaction = connection.BeginTransaction())
        {
            for (var i = 0; i < 300; i++)
            {
                Execute(connection, transaction, string.Create(CultureInfo.InvariantCulture, $"INSERT INTO Other (Payload) VALUES ('{new string('o', 80)} {i}');"));
            }

            transaction.Commit();
        }

        CopyOpenFile(other + "-wal", copy);
        return copy;
    }

    // Copies a folder and everything in it as it is now, open files included: a server stopped at this moment leaves this.
    public static void CopyTree(string from, string to)
    {
        System.IO.Directory.CreateDirectory(to);
        foreach (var file in System.IO.Directory.GetFiles(from))
        {
            CopyOpenFile(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (var folder in System.IO.Directory.GetDirectories(from))
        {
            CopyTree(folder, Path.Combine(to, Path.GetFileName(folder)));
        }
    }

    public static byte[] ReadOpenFile(string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        source.CopyTo(copy);
        return copy.ToArray();
    }

    public static string[] Names(string folder)
        => System.IO.Directory.Exists(folder)
            ? System.IO.Directory.GetFiles(folder).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray()
            : [];

    public static long FileRows(string path, string table)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return Scalar(connection, "SELECT COUNT(*) FROM " + table + ";");
    }

    public void Dispose()
    {
        foreach (var database in _databases)
        {
            database.Dispose();
        }

        Directory.Dispose();
    }
}
