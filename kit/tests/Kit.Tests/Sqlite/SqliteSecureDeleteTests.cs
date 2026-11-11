using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Sqlite;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Sqlite;

/// <summary>
/// What a write deletes doesn't stay in the file: every writable connection runs <c>PRAGMA secure_delete=ON</c>
/// (<see cref="SqliteDatabase.SecureDelete"/>, on by default), so a deleted row's bytes are overwritten with zeros, in
/// the database, in its <c>-wal</c> once checkpointed, and in a snapshot taken afterwards (<c>BackupDatabase</c> copies
/// pages as they are). A database that opts out keeps SQLite's default, which leaves them in the page's free space.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteSecureDeleteTests : IDisposable
{
    private const string Marker = "Kit secure-delete marker: the orchard was empty at noon.";

    private readonly SqliteFixture _fx = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void EveryWritableConnection_HasSecureDeleteOn_UnlessTheDatabaseOptsOut()
    {
        var database = _fx.Open();
        Assert.True(database.SecureDelete);
        Assert.Equal(1L, database.Read(c => SqliteFixture.Scalar(c, "PRAGMA secure_delete;")));
        Assert.Contains("PRAGMA secure_delete=ON;", SqliteDatabase.OpenPragmas, StringComparison.Ordinal);

        var optedOut = new SqliteDatabase(Path.Combine(_fx.Folder, "other.db"), SqliteFixture.V2, _fx.Logger, Path.Combine(_fx.Folder, "other-snapshots"), _fx.Clock) { SecureDelete = false };
        try
        {
            Assert.Equal(0L, optedOut.Read(c => SqliteFixture.Scalar(c, "PRAGMA secure_delete;")));
        }
        finally
        {
            optedOut.Dispose();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADeletedRow_LeavesNoByteInTheFile_OrInTheSnapshotTakenAfter_WithSecureDelete(bool secureDelete)
    {
        var database = new SqliteDatabase(_fx.File, SqliteFixture.V2, _fx.Logger, _fx.Snapshots, _fx.Clock) { SecureDelete = secureDelete };
        try
        {
            SqliteFixture.AddRecent(database, 20);
            await database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Recent (Payload) VALUES ('" + Marker + "');"); return 0; }, Ct);
            await database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "DELETE FROM Recent WHERE Payload LIKE 'Kit secure-delete marker%';"); return 0; }, Ct);
            Checkpoint(_fx.File);
            var snapshot = await database.SnapshotAsync(7, Ct);
            Assert.NotNull(snapshot);

            var holding = FilesHolding(_fx.File, _fx.File + "-wal", snapshot!);
            if (secureDelete)
            {
                Assert.Empty(holding);
            }
            else
            {
                // SQLite's default: the deleted cell stays in the page's free space, and the snapshot copies it.
                Assert.Contains(Path.GetFileName(_fx.File), holding);
                Assert.Contains(Path.GetFileName(snapshot!), holding);
            }
        }
        finally
        {
            database.Dispose();
        }
    }

    [Fact]
    public async Task CheckpointAsync_CutsTheWal_SoNoEarlierVersionOfADeletedRowsPageStaysInIt()
    {
        var database = _fx.Open();
        SqliteFixture.AddRecent(database, 20);
        await database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Recent (Payload) VALUES ('" + Marker + "');"); return 0; }, Ct);
        await database.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "DELETE FROM Recent WHERE Payload LIKE 'Kit secure-delete marker%';"); return 0; }, Ct);

        Assert.True(await database.CheckpointAsync(Ct));

        Assert.Equal(0L, new FileInfo(_fx.File + "-wal").Length);
        Assert.Empty(FilesHolding(_fx.File, _fx.File + "-wal"));
        Assert.Equal(20L, SqliteFixture.Count(database, "Recent"));
    }

    [Fact]
    public async Task CheckpointAsync_DoesNothing_WhileTheDatabaseIsReadOnly()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 1);
        database.Dispose();
        var newer = _fx.Open(SqliteFixture.V1);
        Assert.True(newer.IsReadOnly);

        Assert.False(await newer.CheckpointAsync(Ct));
    }

    [Fact]
    public async Task AVacuumAfterStep_CleansWhatDeletesBeforeSecureDeleteLeftInTheFile_AfterAPreOpSnapshot()
    {
        // A file written before secure_delete was on: the deleted row's bytes stay in its free space.
        var old = new SqliteDatabase(_fx.File, SqliteFixture.V2, _fx.Logger, _fx.Snapshots, _fx.Clock) { SecureDelete = false };
        SqliteFixture.AddRecent(old, 20);
        await old.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Recent (Payload) VALUES ('" + Marker + "');"); return 0; }, Ct);
        await old.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "DELETE FROM Recent WHERE Payload LIKE 'Kit secure-delete marker%';"); return 0; }, Ct);
        old.Dispose();
        Assert.Contains(Path.GetFileName(_fx.File), FilesHolding(_fx.File));

        IReadOnlyList<SqliteMigration> v3 = [.. SqliteFixture.V2, new SqliteMigration(3, "vacuum", "SELECT 1;") { VacuumAfter = true }];
        var database = _fx.Open(v3);

        Assert.Equal(SqliteDatabaseStatus.Migrated, database.Status);
        Assert.Equal(2, database.MigratedFrom);
        Assert.Empty(FilesHolding(_fx.File, _fx.File + "-wal"));
        Assert.Equal(20L, SqliteFixture.Count(database, "Recent"));
        Assert.Contains(_fx.Logger.Entries, e => e.Message.Contains("Vacuumed", StringComparison.Ordinal));

        // The snapshot before the upgrade is the old file as it was (its class keeps it as long as it keeps any).
        var preOp = Assert.Single(database.ListSnapshots(), s => s.Kind == SnapshotKind.PreOp);
        Assert.Equal(SnapshotOperations.Migrate, preOp.Operation);
    }

    [Fact]
    public async Task AVacuumAfterStepsVacuumThatFails_IsAWarning_AndIsTriedAgainAtEachLaterOpen_UntilItSucceeds()
    {
        // A VACUUM that failed (a full disk) used to leave the old bytes in the file's free space for good.
        var old = new SqliteDatabase(_fx.File, SqliteFixture.V2, _fx.Logger, _fx.Snapshots, _fx.Clock) { SecureDelete = false };
        SqliteFixture.AddRecent(old, 20);
        await old.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Recent (Payload) VALUES ('" + Marker + "');"); return 0; }, Ct);
        await old.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "DELETE FROM Recent WHERE Payload LIKE 'Kit secure-delete marker%';"); return 0; }, Ct);
        old.Dispose();
        IReadOnlyList<SqliteMigration> v3 = [.. SqliteFixture.V2, new SqliteMigration(3, "vacuum", "SELECT 1;") { VacuumAfter = true }];
        static void Full() => throw new SqliteException("Synthetic: database or disk is full", 13);

        using (var failing = new SqliteDatabase(_fx.File, v3, _fx.Logger, _fx.Snapshots, _fx.Clock) { DamageRecheckDelay = TimeSpan.Zero, VacuumFault = Full })
        {
            Assert.Equal((SqliteDatabaseStatus.Migrated, 3), (failing.Status, failing.SchemaVersion));
            Assert.True(File.Exists(failing.VacuumPendingPath));
            Assert.Contains(_fx.Logger.At(LogLevel.Warning), e => e.Message.Contains("its VACUUM failed", StringComparison.Ordinal) && e.Message.Contains("tried again at the next start", StringComparison.Ordinal));
            Assert.Equal(20L, SqliteFixture.Count(failing, "Recent"));
        }

        Assert.NotEmpty(FilesHolding(_fx.File, _fx.File + "-wal"));

        // The next open tries again; failing again, it stays pending and the database opens as it is.
        using (var again = new SqliteDatabase(_fx.File, v3, _fx.Logger, _fx.Snapshots, _fx.Clock) { DamageRecheckDelay = TimeSpan.Zero, VacuumFault = Full })
        {
            Assert.Equal(SqliteDatabaseStatus.Ok, again.Status);
            Assert.True(File.Exists(again.VacuumPendingPath));
            Assert.Contains(_fx.Logger.At(LogLevel.Warning), e => e.Message.Contains("failed again", StringComparison.Ordinal));
        }

        // Then it succeeds: the marker goes, and so do the old bytes.
        var database = _fx.Open(v3);
        Assert.Equal(SqliteDatabaseStatus.Ok, database.Status);
        Assert.False(File.Exists(database.VacuumPendingPath));
        Assert.Empty(FilesHolding(_fx.File, _fx.File + "-wal"));
        Assert.Contains(_fx.Logger.Entries, e => e.Message.Contains("which failed before, is done now", StringComparison.Ordinal));
        Assert.Equal(20L, SqliteFixture.Count(database, "Recent"));

        // A database whose VACUUM succeeded at its upgrade leaves no marker.
        Assert.DoesNotContain(Directory.GetFiles(_fx.Folder), f => f.EndsWith(".vacuum-pending", StringComparison.Ordinal));
    }

    private static void Checkpoint(string file)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        connection.Open();
        SqliteFixture.Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    private static List<string> FilesHolding(params string[] files)
    {
        var needle = Encoding.UTF8.GetBytes("Kit secure-delete marker");
        var hits = new List<string>();
        foreach (var file in files.Where(File.Exists))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            if (bytes.AsSpan().IndexOf(needle) >= 0)
            {
                hits.Add(Path.GetFileName(file));
            }
        }

        return hits;
    }
}
