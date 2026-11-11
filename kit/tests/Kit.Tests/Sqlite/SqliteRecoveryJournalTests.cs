using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Sqlite;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Sqlite;

/// <summary>
/// The SQLite storage hardening: a failed put back remembered only in memory (the recovery journal
/// <c>&lt;db&gt;.recovery</c> and its put back at the next open, never a new empty database or a snapshot restored over
/// the data), the order of the moves and the put back's retries, the kept snapshots recorded before the copy goes in,
/// the leftovers of a stop, the kept snapshots' Warning and a marker whose snapshots are all gone, the in-place check's
/// copies, the words of a move aside that failed. A server stopped (killed) at a step is the database folder copied as
/// it is at that moment (<see cref="SqliteFixture.CopyTree"/>), then the step stopped with <see cref="Stopped"/>, which
/// no recovery code catches, and a new database opened on the copy.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteRecoveryJournalTests : IDisposable
{
    private readonly SqliteFixture _fx = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Journal => _fx.File + ".recovery";

    private string Marker => _fx.File + ".keep-snapshots";

    private string Image => _fx.Directory.Combine("image");

    private string ImageFile => Path.Combine(Image, "userratings.db");

    public void Dispose() => _fx.Dispose();

    // -------------------------------------------------------------------- a put back that can't finish, at the next start

    [Fact]
    public async Task AStartEmptyWhoseDatabaseCantGoBack_IsPutBackByTheNextStart_NeverStartedEmpty()
    {
        // No snapshot, an admin's Start empty, the new database can't be put in place and the damaged one can't go back
        // (a folder in its place). The answer names the quarantine folder. Before the journal, a restart saw no
        // database and no snapshot and created a new one: Ok, writable, no rows, the data in the quarantine folder.
        var database = _fx.Open(fault: step =>
        {
            if (step == "install")
            {
                Directory.CreateDirectory(_fx.File);
            }
        });
        await DamagedAtRunTimeAsync(database, snapshot: false);
        var damaged = SqliteFixture.ReadOpenFile(_fx.File);
        var wal = SqliteFixture.ReadOpenFile(_fx.File + "-wal");

        var failed = await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(null, startEmpty: true, Ct));
        var folder = Assert.Single(database.ListQuarantines());
        Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.RecoveryNotPutBack("The new empty database couldn't be created", folder.Name)), (failed.Kind, failed.Message));
        Assert.True(File.Exists(Journal), "The journal of the put back that couldn't finish is kept.");
        database.Dispose();

        // A restart with the folder still in the database's place: read-only, the banner names the quarantine folder, and
        // nothing is created.
        var blocked = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, blocked.Open());
        Assert.StartsWith("The data failed its integrity check (quick_check: ", blocked.StatusMessage, StringComparison.Ordinal);
        Assert.EndsWith(SqliteMessages.RecoveryNotPutBack("The new empty database couldn't be created", folder.Name), blocked.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(SqliteMessages.Missing, Assert.Throws<PluginException>(() => SqliteFixture.Count(blocked, "Parents")).Message);
        Assert.Equal(SqliteMessages.Missing, (await Assert.ThrowsAsync<PluginException>(() => blocked.WriteAsync((_, _) => 0, Ct))).Message);
        Assert.Equal(["reason.txt", "userratings.db", "userratings.db-shm", "userratings.db-wal"], Assert.Single(blocked.ListQuarantines()).Files);
        Assert.True(Directory.Exists(_fx.File));
        Assert.True(File.Exists(Journal));
        Assert.Contains(_fx.Logger.At(LogLevel.Error), e => e.Message.Contains("didn't finish, and what it moved into", StringComparison.Ordinal));
        blocked.Dispose();

        // The folder gone, the next start puts the database and its -wal back as they were, then finds it damaged, as before:
        // read-only, its undamaged tables readable, and only an admin starts it empty.
        Directory.Delete(_fx.File);
        var restarted = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, restarted.Open());
        Assert.Contains("start empty from the plugin's settings page", restarted.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(restarted.ListQuarantines());
        Assert.False(File.Exists(Journal));
        Assert.Equal(damaged, SqliteFixture.ReadOpenFile(_fx.File));
        Assert.Equal(wal, SqliteFixture.ReadOpenFile(_fx.File + "-wal"));
        Assert.Equal(5, SqliteFixture.Count(restarted, "Parents"));

        var recovery = await restarted.RecoverAsync(null, startEmpty: true, Ct);
        Assert.Equal(SqliteDatabaseStatus.RecoveredEmpty, restarted.Status);
        Assert.Equal(["userratings.db", "userratings.db-shm", "userratings.db-wal"], recovery.QuarantinedFiles.Order(StringComparer.Ordinal));
        Assert.Equal(0, SqliteFixture.Count(restarted, "Parents"));
        Assert.False(File.Exists(Journal));
    }

    [Fact]
    public async Task ARestoreWhoseDatabaseCantGoBack_IsPutBackByTheNextStart_ThenRestoredWithTheRightBanner()
    {
        // The same with a snapshot. Before the journal, the restart restored the snapshot as if the file had
        // been missing ("the database file was missing", no folder named) while the damaged one sat in its folder.
        var database = _fx.Open(fault: step =>
        {
            if (step == "install")
            {
                Directory.CreateDirectory(_fx.File);
            }
        });
        var snapshot = await DamagedAtRunTimeAsync(database, snapshot: true);
        var wal = SqliteFixture.ReadOpenFile(_fx.File + "-wal");

        var failed = await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(null, Ct));
        var stuck = Assert.Single(database.ListQuarantines());
        Assert.Equal(SqliteMessages.RecoveryNotPutBack("The snapshot " + snapshot + " couldn't be put in place of the database", stuck.Name), failed.Message);
        database.Dispose();

        // Still in the way: read-only, no snapshot restored over it without an admin.
        var blocked = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, blocked.Open());
        Assert.EndsWith(SqliteMessages.RecoveryNotPutBack("The snapshot " + snapshot + " couldn't be put in place of the database", stuck.Name), blocked.StatusMessage, StringComparison.Ordinal);
        Assert.Null(blocked.LastRecovery);
        Assert.Equal([stuck.Name], blocked.ListQuarantines().Select(q => q.Name));
        blocked.Dispose();

        // Out of the way: put back, then the damaged database's own restore, whose banner names its new folder.
        Directory.Delete(_fx.File);
        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var restarted = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        var moved = Assert.Single(restarted.ListQuarantines());
        Assert.NotEqual(stuck.Name, moved.Name);
        Assert.StartsWith("The data was damaged and was moved to " + moved.Name + "; it was restored from the snapshot " + snapshot, restarted.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(["reason.txt", "userratings.db", "userratings.db-shm", "userratings.db-wal"], moved.Files);
        Assert.Equal(wal, await File.ReadAllBytesAsync(Path.Combine(_fx.Folder, moved.Name, "userratings.db-wal"), Ct));
        Assert.Equal(5, SqliteFixture.Count(restarted, "Parents"));
        Assert.False(File.Exists(Journal));
        Assert.True(File.Exists(Marker));
    }

    [Theory]
    [InlineData("journal", false)]
    [InlineData("moved:userratings.db", false)]
    [InlineData("moved:userratings.db-wal", false)]
    [InlineData("marker", false)]
    [InlineData("install", false)]
    [InlineData("installed", false)]
    [InlineData("open", false)]
    [InlineData("opened", false)]
    [InlineData("moved:userratings.db", true)]
    [InlineData("installed", true)]
    [InlineData("opened", true)]
    public async Task AServerStoppedAtAnyStepOfARecovery_IsPutBackAtTheNextStart_AndTheRecoveryRunsAgain(string step, bool startEmpty)
    {
        // An admin's recovery (or start empty) of a database damaged at run time with a hot WAL; the server stops at the
        // step. The next start finds the journal, puts back what had been moved (deleting what took the database's place
        // only when it is that file, byte for byte), and the open's own path runs as for any damaged database: the newest
        // good snapshot restored, one quarantine folder, the database with the -wal it was found with. (A start empty that
        // didn't finish is not done again: an admin's choice is the admin's, and the open never starts empty on its own.)
        var database = _fx.Open(fault: StopAt(step));
        var snapshot = await DamagedAtRunTimeAsync(database, snapshot: true);
        var wal = SqliteFixture.ReadOpenFile(_fx.File + "-wal");

        await Assert.ThrowsAsync<Stopped>(() => database.RecoverAsync(null, startEmpty, Ct));

        // As the server left it: a quarantine folder has its note from the start; a database at its path is never
        // there without the -wal it was found with; once anything takes the database's place, the snapshots are kept.
        Assert.True(File.Exists(ImageFile + ".recovery"));
        foreach (var quarantine in Directory.GetDirectories(Image, "quarantine-*"))
        {
            Assert.True(File.Exists(Path.Combine(quarantine, "reason.txt")), step + ": a quarantine folder without its note");
        }

        if (File.Exists(ImageFile) && !Placed(ImageFile))
        {
            Assert.Equal(wal, await File.ReadAllBytesAsync(ImageFile + "-wal", Ct));
        }

        if (step is "marker" or "install" or "installed" or "open" or "opened")
        {
            Assert.True(File.Exists(ImageFile + ".keep-snapshots"), step + ": the copy was put in place before the kept snapshots were recorded");
        }

        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var restarted = _fx.Open(file: ImageFile);
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        var moved = Assert.Single(restarted.ListQuarantines());
        Assert.StartsWith("The data was damaged and was moved to " + moved.Name + "; it was restored from the snapshot " + snapshot, restarted.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(["reason.txt", "userratings.db", "userratings.db-shm", "userratings.db-wal"], moved.Files);
        Assert.Equal(wal, await File.ReadAllBytesAsync(Path.Combine(Image, moved.Name, "userratings.db-wal"), Ct));
        Assert.Equal(5, SqliteFixture.Count(restarted, "Parents"));
        Assert.Equal(0, SqliteFixture.Count(restarted, "Recent"));
        Assert.False(File.Exists(ImageFile + ".recovery"));
        Assert.False(File.Exists(ImageFile + ".restore.tmp"));
        Assert.True(File.Exists(ImageFile + ".keep-snapshots"));
        Assert.Single(_fx.Logger.At(LogLevel.Warning), e => e.Message.Contains("didn't finish; what it had moved aside", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AServerStoppedDuringAPutBack_LeavesTheDatabaseInItsFolder_AndTheNextStartPutsTheRestBack()
    {
        // The put back moves the sidecars first and the database last, so at every moment a database at its path has its
        // -wal beside it. Stopped after the first file went back (the -wal): the database is still in the folder.
        var stopped = false;
        var database = _fx.Open(fault: step =>
        {
            if (step == "open")
            {
                throw new IOException("Simulated: the copy can't be opened.");
            }

            if (!stopped && step.StartsWith("putback:", StringComparison.Ordinal))
            {
                stopped = true;
                SqliteFixture.CopyTree(_fx.Folder, Image);
                throw new Stopped();
            }
        });
        var snapshot = await DamagedAtRunTimeAsync(database, snapshot: true);
        var wal = SqliteFixture.ReadOpenFile(_fx.File + "-wal");

        await Assert.ThrowsAsync<Stopped>(() => database.RecoverAsync(null, Ct));

        Assert.False(File.Exists(ImageFile), "The database went back before its -wal.");
        Assert.Equal(wal, await File.ReadAllBytesAsync(ImageFile + "-wal", Ct));
        var folder = Assert.Single(Directory.GetDirectories(Image, "quarantine-*"));
        Assert.True(File.Exists(Path.Combine(folder, "userratings.db")));

        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var restarted = _fx.Open(file: ImageFile);
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        var moved = Assert.Single(restarted.ListQuarantines());
        Assert.StartsWith("The data was damaged and was moved to " + moved.Name + "; it was restored from the snapshot " + snapshot, restarted.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(wal, await File.ReadAllBytesAsync(Path.Combine(Image, moved.Name, "userratings.db-wal"), Ct));
        Assert.False(File.Exists(ImageFile + ".recovery"));
    }

    [Fact]
    public async Task APutBackMoveThatFailsOnce_IsTriedAgain_AndEverythingGoesBack()
    {
        // A file held a moment (a scanner) is no reason to leave the database's files apart: three tries, as the rename.
        var held = 0;
        var database = _fx.Open(fault: step =>
        {
            if (step == "open")
            {
                throw new IOException("Simulated: the copy can't be opened.");
            }

            if (step == "moveback:userratings.db" && held++ == 0)
            {
                throw new IOException("Simulated: the database is held a moment.");
            }
        });
        var snapshot = await DamagedAtRunTimeAsync(database, snapshot: true);
        var before = Listing(_fx.Folder);

        var failed = await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(null, Ct));

        Assert.Equal(SqliteMessages.RecoveryPutBack("The snapshot " + snapshot + " couldn't be opened in place of the database", movedAny: true), failed.Message);
        Assert.Equal(2, held);
        Assert.Equal(before, Listing(_fx.Folder));
        Assert.Empty(database.ListQuarantines());
        Assert.False(File.Exists(Journal));
    }

    [Fact]
    public async Task ALargeUpgradeOfTheRestoredCopy_HoldsItsCheckpoints_SoAStopThenStillFindsTheCopyItPutThere()
    {
        // The next start deletes what took the database's place only when it is that file, byte for byte. A schema 1
        // snapshot restored into this build is upgraded by the open after the rename; an upgrade that writes more than 1000
        // pages would checkpoint into the copy on its own and change it, and the next start couldn't tell it from someone
        // else's file (read-only, the data stuck in the folder). The recovery's upgrade holds its checkpoints instead.
        IReadOnlyList<SqliteMigration> v2 = [.. SqliteFixture.V1, new SqliteMigration(2, "touch every Bulk page", SqliteFixture.V2Sql + " UPDATE Bulk SET Payload = Payload || '!';")];
        var v1 = _fx.Open(SqliteFixture.V1);
        SqliteFixture.AddFamilies(v1, 5);
        SqliteFixture.AddBulk(v1, 40000);
        var old = Path.GetFileName((await v1.SnapshotAsync(7, Ct))!);
        v1.Dispose();
        var database = _fx.Open(v2, fault: StopAt("opened"));
        Assert.Equal(SqliteDatabaseStatus.Migrated, database.Open());
        var root = SqliteFixture.RootPage(database, "Parents");
        database.Release("test");
        SqliteFixture.CorruptPage(_fx.File, root);
        Assert.False(database.QuickCheck()!.Ok);

        await Assert.ThrowsAsync<Stopped>(() => database.RecoverAsync(old, Ct));
        Assert.True(Placed(ImageFile), "The copy the recovery put in place changed before the recovery was done.");

        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var restarted = _fx.Open(v2, file: ImageFile);
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        Assert.StartsWith("The data was damaged and was moved to ", restarted.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(40000, SqliteFixture.Count(restarted, "Bulk"));
        Assert.False(File.Exists(ImageFile + ".recovery"));
    }

    [Fact]
    public async Task AnAdminsRecoveryOverAPutBackThatCantFinish_KeepsItsJournal_AndPutsBothBackInTurn()
    {
        // A recovery started while an earlier one's put back couldn't finish (an admin's choice from that banner) keeps the
        // earlier journal inside its own: put back in turn, the earlier one is the journal again, and the next start puts
        // that one back too. Without it the earlier folder's database would be forgotten, and the restart would restore a
        // snapshot as if the file had been missing.
        var obstacle = true;
        var failOpen = false;
        var database = _fx.Open(fault: step =>
        {
            if (obstacle && step == "install")
            {
                Directory.CreateDirectory(_fx.File);
            }

            if (failOpen && step == "open")
            {
                throw new IOException("Simulated: the copy can't be opened.");
            }
        });
        var snapshot = await DamagedAtRunTimeAsync(database, snapshot: true);
        await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(null, Ct));
        var stuck = Assert.Single(database.ListQuarantines());

        // The folder is cleared while the server runs, and the admin restores the snapshot, which fails in turn.
        obstacle = false;
        failOpen = true;
        Directory.Delete(_fx.File);
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        var again = await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(snapshot, Ct));
        Assert.Equal(SqliteMessages.RecoveryPutBack("The snapshot " + snapshot + " couldn't be opened in place of the database", movedAny: false), again.Message);
        var journal = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllBytesAsync(Journal, Ct)));
        Assert.Equal(stuck.Name, (string)journal["Folder"]!);
        Assert.Equal("stuck", (string)journal["Step"]!);
        database.Dispose();

        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var restarted = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        var moved = Assert.Single(restarted.ListQuarantines());
        Assert.NotEqual(stuck.Name, moved.Name);
        Assert.StartsWith("The data was damaged and was moved to " + moved.Name, restarted.StatusMessage, StringComparison.Ordinal);
        Assert.False(File.Exists(Journal));
    }

    [Fact]
    public async Task AnAdminsRecoveryOverAPutBackThatCantFinish_ThatSucceeds_LeavesTheEarlierFolder_AndSaysWhereItIs()
    {
        var obstacle = true;
        var database = _fx.Open(fault: step =>
        {
            if (obstacle && step == "install")
            {
                Directory.CreateDirectory(_fx.File);
            }
        });
        var snapshot = await DamagedAtRunTimeAsync(database, snapshot: true);
        await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(null, Ct));
        var stuck = Assert.Single(database.ListQuarantines());

        obstacle = false;
        Directory.Delete(_fx.File);
        var recovery = await database.RecoverAsync(snapshot, Ct);

        Assert.Equal(snapshot, recovery.RestoredFrom);
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, database.Status);
        Assert.Contains("what was moved aside is in " + stuck.Name, database.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(["reason.txt", "userratings.db", "userratings.db-shm", "userratings.db-wal"], Assert.Single(database.ListQuarantines()).Files);
        Assert.False(File.Exists(Journal));
        Assert.Equal(5, SqliteFixture.Count(database, "Parents"));
    }

    [Fact]
    public async Task AnAdminsOwnCopyPutInTheDatabasesPlace_WhileARecoverysFilesWaitInTheirFolder_IsOpenedAsItIs_NeverDeleted_AndANoticeNamesTheFolder()
    {
        // The next start deletes only what the recovery put there (its size and SHA-256). A good database an admin
        // copied into the place by hand with the server stopped is opened as it is, as a restart opens the one an
        // admin's recovery refused ("restart the server to use it"), and the folder keeps what the recovery had moved.
        // The notice naming the folder stays (Diagnostics, the settings page's banner), at every start, until the
        // folder is gone; before, a Warning in the log said it once and the journal went.
        var database = _fx.Open(fault: step =>
        {
            if (step == "install")
            {
                Directory.CreateDirectory(_fx.File);
            }
        });
        SqliteFixture.AddFamilies(database, 6, from: 100);
        var backup = _fx.Directory.Combine("backup.db");
        database.Release("test");
        File.Copy(_fx.File, backup);
        await DamagedAtRunTimeAsync(database, snapshot: true);
        await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(null, Ct));
        var stuck = Assert.Single(database.ListQuarantines());
        database.Dispose();

        Directory.Delete(_fx.File);
        File.Copy(backup, _fx.File);
        var restarted = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.Ok, restarted.Open());
        Assert.Equal(6, SqliteFixture.Count(restarted, "Parents"));
        Assert.Equal(["reason.txt", "userratings.db", "userratings.db-shm", "userratings.db-wal"], Assert.Single(restarted.ListQuarantines(), q => q.Name == stuck.Name).Files);
        Assert.Contains(_fx.Logger.At(LogLevel.Warning), e => e.Message.Contains("is opened as it is", StringComparison.Ordinal));
        Assert.Null(restarted.StatusMessage);
        Assert.Equal(SqliteMessages.KeptAside(stuck.Name), restarted.Notice);
        Assert.Equal(SqliteMessages.KeptAside(stuck.Name), restarted.Describe()["Notice"]);
        Assert.True(File.Exists(Journal), "The journal stays as the record of what is in the folder.");
        await restarted.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Parents (Id, Name) VALUES (500, 'written after');"); return 0; }, Ct);
        restarted.Dispose();

        // Every start says it again, and never touches the database in place or the folder.
        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var again = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.Ok, again.Open());
        Assert.Equal(SqliteMessages.KeptAside(stuck.Name), again.Notice);
        Assert.Equal(7, SqliteFixture.Count(again, "Parents"));
        Assert.Equal(["reason.txt", "userratings.db", "userratings.db-shm", "userratings.db-wal"], Assert.Single(again.ListQuarantines(), q => q.Name == stuck.Name).Files);

        // The admin deletes the folder: the notice goes at once, and the next start deletes the journal.
        Directory.Delete(Path.Combine(_fx.Folder, stuck.Name), recursive: true);
        Assert.Null(again.Notice);
        again.Dispose();
        var after = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.Ok, after.Open());
        Assert.Null(after.Notice);
        Assert.Null(after.Describe()["Notice"]);
        Assert.False(File.Exists(Journal));
        Assert.Equal(7, SqliteFixture.Count(after, "Parents"));
    }

    [Fact]
    public async Task KeptAside_ThenTheAdminDeletesOnlyTheDatabaseFromTheFolder_ItsLogFilesStayThere_NeverReplayedIntoTheAdminsDatabase()
    {
        // The journal is kept while an admin's own copy is used; the admin then deletes only userratings.db from the
        // quarantine folder and leaves its -wal and -shm. The next start found the database neither in the folder nor
        // its own in the place, and moved the damaged database's -wal next to the admin's, which replayed it: Recent
        // went from 0 rows to 7 and quick_check still passed (silent corruption). Now log files without their database
        // never go back; the Notice says to delete or move the whole folder.
        var database = _fx.Open(fault: step =>
        {
            if (step == "install")
            {
                Directory.CreateDirectory(_fx.File);
            }
        });
        SqliteFixture.AddFamilies(database, 6, from: 100);
        var backup = _fx.Directory.Combine("backup.db");
        database.Release("test");
        File.Copy(_fx.File, backup);
        await DamagedAtRunTimeAsync(database, snapshot: true);
        await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(null, Ct));
        var stuck = Assert.Single(database.ListQuarantines());
        database.Dispose();

        Directory.Delete(_fx.File);
        File.Copy(backup, _fx.File);
        var kept = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.Ok, kept.Open());
        Assert.Equal(SqliteMessages.KeptAside(stuck.Name), kept.Notice);
        Assert.Equal(0, SqliteFixture.Count(kept, "Recent"));
        kept.Dispose();

        // The admin takes only the damaged database out of the folder: its log files, a real -wal of seven Recent rows with
        // the same page size as the admin's database, stay.
        var folder = Path.Combine(_fx.Folder, stuck.Name);
        File.Delete(Path.Combine(folder, "userratings.db"));
        var wal = SqliteFixture.ReadOpenFile(Path.Combine(folder, "userratings.db-wal"));
        Assert.True(wal.Length > 0, "The damaged database's -wal holds frames.");

        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var restarted = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.Ok, restarted.Open());
        Assert.Equal(0, SqliteFixture.Count(restarted, "Recent"));
        Assert.Equal(6, SqliteFixture.Count(restarted, "Parents"));
        Assert.True(restarted.QuickCheck()!.Ok);
        Assert.Equal(["reason.txt", "userratings.db-shm", "userratings.db-wal"], SqliteFixture.Names(folder));
        Assert.Equal(wal, await File.ReadAllBytesAsync(Path.Combine(folder, "userratings.db-wal"), Ct));
        Assert.Equal(SqliteMessages.KeptAsideLogFiles(stuck.Name), restarted.Notice);
        Assert.Equal(SqliteMessages.KeptAsideLogFiles(stuck.Name), restarted.Describe()["Notice"]);
        Assert.Contains("Delete or move the whole folder.", restarted.Notice, StringComparison.Ordinal);
        Assert.Contains(_fx.Logger.At(LogLevel.Warning), e => e.Message.Contains("without the database itself", StringComparison.Ordinal));
        Assert.True(File.Exists(Journal), "The journal stays as the record of what is in the folder.");
        await restarted.WriteAsync((c, t) => { SqliteFixture.Execute(c, t, "INSERT INTO Parents (Id, Name) VALUES (501, 'written after');"); return 0; }, Ct);
        restarted.Dispose();

        // Every start says it again and never touches the folder.
        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var again = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.Ok, again.Open());
        Assert.Equal(0, SqliteFixture.Count(again, "Recent"));
        Assert.Equal(7, SqliteFixture.Count(again, "Parents"));
        Assert.Equal(wal, await File.ReadAllBytesAsync(Path.Combine(folder, "userratings.db-wal"), Ct));
        Assert.Equal(SqliteMessages.KeptAsideLogFiles(stuck.Name), again.Notice);

        // The whole folder deleted: the notice goes, and the next start deletes the journal.
        Directory.Delete(folder, recursive: true);
        Assert.Null(again.Notice);
        again.Dispose();
        var after = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.Ok, after.Open());
        Assert.Null(after.Notice);
        Assert.False(File.Exists(Journal));
        Assert.Equal(7, SqliteFixture.Count(after, "Parents"));
    }

    [Fact]
    public async Task APutBackThatCantFinish_ThenTheAdminDeletesOnlyTheDatabaseFromTheFolder_NeverPutsItsLogFilesBesideTheRecoverysCopy()
    {
        // The other half: what the recovery put in the place is still there (its delete was refused) when the admin takes
        // only the damaged database out of the folder. Before, the next start kept the copy and moved the damaged -wal next
        // to it. Now the copy goes as usual, the log files stay in their folder, and the open goes on as for a missing
        // database: the newest snapshot restored, its rows only.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Holding a file so that it can't be deleted is Windows' share mode.");
        FileStream? heldJournal = null;
        FileStream? heldDatabase = null;
        var database = _fx.Open(fault: step =>
        {
            if (step == "opened")
            {
                heldJournal = new FileStream(_fx.File + ".recovery", FileMode.Open, FileAccess.Read, FileShare.Read);
                heldDatabase = new FileStream(_fx.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
        });
        await DamagedAtRunTimeAsync(database, snapshot: true);
        try
        {
            await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(null, startEmpty: true, Ct));
        }
        finally
        {
            await (heldJournal?.DisposeAsync() ?? ValueTask.CompletedTask);
            await (heldDatabase?.DisposeAsync() ?? ValueTask.CompletedTask);
        }

        Assert.True(Placed(_fx.File));
        var stuck = Assert.Single(database.ListQuarantines());
        database.Dispose();
        var folder = Path.Combine(_fx.Folder, stuck.Name);
        File.Delete(Path.Combine(folder, "userratings.db"));
        var wal = SqliteFixture.ReadOpenFile(Path.Combine(folder, "userratings.db-wal"));

        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var restarted = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        Assert.Equal(5, SqliteFixture.Count(restarted, "Parents"));
        Assert.Equal(0, SqliteFixture.Count(restarted, "Recent"));
        Assert.True(restarted.QuickCheck()!.Ok);
        Assert.Equal(["reason.txt", "userratings.db-shm", "userratings.db-wal"], SqliteFixture.Names(folder));
        Assert.Equal(wal, await File.ReadAllBytesAsync(Path.Combine(folder, "userratings.db-wal"), Ct));
        Assert.Contains(_fx.Logger.At(LogLevel.Warning), e => e.Message.Contains("without the database itself (taken out of the folder by hand)", StringComparison.Ordinal));
        Assert.False(File.Exists(Journal));
    }

    [Fact]
    public async Task APutBackWithoutTheRecoverysGuard_StillLeavesTheCopyAsRecorded_ClearPoolsKeepingFilesCarriesIt()
    {
        // With the recovery's own guard on what it put in place (the HoldPlacedGuard), ClearPools() in the put back
        // passed every test, since the guard kept the last connection read-only. Without the guard (one that can't be
        // opened: the "guard" step throws, as SQLite would), the put back's ClearPoolsKeepingFiles alone keeps an
        // upgraded copy as the journal recorded it; ClearPools() there wrote the upgrade's -wal into the copy.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Holding a file so that it can't be deleted is Windows' share mode.");
        IReadOnlyList<SqliteMigration> v2 = [.. SqliteFixture.V1, new SqliteMigration(2, "v2", SqliteFixture.V2Sql)];
        var v1 = _fx.Open(SqliteFixture.V1);
        SqliteFixture.AddFamilies(v1, 5);
        var old = Path.GetFileName((await v1.SnapshotAsync(7, Ct))!);
        v1.Dispose();

        FileStream? heldJournal = null;
        FileStream? heldDatabase = null;
        var guardRefused = false;
        var database = _fx.Open(v2, fault: step =>
        {
            if (step == "guard")
            {
                guardRefused = true;
                throw new SqliteException("The guard can't be opened (a test's stand-in).", 14);
            }

            if (step == "opened")
            {
                heldJournal = new FileStream(_fx.File + ".recovery", FileMode.Open, FileAccess.Read, FileShare.Read);
                heldDatabase = new FileStream(_fx.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
        });
        Assert.Equal(SqliteDatabaseStatus.Migrated, database.Open());
        SqliteFixture.AddFamilies(database, 3, from: 100);
        var root = SqliteFixture.RootPage(database, "Parents");
        database.Release("test");
        SqliteFixture.CorruptPage(_fx.File, root);
        Assert.False(database.QuickCheck()!.Ok);
        var damaged = SqliteFixture.ReadOpenFile(_fx.File);

        bool placedAfterPutBack;
        try
        {
            await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(old, Ct));
            placedAfterPutBack = Placed(_fx.File);
        }
        finally
        {
            await (heldJournal?.DisposeAsync() ?? ValueTask.CompletedTask);
            await (heldDatabase?.DisposeAsync() ?? ValueTask.CompletedTask);
        }

        Assert.True(guardRefused, "The recovery ran without its guard.");
        Assert.True(placedAfterPutBack, "The put back changed the copy it couldn't delete, so the next start can't tell it is the recovery's.");
        database.Dispose();

        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var restarted = _fx.Open(v2);
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        var moved = Assert.Single(restarted.ListQuarantines());
        Assert.Equal(damaged, await File.ReadAllBytesAsync(Path.Combine(_fx.Folder, moved.Name, "userratings.db"), Ct));
        Assert.False(File.Exists(Journal));
    }

    [Fact]
    public async Task ARecoveryWhoseJournalCantBeDeletedAtTheEnd_IsntDone_AndPutsEverythingBack()
    {
        // While the journal is there a restart takes the recovery for one that didn't finish and undoes it, after members may
        // have written to the restored data: the recovery is done only once its journal is gone.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Holding a file so that it can't be deleted is Windows' share mode.");
        FileStream? held = null;
        var database = _fx.Open(fault: step =>
        {
            if (step == "opened")
            {
                held = new FileStream(_fx.File + ".recovery", FileMode.Open, FileAccess.Read, FileShare.Read);
            }
        });
        var snapshot = await DamagedAtRunTimeAsync(database, snapshot: true);
        var before = Listing(_fx.Folder);
        try
        {
            var failed = await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(null, Ct));
            Assert.Equal(SqliteMessages.RecoveryPutBack("The recovery couldn't be recorded as finished (its journal couldn't be deleted)", movedAny: true), failed.Message);
        }
        finally
        {
            if (held is not null)
            {
                await held.DisposeAsync();
            }
        }

        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Status);
        Assert.Equal(before, Listing(_fx.Folder).Where(e => !e.StartsWith("userratings.db.recovery ", StringComparison.Ordinal)).ToList());
        Assert.Empty(database.ListQuarantines());
        database.Dispose();

        // The journal it couldn't delete names files that are all back: the next start deletes it and restores as usual.
        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var restarted = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        Assert.Single(restarted.ListQuarantines());
        Assert.Contains(snapshot!, restarted.StatusMessage, StringComparison.Ordinal);
        Assert.False(File.Exists(Journal));
    }

    [Fact]
    public void ADatabasesFiles_AreDeletedSidecarsFirst_SoAnUndeletableSidecarNeverLeavesAWalWithoutItsDatabase()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Holding a file so that it can't be deleted is Windows' share mode.");
        var file = _fx.Directory.Combine("copy.db");
        File.WriteAllText(file, "db");
        File.WriteAllText(file + "-shm", "shm");
        File.WriteAllText(file + "-wal", "wal");
        using (new FileStream(file + "-wal", FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => SqliteFiles.DeleteWithSidecars(file));
        }

        Assert.True(File.Exists(file), "The database went before its -wal.");
        Assert.True(File.Exists(file + "-wal"));
        SqliteFiles.DeleteWithSidecars(file);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "copy.db*"));
    }

    [Fact]
    public async Task AJournalThatCantBeRead_WithNoDatabase_IsReadOnly_UntilAnAdminActs_AndWithOne_IsSetAside()
    {
        Directory.CreateDirectory(_fx.Folder);
        await File.WriteAllTextAsync(Journal, "{ not a journal", Ct);

        var database = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Open());
        Assert.Equal(SqliteMessages.UnreadableRecoveryJournal("userratings.db.recovery"), database.StatusMessage);
        Assert.Equal(SqliteMessages.Missing, Assert.Throws<PluginException>(() => SqliteFixture.Count(database, "Parents")).Message);
        Assert.False(File.Exists(_fx.File), "Nothing created the database.");
        Assert.True(File.Exists(Journal));

        // An admin starts it empty: the unreadable journal is set aside, never overwritten unseen.
        await database.RecoverAsync(null, startEmpty: true, Ct);
        Assert.Equal(SqliteDatabaseStatus.RecoveredEmpty, database.Status);
        Assert.False(File.Exists(Journal));
        var aside = Assert.Single(Directory.GetFiles(_fx.Folder, "userratings.db.recovery.unreadable-*"));
        Assert.Equal("{ not a journal", await File.ReadAllTextAsync(aside, Ct));
        database.Dispose();

        // With a database in place, it is set aside and the database opened as it is.
        await File.WriteAllTextAsync(Journal, "[]", Ct);
        _fx.Clock.Advance(TimeSpan.FromSeconds(2));
        var reopened = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.Ok, reopened.Open());
        Assert.False(File.Exists(Journal));
        Assert.Equal(2, Directory.GetFiles(_fx.Folder, "userratings.db.recovery.unreadable-*").Length);
    }

    // ------------------------------------------------------------------------------------------------ the leftovers

    [Fact]
    public async Task TheLeftoversOfAStop_TheCheckedCopy_TheRechecksCopy_AndTheJournalsTemporaryFile_GoAtTheNextOpen()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 2);
        database.Dispose();
        string[] leftovers = [".restore.tmp", ".restore.tmp-wal", ".recheck.tmp", ".recheck.tmp-wal", ".recheck.tmp-shm", ".recovery.tmp"];
        foreach (var suffix in leftovers)
        {
            await File.WriteAllTextAsync(_fx.File + suffix, "left by a stop", Ct);
        }

        var reopened = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.Ok, reopened.Open());
        Assert.All(leftovers, suffix => Assert.False(File.Exists(_fx.File + suffix), suffix));
        Assert.Equal(2, SqliteFixture.Count(reopened, "Parents"));
    }

    // ----------------------------------------------------------------------------------------- the kept snapshots

    [Fact]
    public async Task WhileSnapshotsAreKept_OneWarningPerProcessNamesTheMarker_AsTheJsonStoreDoes()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 3);
        await database.SnapshotAsync(3, Ct);
        database.Dispose();
        File.Delete(_fx.File);
        var restarted = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());

        for (var i = 0; i < 3; i++)
        {
            _fx.Clock.Advance(TimeSpan.FromDays(1));
            await restarted.SnapshotAsync(3, Ct);
            await restarted.PreOpSnapshotAsync(SnapshotOperations.Import, Ct);
        }

        Assert.Single(KeptWarnings());
        Assert.Contains("are kept until one of them is restored or userratings.db.keep-snapshots is deleted", KeptWarnings()[0].Message, StringComparison.Ordinal);
        restarted.Dispose();

        var reopened = _fx.Open();
        _fx.Clock.Advance(TimeSpan.FromDays(1));
        await reopened.SnapshotAsync(3, Ct);
        await reopened.SnapshotAsync(3, Ct);
        Assert.Equal(2, KeptWarnings().Count);
    }

    [Fact]
    public async Task AMarkerWhoseSnapshotsWereAllDeletedByHand_StaysAsTheJsonStoresDoes_AndKeepsThemIfTheyComeBack()
    {
        // Decided: the marker stays while none of its snapshots is there, as the JSON store's does; it keeps nothing
        // else, so the snapshots taken since rotate as usual, and a kept snapshot that comes back (a backup of
        // snapshots/ put back) is kept again. Before, the marker went at the first rotation without them.
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 3);
        var first = (await database.SnapshotAsync(3, Ct))!;
        database.Dispose();
        var saved = _fx.Directory.Combine("saved-" + Path.GetFileName(first));
        File.Copy(first, saved);
        File.Delete(_fx.File);
        var restarted = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        var before = restarted.KeepSnapshotsBeforeUtc;
        Assert.NotNull(before);

        // An admin deletes the kept snapshot by hand.
        File.Delete(first);
        _fx.Clock.Advance(TimeSpan.FromDays(1));
        await restarted.SnapshotAsync(3, Ct);

        Assert.Equal(before, restarted.KeepSnapshotsBeforeUtc);
        Assert.True(File.Exists(Marker));
        Assert.DoesNotContain(_fx.Logger.At(LogLevel.Information), e => e.Message.Contains("keeps nothing", StringComparison.Ordinal));
        Assert.Single(KeptWarnings());
        for (var i = 0; i < 4; i++)
        {
            _fx.Clock.Advance(TimeSpan.FromDays(1));
            await restarted.SnapshotAsync(3, Ct);
        }

        Assert.Equal(3, restarted.ListSnapshots().Count(s => s.Kind == SnapshotKind.Daily));
        Assert.True(File.Exists(Marker));

        // The kept snapshot comes back: it is kept again, through every rotation.
        File.Copy(saved, first);
        for (var i = 0; i < 4; i++)
        {
            _fx.Clock.Advance(TimeSpan.FromDays(1));
            await restarted.SnapshotAsync(3, Ct);
        }

        Assert.True(File.Exists(first), "The kept snapshot that came back was rotated out.");
        Assert.True(Assert.Single(restarted.ListSnapshots(), s => s.Name == Path.GetFileName(first)).Kept);
        restarted.Dispose();
        var reopened = _fx.Open();
        Assert.Equal(before, reopened.KeepSnapshotsBeforeUtc);
    }

    // ---------------------------------------------------------------------------------------- the in-place check's copies

    [Fact]
    public async Task ACheckpointByAnotherProgramBetweenTheChecksCopies_NeverMakesAGoodFileLookDamaged()
    {
        string? checkpoint = null;
        long walAfterCheckpoint = -1;
        // The -wal is copied first: another program's checkpoint, then a write that grows the same table, between the two
        // copies leaves the copy one consistent state (the frames copied are in the database copied after them too). With
        // the database copied first, the copy was the database from before the checkpoint with the later write's frames:
        // its page 1 and the table's interior page pointing at pages that file never had, so integrity_check failed and the
        // recovery quarantined a good file.
        var database = _fx.Open(fault: step =>
        {
            if (step == "recheck")
            {
                using var other = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _fx.File, Pooling = false }.ToString());
                other.Open();
                using (var command = other.CreateCommand())
                {
                    command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    using var reader = command.ExecuteReader();
                    reader.Read();
                    checkpoint = reader.GetInt64(0) + "," + reader.GetInt64(1) + "," + reader.GetInt64(2);
                }

                walAfterCheckpoint = new FileInfo(_fx.File + "-wal").Length;
                using var transaction = other.BeginTransaction();
                for (var i = 0; i < 300; i++)
                {
                    SqliteFixture.Execute(other, transaction, "INSERT INTO Bulk (Payload) VALUES ('another program');");
                }

                transaction.Commit();
            }
        });
        SqliteFixture.AddFamilies(database, 5);
        SqliteFixture.AddBulk(database);
        await database.SnapshotAsync(3, Ct);
        database.Release("test");
        SqliteFixture.AddBulk(database, 5000);
        var root = SqliteFixture.RootPage(database, "Parents");
        var page = ReadPage(_fx.File, root);
        SqliteFixture.CorruptPage(_fx.File, root);
        Assert.False(database.QuickCheck()!.Ok);
        WritePage(_fx.File, root, page);
        Assert.True(new FileInfo(_fx.File + "-wal").Length > 100 * 4096, "The new Bulk pages are only in the -wal: " + new FileInfo(_fx.File + "-wal").Length);

        var refused = await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(null, Ct));

        Assert.Equal("0,0,0", checkpoint);
        Assert.Equal(0, walAfterCheckpoint);
        Assert.Equal((PluginErrorKind.Conflict, StoreMessages.ReadableFileInPlace("userratings.db")), (refused.Kind, refused.Message));
        Assert.Empty(database.ListQuarantines());
    }

    // ------------------------------------------------------------------------------------------- the move aside's words

    [Fact]
    public async Task AMoveAsideThatFails_WithNoDatabase_SaysWhatWasFoundInItsPlace_NotTheDamagedDatabase()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Holding a file so that it can't be moved is Windows' share mode.");
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 3);
        var root = SqliteFixture.RootPage(database, "Parents");
        var snapshot = (await database.SnapshotAsync(3, Ct))!;
        database.Dispose();
        SqliteFixture.CorruptPage(snapshot, root);
        File.Delete(_fx.File);
        await File.WriteAllBytesAsync(_fx.File, [], Ct);
        var restarted = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, restarted.Open());

        using (new FileStream(_fx.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var failed = await Assert.ThrowsAsync<PluginException>(() => restarted.RecoverAsync(null, startEmpty: true, Ct));
            Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.MoveAsideFailed(damaged: false)), (failed.Kind, failed.Message));
        }

        Assert.Equal("What was found in place of the database couldn't be moved aside, so nothing was changed. The server log has the details.", SqliteMessages.MoveAsideFailed(damaged: false));
        Assert.DoesNotContain(restarted.ListQuarantines(), q => q.Files.Contains("userratings.db"));
        Assert.False(File.Exists(Journal));
    }

    // ------------------------------------------------------------------------------------------------ the put back's pools

    [Fact]
    public async Task APutBackOfAnUpgradedCopyThatCantBeDeleted_LeavesTheCopyAsTheJournalRecordedIt_SoTheNextStartPutsTheDataBack()
    {
        // A schema 1 snapshot restored into a schema 2 build is upgraded in place, in the -wal; the journal can't be
        // deleted at the end and the copy can't be deleted by the put back (both held). PutBack's ClearPools() closed
        // the last connection, which wrote the -wal into the copy, so the next start no longer knew it as the
        // recovery's: it opened it as an admin's own database (Ok, the snapshot's rows, no banner) while the real data
        // sat in the quarantine folder. Now the put back clears the pools keeping the files.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Holding a file so that it can't be deleted is Windows' share mode.");
        IReadOnlyList<SqliteMigration> v2 = [.. SqliteFixture.V1, new SqliteMigration(2, "v2", SqliteFixture.V2Sql)];
        var v1 = _fx.Open(SqliteFixture.V1);
        SqliteFixture.AddFamilies(v1, 5);
        var old = Path.GetFileName((await v1.SnapshotAsync(7, Ct))!);
        v1.Dispose();

        FileStream? heldJournal = null;
        FileStream? heldDatabase = null;
        var placedAtOpened = false;
        var database = _fx.Open(v2, fault: step =>
        {
            if (step == "opened")
            {
                placedAtOpened = Placed(_fx.File);
                heldJournal = new FileStream(_fx.File + ".recovery", FileMode.Open, FileAccess.Read, FileShare.Read);
                heldDatabase = new FileStream(_fx.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
        });
        Assert.Equal(SqliteDatabaseStatus.Migrated, database.Open());
        SqliteFixture.AddFamilies(database, 3, from: 100);
        var root = SqliteFixture.RootPage(database, "Parents");
        database.Release("test");
        SqliteFixture.CorruptPage(_fx.File, root);
        Assert.False(database.QuickCheck()!.Ok);
        var damaged = SqliteFixture.ReadOpenFile(_fx.File);

        bool placedAfterPutBack;
        try
        {
            await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(old, Ct));
            placedAfterPutBack = Placed(_fx.File);
        }
        finally
        {
            await (heldJournal?.DisposeAsync() ?? ValueTask.CompletedTask);
            await (heldDatabase?.DisposeAsync() ?? ValueTask.CompletedTask);
        }

        Assert.True(placedAtOpened, "The copy changed before the recovery was done.");
        Assert.True(placedAfterPutBack, "The put back changed the copy it couldn't delete, so the next start can't tell it is the recovery's.");
        var stuck = Assert.Single(database.ListQuarantines());
        database.Dispose();

        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var restarted = _fx.Open(v2);
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        var moved = Assert.Single(restarted.ListQuarantines());
        Assert.NotEqual(stuck.Name, moved.Name);
        Assert.StartsWith("The data was damaged and was moved to " + moved.Name, restarted.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(damaged, await File.ReadAllBytesAsync(Path.Combine(_fx.Folder, moved.Name, "userratings.db"), Ct));
        Assert.Null(restarted.Notice);
        Assert.False(File.Exists(Journal));
    }

    [Fact]
    public async Task AStartEmptyThatCantBeFinished_WithItsNewDatabaseHeld_IsPutBackByTheNextStart_NeverOpenedEmpty()
    {
        // Start empty, the journal and the new database held. Before: the restart opened the new empty database as an
        // admin's own, writable, with the data in the quarantine folder.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Holding a file so that it can't be deleted is Windows' share mode.");
        FileStream? heldJournal = null;
        FileStream? heldDatabase = null;
        var database = _fx.Open(fault: step =>
        {
            if (step == "opened")
            {
                heldJournal = new FileStream(_fx.File + ".recovery", FileMode.Open, FileAccess.Read, FileShare.Read);
                heldDatabase = new FileStream(_fx.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
        });
        Assert.Equal(SqliteDatabaseStatus.Ok, database.Open());
        SqliteFixture.AddFamilies(database, 5);
        await database.SnapshotAsync(7, Ct);
        SqliteFixture.AddFamilies(database, 3, from: 100);
        var root = SqliteFixture.RootPage(database, "Parents");
        database.Release("test");
        SqliteFixture.CorruptPage(_fx.File, root);
        Assert.False(database.QuickCheck()!.Ok);

        try
        {
            await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(null, startEmpty: true, Ct));
        }
        finally
        {
            await (heldJournal?.DisposeAsync() ?? ValueTask.CompletedTask);
            await (heldDatabase?.DisposeAsync() ?? ValueTask.CompletedTask);
        }

        Assert.True(Placed(_fx.File), "The put back changed the new database it couldn't delete.");
        database.Dispose();

        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var restarted = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        Assert.StartsWith("The data was damaged and was moved to " + Assert.Single(restarted.ListQuarantines()).Name, restarted.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(5, SqliteFixture.Count(restarted, "Parents"));
        Assert.False(File.Exists(Journal));
    }

    [Fact]
    public async Task WhileARecoveryOpensItsCopy_ARefusedJournalDeleteAfterThePoolsWereEmptied_StillFindsTheCopyAsRecorded()
    {
        // The guard: from the rename until the journal is gone, a read-only connection holds what the recovery put in
        // place, so the identity check never depends on pooling. Here the pools are emptied at the end of the open (as the
        // pool's own idle cleanup does after a few minutes), then the journal and the new database can't be deleted: without
        // the guard, the last connection to close wrote the new database's schema from the -wal into it.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Holding a file so that it can't be deleted is Windows' share mode.");
        FileStream? heldJournal = null;
        FileStream? heldDatabase = null;
        var database = _fx.Open(fault: step =>
        {
            if (step == "opened")
            {
                SqliteConnection.ClearAllPools();
                heldJournal = new FileStream(_fx.File + ".recovery", FileMode.Open, FileAccess.Read, FileShare.Read);
                heldDatabase = new FileStream(_fx.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
        });
        var snapshot = await DamagedAtRunTimeAsync(database, snapshot: true);

        try
        {
            await Assert.ThrowsAsync<PluginException>(() => database.RecoverAsync(null, startEmpty: true, Ct));
        }
        finally
        {
            await (heldJournal?.DisposeAsync() ?? ValueTask.CompletedTask);
            await (heldDatabase?.DisposeAsync() ?? ValueTask.CompletedTask);
        }

        Assert.True(Placed(_fx.File), "Emptying the pools changed the new database before the recovery was done.");
        database.Dispose();

        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var restarted = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        Assert.Contains(snapshot!, restarted.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(5, SqliteFixture.Count(restarted, "Parents"));
        Assert.False(File.Exists(Journal));
    }

    // ------------------------------------------------------------------------------------- what a journal may name

    [Theory]
    [InlineData("..\\outside", "userratings.db")]
    [InlineData("../outside", "userratings.db")]
    [InlineData("a/b", "userratings.db")]
    [InlineData("C:outside", "userratings.db")]
    [InlineData("ABSOLUTE", "userratings.db")]
    [InlineData("snapshots", "userratings.db")]
    [InlineData("quarantine-x", "userratings.db")]
    [InlineData("quarantine-20261001-000000", "..\\userratings.db")]
    [InlineData("quarantine-20261001-000000", "a/userratings.db")]
    [InlineData("quarantine-20261001-000000", "C:userratings.db")]
    [InlineData("quarantine-20261001-000000", "ABSOLUTE")]
    [InlineData("quarantine-20261001-000000", "other.db")]
    [InlineData("quarantine-20261001-000000", "reason.txt")]
    [InlineData("quarantine-20261001-000000", "userratings.db.keep-snapshots")]
    public async Task AJournalThatNamesAnythingButTheDatabasesOwnFilesInAQuarantineFolder_IsNoJournalOfOurs_AndMovesOrDeletesNothing(string folder, string file)
    {
        // A journal changed by hand must never move or delete a file outside the database's own set. The database is
        // missing (so a journal followed blindly would move what it names into its place, and remove the folder it names
        // once empty); a file waits wherever the journal points (inside the test's folder), and a sibling quarantine folder
        // holds its own note. The journal isn't one of ours: read-only, nothing created, nothing anywhere moved or deleted.
        var outside = _fx.Directory.Combine("outside");
        folder = folder == "ABSOLUTE" ? outside : folder;
        file = file == "ABSOLUTE" ? Path.Combine(outside, "userratings.db") : file;
        Directory.CreateDirectory(_fx.Folder);
        _fx.Directory.CreateFile(Path.Combine("userratings", "quarantine-20261001-000000", "reason.txt"), "an earlier quarantine's note");
        var source = Path.GetFullPath(Path.Combine(_fx.Folder, folder, file));
        if (source.StartsWith(_fx.Directory.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !File.Exists(source) && !string.Equals(source, _fx.File, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllTextAsync(source, "a file the journal points at", Ct);
        }

        await WriteJournalAsync(folder, file);
        var before = Tree(_fx.Directory.Path);

        var database = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Open());
        Assert.Equal(SqliteMessages.UnreadableRecoveryJournal("userratings.db.recovery"), database.StatusMessage);
        Assert.Equal(before, Tree(_fx.Directory.Path));
        Assert.Contains(_fx.Logger.At(LogLevel.Error), e => e.Message.Contains("isn't a recovery journal", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("quarantine-20261001-000000")]
    [InlineData("quarantine-20261001-000000-2")]
    public async Task AJournalThatNamesTheDatabasesOwnFilesInAQuarantineFolder_IsPutBack(string folder)
    {
        // The other side: what a recovery writes is still read and put back.
        Directory.CreateDirectory(_fx.Folder);
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 4);
        database.Dispose();
        Directory.CreateDirectory(Path.Combine(_fx.Folder, folder));
        File.Move(_fx.File, Path.Combine(_fx.Folder, folder, "userratings.db"));
        await File.WriteAllTextAsync(Path.Combine(_fx.Folder, folder, "reason.txt"), "note", Ct);
        await WriteJournalAsync(folder, "userratings.db", "userratings.db-wal", "userratings.db-shm", "userratings.db-journal");

        var restarted = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.Ok, restarted.Open());
        Assert.Equal(4, SqliteFixture.Count(restarted, "Parents"));
        Assert.Empty(restarted.ListQuarantines());
        Assert.False(File.Exists(Journal));
    }

    // ---------------------------------------------------------------------------------------------------- small cases

    [Fact]
    public async Task AnEarlierRecoverysEntryThatIsntOurs_OrIsNestedTooDeep_IsLeftOut_AndSaidWithTheFolderItNames()
    {
        // Before, such an entry was dropped without a word, and what its recovery had moved stayed in its folder unsaid.
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 2);
        database.Dispose();

        var malformed = Entry(null);
        malformed["Earlier"] = Entry("quarantine-20261001-000007", "other.db");
        await File.WriteAllTextAsync(Journal, malformed.ToJsonString(), Ct);
        var first = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.Ok, first.Open());
        Assert.Contains(_fx.Logger.At(LogLevel.Warning), e => e.Message.Contains("an earlier recovery's entry that isn't a journal of ours (it names the folder \"quarantine-20261001-000007\")", StringComparison.Ordinal));
        Assert.False(File.Exists(Journal));
        first.Dispose();

        // Ten entries, each inside the one before: the first nine are read (each put back, nothing to move), the tenth is
        // nested deeper than eight and said.
        JsonObject? chain = null;
        for (var depth = 9; depth >= 0; depth--)
        {
            var entry = Entry(null);
            entry["Reason"] = "level " + depth.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (depth == 9)
            {
                entry["Folder"] = "quarantine-20261001-000009";
            }

            if (chain is not null)
            {
                entry["Earlier"] = chain;
            }

            chain = entry;
        }

        await File.WriteAllTextAsync(Journal, chain!.ToJsonString(), Ct);
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        var putBackBefore = PutBackWarnings();
        var second = _fx.Open();
        Assert.Equal(SqliteDatabaseStatus.Ok, second.Open());
        Assert.Contains(_fx.Logger.At(LogLevel.Warning), e => e.Message.Contains("an earlier recovery's entry nested deeper than 8 (it names the folder \"quarantine-20261001-000009\")", StringComparison.Ordinal));
        Assert.Equal(9, PutBackWarnings() - putBackBefore);
        Assert.False(File.Exists(Journal));
        Assert.Equal(2, SqliteFixture.Count(second, "Parents"));
    }

    [Fact]
    public async Task AServerStoppedOnceTheFolderIsMade_BeforeAnythingMoved_LeavesNoFolderBehind_AndItsNoteIsTrue()
    {
        // The journal comes before the folder: a stop after the folder and its note were made, before the first move, left
        // (before) a folder no open would remove, whose note said the files "were moved here". Now the journal names the
        // folder, the next start removes it, and the note says what is and isn't there.
        var database = _fx.Open(fault: StopAt("folder"));
        var snapshot = await DamagedAtRunTimeAsync(database, snapshot: true);

        await Assert.ThrowsAsync<Stopped>(() => database.RecoverAsync(null, Ct));

        Assert.True(File.Exists(ImageFile + ".recovery"), "The journal wasn't written before the folder was made.");
        var left = Assert.Single(Directory.GetDirectories(Image, "quarantine-*"));
        Assert.Equal(["reason.txt"], Directory.GetFiles(left).Select(f => Path.GetFileName(f)));
        var note = await File.ReadAllTextAsync(Path.Combine(left, "reason.txt"), Ct);
        Assert.Contains("userratings.db, userratings.db-wal, userratings.db-shm of " + _fx.File + " were to be moved into this folder because it failed its integrity check", note, StringComparison.Ordinal);
        Assert.Contains("one that isn't was never moved", note, StringComparison.Ordinal);
        Assert.True(File.Exists(ImageFile), "The database was moved, though the server stopped before it.");

        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var restarted = _fx.Open(file: ImageFile);
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        var moved = Assert.Single(restarted.ListQuarantines());
        Assert.NotEqual(Path.GetFileName(left), moved.Name);
        Assert.Contains(snapshot!, restarted.StatusMessage, StringComparison.Ordinal);
        Assert.False(File.Exists(ImageFile + ".recovery"));
    }

    [Fact]
    public async Task AJournalWhoseFolderCantBeListed_IsNoNothingToPutBack_TheDatabaseStaysReadOnly_AndSaysSo()
    {
        // A folder the server may not read (File.Exists is false for every file in it) is never "nothing was moved": before,
        // the journal went, and with the database moved into that folder the open restored a snapshot over the data.
        var database = _fx.Open(fault: StopAt("moved:userratings.db-wal"));
        await DamagedAtRunTimeAsync(database, snapshot: true);
        await Assert.ThrowsAsync<Stopped>(() => database.RecoverAsync(null, Ct));
        var folder = Path.GetFileName(Assert.Single(Directory.GetDirectories(Image, "quarantine-*")));
        Assert.False(File.Exists(ImageFile));

        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var denied = _fx.Open(file: ImageFile, fault: step =>
        {
            if (step == "resume:list")
            {
                throw new UnauthorizedAccessException("Simulated: access to the folder is denied.");
            }
        });
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, denied.Open());
        Assert.EndsWith(SqliteMessages.RecoveryNotPutBack(SqliteMessages.InterruptedRecovery, folder), denied.StatusMessage, StringComparison.Ordinal);
        Assert.Null(denied.LastRecovery);
        Assert.False(File.Exists(ImageFile), "Nothing may take the database's place while its folder can't be read.");
        Assert.True(File.Exists(ImageFile + ".recovery"));
        Assert.Contains(_fx.Logger.At(LogLevel.Error), e => e.Message.Contains("couldn't be put back", StringComparison.Ordinal) && e.Message.Contains("the folder couldn't be read", StringComparison.Ordinal));
        denied.Dispose();

        // A file in the folder's place: the same.
        var aside = Path.Combine(Path.GetDirectoryName(ImageFile)!, "moved-away");
        Directory.Move(Path.Combine(Image, folder), aside);
        await File.WriteAllTextAsync(Path.Combine(Image, folder), "not a folder", Ct);
        var blocked = _fx.Open(file: ImageFile);
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, blocked.Open());
        Assert.False(File.Exists(ImageFile));
        blocked.Dispose();

        // The folder readable again: put back, then restored as any damaged database.
        File.Delete(Path.Combine(Image, folder));
        Directory.Move(aside, Path.Combine(Image, folder));
        var restarted = _fx.Open(file: ImageFile);
        Assert.Equal(SqliteDatabaseStatus.RestoredFromSnapshot, restarted.Open());
        Assert.False(File.Exists(ImageFile + ".recovery"));
    }

    // A journal entry as a recovery writes one: no Placed, nothing failed.
    private static JsonObject Entry(string? folder, params string[] files) => new()
    {
        ["Folder"] = folder,
        ["Files"] = new JsonArray(files.Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
        ["Step"] = "quarantine",
        ["Utc"] = "2026-10-01T00:00:00Z",
        ["Damaged"] = true,
        ["Reason"] = "a test's journal",
        ["Snapshot"] = null,
        ["Placed"] = null,
        ["Failure"] = null
    };

    private Task WriteJournalAsync(string? folder, params string[] files) => File.WriteAllTextAsync(Journal, Entry(folder, files).ToJsonString(), Ct);

    private int PutBackWarnings() => _fx.Logger.At(LogLevel.Warning).Count(e => e.Message.Contains("didn't finish; what it had moved aside (nothing)", StringComparison.Ordinal));

    // Every folder and file under a folder, each file with its SHA-256 (a -shm by name only).
    private static List<string> Tree(string root)
        => Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => Directory.Exists(p)
                ? Path.GetRelativePath(root, p) + "/"
                : Path.GetRelativePath(root, p) + (p.EndsWith("-shm", StringComparison.Ordinal) ? string.Empty : " " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(SqliteFixture.ReadOpenFile(p)))))
            .Order(StringComparer.Ordinal)
            .ToList();

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

    // Every file and folder in a folder (the snapshots' folder by name only), each file with its bytes' hash; the -shm by
    // name only, since a reader's connection writes its read marks there.
    private static List<string> Listing(string folder)
    {
        var entries = Directory.GetDirectories(folder).Select(d => Path.GetFileName(d) + "/").ToList();
        foreach (var file in Directory.GetFiles(folder))
        {
            entries.Add(file.EndsWith("-shm", StringComparison.Ordinal)
                ? Path.GetFileName(file)
                : Path.GetFileName(file) + " " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(SqliteFixture.ReadOpenFile(file))));
        }

        return entries.Order(StringComparer.Ordinal).ToList();
    }

    // Whether the file is the one the image's journal says the recovery put there (its size and SHA-256).
    private static bool Placed(string file)
    {
        var journal = Path.Combine(Path.GetDirectoryName(file)!, "userratings.db.recovery");
        if (!File.Exists(journal) || JsonNode.Parse(File.ReadAllBytes(journal))?["Placed"] is not JsonObject placed)
        {
            return false;
        }

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.Length == (long)placed["Size"]! && string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)), (string)placed["Sha256"]!, StringComparison.OrdinalIgnoreCase);
    }

    // The fault hook that stops the server at a step: the folder copied as it is, then nothing more runs.
    private Action<string> StopAt(string step)
        => s =>
        {
            if (s == step && !Directory.Exists(Image))
            {
                SqliteFixture.CopyTree(_fx.Folder, Image);
                throw new Stopped();
            }
        };

    private List<LogEntry> KeptWarnings()
        => _fx.Logger.At(LogLevel.Warning).Where(e => e.Message.Contains("are kept until one of them is restored", StringComparison.Ordinal)).ToList();

    // Five families and the many-page Bulk table (and a daily snapshot of them, or none), then seven rows in Recent that are
    // only in the -wal, the pools open, and Bulk's root page overwritten: damaged at run time (quick_check), with a hot WAL.
    // Returns the snapshot's name.
    private async Task<string?> DamagedAtRunTimeAsync(SqliteDatabase database, bool snapshot)
    {
        SqliteFixture.AddFamilies(database, 5);
        SqliteFixture.AddBulk(database);
        var name = snapshot ? Path.GetFileName((await database.SnapshotAsync(7, Ct))!) : null;
        var root = SqliteFixture.RootPage(database, "Bulk");
        database.Release("test");
        SqliteFixture.AddRecent(database, 7);
        SqliteFixture.CorruptPage(_fx.File, root);
        Assert.False(database.QuickCheck()!.Ok);
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, database.Status);
        Assert.True(new FileInfo(_fx.File + "-wal").Length > 0);
        return name;
    }

    // The server stopped here: nothing after it runs, and nothing catches it.
    private sealed class Stopped : Exception
    {
        public Stopped()
            : base("The server stopped here (a test's stand-in for a kill).")
        {
        }
    }
}
