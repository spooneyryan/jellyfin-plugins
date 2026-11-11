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
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Sqlite;

/// <summary>
/// The damage marker: damage only <c>integrity_check</c> sees, found by the check before a pre-operation snapshot, made
/// the database read-only until a restart, which opened it again as it was (<c>quick_check</c> passes), and the message
/// promised a restore that never came. A restart now checks it again: only indexes damaged, they are rebuilt (REINDEX,
/// a copy kept first); otherwise the newest good snapshot is restored.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteDamageMarkerTests : IDisposable
{
    private readonly SqliteFixture _fx = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task IndexOnlyDamage_FoundBeforeAPreOpSnapshot_IsRebuiltAtTheRestart_AndNoRowIsLost()
    {
        var database = SeededWithASnapshot();

        // The index now claims to be on Note: its entries (ParentId values) no longer match. The tables are intact.
        Damage("UPDATE sqlite_master SET sql='CREATE INDEX IX_Children_Parent ON Children(Note)' WHERE name='IX_Children_Parent';");
        Assert.Equal("ok", QuickCheck());

        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        var refused = await Assert.ThrowsAsync<PluginException>(() => database.RunWriteAsync((_, _) => 0, commit: true, SnapshotOperations.Purge, Ct));
        Assert.Equal(PluginErrorKind.Unavailable, refused.Kind);
        Assert.Equal(SqliteMessages.IndexesDamaged, refused.Message);
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Status);
        Assert.Contains("indexes failed their integrity check", database.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("rebuild the indexes", database.StatusMessage, StringComparison.Ordinal);
        Assert.True(File.Exists(database.DamageMarkerPath));
        database.Dispose();

        using var restarted = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.Ok, restarted.Open());
        Assert.Null(restarted.LastRecovery);
        var reindex = Assert.IsType<SqliteReindexResult>(restarted.LastReindex);
        Assert.Contains("missing from index IX_Children_Parent", reindex.Reason, StringComparison.Ordinal);
        Assert.Contains("userratings.db", reindex.CopiedFiles);
        Assert.True(File.Exists(Path.Combine(_fx.Folder, reindex.CopyFolder, "userratings.db")));
        Assert.Contains(restarted.ListQuarantines(), q => q.Name == reindex.CopyFolder);
        Assert.False(File.Exists(restarted.DamageMarkerPath));
        Assert.Equal("ok", IntegrityCheck());

        // Nothing restored from the snapshot: the rows written after it are still there, and writes go on.
        await restarted.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Parents (Id, Name) VALUES (1000, 'after the restart');"); return 0; }, Ct);
        Assert.Equal("52", Count("Parents"));
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        await restarted.RunWriteAsync((_, _) => 0, commit: true, SnapshotOperations.Purge, Ct);
        Assert.Equal(SqliteDatabaseStatus.Ok, restarted.Status);
    }

    [Fact]
    public async Task DamageARebuildCantRepair_FoundBeforeAPreOpSnapshot_IsRestoredFromTheNewestSnapshotAtTheRestart()
    {
        var database = SeededWithASnapshot();

        // A UNIQUE index over duplicate values: integrity_check finds it (quick_check doesn't check UNIQUE), and REINDEX
        // can't build it.
        Damage("UPDATE sqlite_master SET sql='CREATE UNIQUE INDEX IX_Children_Parent ON Children(ParentId)' WHERE name='IX_Children_Parent';");
        Assert.Equal("ok", QuickCheck());

        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<PluginException>(() => database.RunWriteAsync((_, _) => 0, commit: true, SnapshotOperations.Purge, Ct));
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Status);
        Assert.True(File.Exists(database.DamageMarkerPath));
        database.Dispose();

        using var restarted = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        Assert.Null(restarted.LastReindex);
        var recovery = Assert.IsType<SqliteRecoveryResult>(restarted.LastRecovery);
        Assert.NotNull(recovery.RestoredFrom);
        Assert.Contains("non-unique entry in index IX_Children_Parent", recovery.Reason, StringComparison.Ordinal);
        Assert.False(File.Exists(restarted.DamageMarkerPath));
        Assert.Equal("ok", IntegrityCheck());

        // The snapshot's 50: the parent written after it is lost, as with any restore.
        Assert.Equal("50", Count("Parents"));
    }

    // The check after REINDEX had no test (with it removed every kit test passed). Here the integrity_check the kit
    // reads (its first 20 lines) lists only index lines, so the damage looks index-only, but a NULL in a NOT NULL
    // column sits in the table's last row, past those 20: REINDEX runs and repairs the index, the check after it still
    // fails, and the open restores the newest snapshot instead of opening the database as repaired.
    [Fact]
    public async Task IndexLinesFirst_ButTableDamagePastThem_TheCheckAfterReindexFails_AndTheSnapshotIsRestored()
    {
        var database = SeededWithASnapshot();
        const string Table = "CREATE TABLE Children (Id INTEGER PRIMARY KEY, ParentId INTEGER NOT NULL REFERENCES Parents(Id) ON DELETE CASCADE, Note TEXT NOT NULL)";
        Damage("UPDATE sqlite_master SET sql='" + Table.Replace("Note TEXT NOT NULL", "Note TEXT NULL", StringComparison.Ordinal) + "' WHERE name='Children';");
        Write("UPDATE Children SET Note = NULL WHERE Id = (SELECT MAX(Id) FROM Children);");
        Damage("UPDATE sqlite_master SET sql='" + Table + "' WHERE name='Children'; UPDATE sqlite_master SET sql='CREATE INDEX IX_Children_Parent ON Children(Note)' WHERE name='IX_Children_Parent';");
        var full = IntegrityLines(1000);
        Assert.Contains("NULL value in Children.Note", full[^2], StringComparison.Ordinal);
        Assert.True(full.Count > 20, "more lines than the kit reads");
        Assert.All(IntegrityLines(20), line => Assert.Contains(" missing from index ", line, StringComparison.Ordinal));

        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<PluginException>(() => database.RunWriteAsync((_, _) => 0, commit: true, SnapshotOperations.Purge, Ct));
        Assert.Contains("rebuild the indexes", database.StatusMessage, StringComparison.Ordinal);
        database.Dispose();

        using var restarted = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        Assert.Null(restarted.LastReindex);
        var recovery = Assert.IsType<SqliteRecoveryResult>(restarted.LastRecovery);
        Assert.Contains("integrity_check after REINDEX: ", recovery.Reason, StringComparison.Ordinal);
        Assert.Contains("NULL value in Children.Note", recovery.Reason, StringComparison.Ordinal);
        Assert.Equal("ok", IntegrityCheck());
        Assert.Equal("50", Count("Parents"));
    }

    [Fact]
    public void AMarkerBesideAGoodDatabase_IsCheckedAtOpen_AndGoes_NothingMoved()
    {
        var database = SeededWithASnapshot();
        database.Dispose();
        File.WriteAllText(database.DamageMarkerPath, "{\"Reason\":\"left by hand\"}");

        using var reopened = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.Ok, reopened.Open());
        Assert.Null(reopened.LastRecovery);
        Assert.Null(reopened.LastReindex);
        Assert.False(File.Exists(reopened.DamageMarkerPath));
        Assert.Empty(reopened.ListQuarantines());
        Assert.Equal("51", Count("Parents"));
    }

    [Fact]
    public void OnlyIndexLines_AreIndexOnlyDamage()
    {
        static SqliteCheckResult Check(params string[] lines) => new(SqliteCheckResult.IntegrityCheck, DateTime.UtcNow, false, lines);
        Assert.True(SqliteDatabase.IsIndexesOnly(Check("row 1 missing from index IX_A", "wrong # of entries in index IX_A")));
        Assert.True(SqliteDatabase.IsIndexesOnly(Check("non-unique entry in index IX_B")));
        Assert.False(SqliteDatabase.IsIndexesOnly(Check("row 1 missing from index IX_A", "Tree 4 page 4 cell 0: invalid page number 999")));
        Assert.False(SqliteDatabase.IsIndexesOnly(Check("NULL value in Children.Note")));
        Assert.False(SqliteDatabase.IsIndexesOnly(new SqliteCheckResult(SqliteCheckResult.IntegrityCheck, DateTime.UtcNow, true, ["ok"])));
    }

    // 50 families, a daily snapshot of them, then one more parent the snapshot hasn't; checkpointed and released.
    private SqliteDatabase SeededWithASnapshot()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 50);
        Assert.NotNull(database.SnapshotAsync(7, Ct).GetAwaiter().GetResult());
        SqliteFixture.AddFamilies(database, 1, from: 51);
        Assert.True(database.CheckpointAsync(Ct).GetAwaiter().GetResult());
        database.Release("test");
        return database;
    }

    private void Damage(string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _fx.File, Pooling = false }.ToString());
        c.Open();
        SqliteFixture.Execute(c, null, "PRAGMA writable_schema=ON;");
        SqliteFixture.Execute(c, null, sql);
        SqliteFixture.Execute(c, null, "PRAGMA writable_schema=OFF;");
    }

    private void Write(string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _fx.File, Pooling = false }.ToString());
        c.Open();
        SqliteFixture.Execute(c, null, sql);
    }

    private List<string> IntegrityLines(int max)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _fx.File, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        c.Open();
        using var command = c.CreateCommand();
        command.CommandText = "PRAGMA integrity_check(" + max.ToString(System.Globalization.CultureInfo.InvariantCulture) + ");";
        using var reader = command.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
        {
            lines.Add(reader.GetString(0));
        }

        return lines;
    }

    private string QuickCheck() => Query("PRAGMA quick_check;");

    private string IntegrityCheck() => Query("PRAGMA integrity_check;");

    private string Count(string table) => Query("SELECT COUNT(*) FROM " + table + ";");

    private string Query(string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _fx.File, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        c.Open();
        return SqliteFixture.Text(c, sql);
    }
}
