using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Sqlite;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.UserRatings.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Storage;

/// <summary>
/// The SQLite tests on <c>userratings.db</c> through the kit's runner: the migrations from 0 to 3 with every
/// table and index (M002 adds <c>Ratings.AttachedUtc</c>, and a version 1 database upgrades with its rows kept;
/// M003 vacuums the file once, since 0.3.14), read-only for a newer schema, the CHECK constraints, a test migration that rebuilds
/// <c>Ratings</c> with the kit's FK-safe procedure (and the control that shows the cascade it prevents), WAL readers during
/// a write, the two snapshot classes, damage found by a snapshot, by <c>quick_check</c> and by a query, the corruption
/// restore with a hot WAL, and the loose timing of the summary query on 50,000 rows.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class RatingsSchemaTests : IDisposable
{
    private static readonly string[] Tables = ["FormerUsers", "ImportLedger", "ImportLedgerRows", "Meta", "ModerationLog", "RatingKeys", "Ratings", "Tombstones"];

    private static readonly string[] Indexes =
    [
        "IX_ImportLedgerRows_Rating", "IX_ModLog_At", "IX_RatingKeys_Key", "IX_Ratings_Album", "IX_Ratings_Detached", "IX_Ratings_Item",
        "IX_Ratings_Recent", "IX_Ratings_Season", "IX_Ratings_Series", "IX_Ratings_User", "UX_Ratings_User_Item"
    ];

    // A rebuild of Ratings following sqlite.org/lang_altertable.html §7: a new column and a stricter CHECK, the rows copied,
    // the old table dropped, the new one renamed, the indexes made again. RatingKeys points at Ratings(Id) ON DELETE CASCADE.
    private static readonly string RebuildRatingsSql =
        "CREATE TABLE new_Ratings ("
        + "Id TEXT PRIMARY KEY, UserId TEXT NOT NULL, ItemId TEXT NOT NULL, Stars2 INTEGER NULL CHECK (Stars2 BETWEEN 1 AND 10), "
        + "ReviewText TEXT NULL CHECK (ReviewText IS NULL OR length(ReviewText) BETWEEN 1 AND 20000), IsSpoiler INTEGER NOT NULL DEFAULT 0 CHECK (IsSpoiler IN (0, 1)), "
        + "CreatedUtc TEXT NOT NULL, UpdatedUtc TEXT NOT NULL, ReviewUpdatedUtc TEXT NULL, EditCount INTEGER NOT NULL DEFAULT 0 CHECK (EditCount >= 0), "
        + "Origin TEXT NOT NULL DEFAULT 'User', ItemKind TEXT NOT NULL, ItemName TEXT NULL, ProductionYear INTEGER NULL, ProviderIds TEXT NULL, "
        + "SeriesProviderIds TEXT NULL, SeriesName TEXT NULL, SeasonNumber INTEGER NULL, EpisodeNumber INTEGER NULL, ItemPath TEXT NULL, SeriesId TEXT NULL, "
        + "SeasonId TEXT NULL, AlbumId TEXT NULL, DetachedUtc TEXT NULL, DetachReason TEXT NULL, MirroredValue REAL NULL, AttachedUtc TEXT NULL, Extra TEXT NULL, "
        + "CHECK (Stars2 IS NOT NULL OR ReviewText IS NOT NULL), CHECK ((DetachedUtc IS NULL) = (DetachReason IS NULL)));"
        + "INSERT INTO new_Ratings (Id, UserId, ItemId, Stars2, ReviewText, IsSpoiler, CreatedUtc, UpdatedUtc, ReviewUpdatedUtc, EditCount, Origin, ItemKind, ItemName, "
        + "ProductionYear, ProviderIds, SeriesProviderIds, SeriesName, SeasonNumber, EpisodeNumber, ItemPath, SeriesId, SeasonId, AlbumId, DetachedUtc, DetachReason, MirroredValue, AttachedUtc) "
        + "SELECT Id, UserId, ItemId, Stars2, ReviewText, IsSpoiler, CreatedUtc, UpdatedUtc, ReviewUpdatedUtc, EditCount, Origin, ItemKind, ItemName, "
        + "ProductionYear, ProviderIds, SeriesProviderIds, SeriesName, SeasonNumber, EpisodeNumber, ItemPath, SeriesId, SeasonId, AlbumId, DetachedUtc, DetachReason, MirroredValue, AttachedUtc FROM Ratings;"
        + "DROP TABLE Ratings;"
        + "ALTER TABLE new_Ratings RENAME TO Ratings;"
        + "CREATE UNIQUE INDEX UX_Ratings_User_Item ON Ratings(UserId, ItemId) WHERE DetachedUtc IS NULL;"
        + "CREATE INDEX IX_Ratings_Item ON Ratings(ItemId) WHERE DetachedUtc IS NULL;"
        + "CREATE INDEX IX_Ratings_User ON Ratings(UserId, UpdatedUtc);"
        + "CREATE INDEX IX_Ratings_Recent ON Ratings(ReviewUpdatedUtc) WHERE ReviewText IS NOT NULL AND DetachedUtc IS NULL;"
        + "CREATE INDEX IX_Ratings_Series ON Ratings(SeriesId) WHERE SeriesId IS NOT NULL;"
        + "CREATE INDEX IX_Ratings_Season ON Ratings(SeasonId) WHERE SeasonId IS NOT NULL;"
        + "CREATE INDEX IX_Ratings_Album ON Ratings(AlbumId) WHERE AlbumId IS NOT NULL;"
        + "CREATE INDEX IX_Ratings_Detached ON Ratings(DetachedUtc) WHERE DetachedUtc IS NOT NULL;";

    private readonly StoreFixture _fx = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fx.Dispose();

    // --------------------------------------------------------------------------------------------------- the schema

    [Fact]
    public void ANewDatabase_IsCreatedAtVersion4_WithEveryDocumentedTableAndIndex_AndSecureDelete()
    {
        Assert.Equal(SqliteDatabaseStatus.Ok, _fx.Database.Status);
        Assert.False(_fx.Store.IsReadOnly);
        Assert.Equal((4, 4), (_fx.Database.SchemaVersion, RatingsSchema.CurrentVersion));
        Assert.Equal(1, _fx.Database.Read(c => StoreFixture.Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('Tombstones') WHERE name = 'Origin';")));
        Assert.Equal(1, _fx.Database.Read(c => StoreFixture.Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('FormerUsers') WHERE name = 'Origin';")));
        Assert.Equal(1, _fx.Database.Read(c => StoreFixture.Scalar(c, "PRAGMA secure_delete;")));
        Assert.Equal(1, _fx.Database.Read(c => StoreFixture.Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('Ratings') WHERE name = 'AttachedUtc';")));
        Assert.True(File.Exists(_fx.File));
        Assert.Equal(Tables, Names("table"));
        Assert.Equal(Indexes, Names("index").Where(n => !n.StartsWith("sqlite_autoindex", StringComparison.Ordinal)));
        Assert.Equal(1, _fx.Database.Read(c => StoreFixture.Scalar(c, "PRAGMA foreign_keys;")));
        Assert.Equal("wal", _fx.Database.Read(c => Text(c, "PRAGMA journal_mode;")));

        // A new database has nothing to keep: no pre-operation snapshot (the kit's rule).
        Assert.Empty(_fx.Database.ListSnapshots());
        Assert.Null(_fx.Store.StatusMessage);
    }

    [Fact]
    public void TheMigrations_AreM001Initial_M002AttachedUtc_M003Vacuum_AndM004DeletionOrigin_NoneARebuild_OnlyM003AVacuum()
    {
        Assert.Equal(
            [(1, "M001_Initial", false, false), (2, "M002_AttachedUtc", false, false), (3, "M003_Vacuum", false, true), (4, "M004_DeletionOrigin", false, false)],
            RatingsSchema.Migrations.Select(m => (m.Version, m.Name, m.RequiresRebuild, m.VacuumAfter)));
        Assert.Equal("userratings.db", RatingsSchema.DatabaseFile);
    }

    [Fact]
    public async Task AVersion2Database_WithADeletedReviewInItsFreeSpace_IsVacuumedByM003_AfterAPreOpSnapshotThatExpiresIn7Days()
    {
        // A 0.3.13 file (M002, no secure_delete) keeps a deleted review's bytes in its free
        // space; M003's VACUUM rebuilds it once. The migrate snapshot before it is the old file as it was, kept 7 days.
        const string Marker = "UR M003 residue marker: the orchard was empty.";
        _fx.Reopen([RatingsSchema.Migrations[0], RatingsSchema.Migrations[1]]);
        for (var i = 0; i < 30; i++)
        {
            await _fx.Store.UpsertAsync(StoreFixture.Movie(Guid.NewGuid(), Guid.NewGuid(), 6, "Synthetic padding review " + i + "."), Ct);
        }

        var deleted = Guid.NewGuid();
        using (var old = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _fx.File, Pooling = false }.ToString()))
        {
            await old.OpenAsync(Ct);
            StoreFixture.Execute(old, null, "PRAGMA secure_delete=OFF;");
            StoreFixture.Execute(old, null, "INSERT INTO Ratings (Id, UserId, ItemId, Stars2, ReviewText, CreatedUtc, UpdatedUtc, ItemKind) VALUES ('" + deleted.ToString("N")
                + "', '" + Guid.NewGuid().ToString("N") + "', '" + Guid.NewGuid().ToString("N") + "', 7, '" + Marker + "', '2026-10-19T03:20:00.0000000Z', '2026-10-19T03:20:00.0000000Z', 'Movie');");
            StoreFixture.Execute(old, null, "DELETE FROM Ratings WHERE Id = '" + deleted.ToString("N") + "';");
            StoreFixture.Execute(old, null, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        Assert.Equal(2, _fx.Database.SchemaVersion);
        Assert.Contains(RatingsSchema.DatabaseFile, FilesHolding(Marker, _fx.File));

        _fx.Reopen();

        Assert.Equal((SqliteDatabaseStatus.Migrated, RatingsSchema.CurrentVersion), (_fx.Database.Status, _fx.Database.SchemaVersion));
        Assert.Empty(FilesHolding(Marker, _fx.File, _fx.File + "-wal"));
        Assert.Equal(30, _fx.Count("Ratings"));
        var migrate = Assert.Single(_fx.Database.ListSnapshots(), s => s.Kind == SnapshotKind.PreOp && s.Operation == SnapshotOperations.Migrate);
        var copy = Path.Combine(_fx.Snapshots, "pre-op", migrate.Name);
        Assert.NotEmpty(FilesHolding(Marker, copy));

        // The daily snapshot after it holds nothing of it; the old copy goes once it is older than 7 days.
        var daily = (await _fx.Database.SnapshotAsync(7, Ct))!;
        Assert.Empty(FilesHolding(Marker, daily));
        _fx.Clock.Advance(TimeSpan.FromDays(6));
        await _fx.Database.SnapshotAsync(7, Ct);
        Assert.True(File.Exists(copy));
        _fx.Clock.Advance(TimeSpan.FromDays(2));
        await _fx.Database.SnapshotAsync(7, Ct);
        Assert.False(File.Exists(copy));
        Assert.DoesNotContain(_fx.Database.ListSnapshots(), s => s.Kind == SnapshotKind.PreOp);
    }

    [Fact]
    public async Task M003sVacuum_ThatFails_IsAWarning_AndTheNextStartVacuumsTheFile()
    {
        // M003's VACUUM on a full disk used to leave the deleted review in the free space for good.
        const string Marker = "UR M003 retry marker: the ferry left at dawn.";
        _fx.Reopen([RatingsSchema.Migrations[0], RatingsSchema.Migrations[1]]);
        for (var i = 0; i < 30; i++)
        {
            await _fx.Store.UpsertAsync(StoreFixture.Movie(Guid.NewGuid(), Guid.NewGuid(), 6, "Synthetic padding review " + i + "."), Ct);
        }

        var deleted = Guid.NewGuid().ToString("N");
        using (var old = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _fx.File, Pooling = false }.ToString()))
        {
            await old.OpenAsync(Ct);
            StoreFixture.Execute(old, null, "PRAGMA secure_delete=OFF;");
            StoreFixture.Execute(old, null, "INSERT INTO Ratings (Id, UserId, ItemId, Stars2, ReviewText, CreatedUtc, UpdatedUtc, ItemKind) VALUES ('" + deleted
                + "', '" + Guid.NewGuid().ToString("N") + "', '" + Guid.NewGuid().ToString("N") + "', 7, '" + Marker + "', '2026-10-19T03:20:00.0000000Z', '2026-10-19T03:20:00.0000000Z', 'Movie');");
            StoreFixture.Execute(old, null, "DELETE FROM Ratings WHERE Id = '" + deleted + "';");
            StoreFixture.Execute(old, null, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        _fx.Reopen(vacuumFault: static () => throw new SqliteException("Synthetic: database or disk is full", 13));

        Assert.Equal((SqliteDatabaseStatus.Migrated, RatingsSchema.CurrentVersion), (_fx.Database.Status, _fx.Database.SchemaVersion));
        Assert.True(File.Exists(_fx.Database.VacuumPendingPath));
        Assert.Contains(_fx.Logger.At(LogLevel.Warning), e => e.Message.Contains("its VACUUM failed", StringComparison.Ordinal));
        Assert.NotEmpty(FilesHolding(Marker, _fx.File, _fx.File + "-wal"));
        Assert.Equal(30, _fx.Count("Ratings"));

        _fx.Reopen();

        Assert.Equal((SqliteDatabaseStatus.Ok, RatingsSchema.CurrentVersion), (_fx.Database.Status, _fx.Database.SchemaVersion));
        Assert.False(File.Exists(_fx.Database.VacuumPendingPath));
        Assert.Empty(FilesHolding(Marker, _fx.File, _fx.File + "-wal"));
        Assert.Equal(30, _fx.Count("Ratings"));
    }

    [Fact]
    public async Task AVersion3Database_UpgradesToVersion4_ItsTombstonesAndFormerMembersLocal()
    {
        // 0.3.20 (M004_DeletionOrigin): every tombstone and former member there is was made here, or by an import before this
        // version, and reads Local; nothing is rebuilt.
        _fx.Reopen([RatingsSchema.Migrations[0], RatingsSchema.Migrations[1], RatingsSchema.Migrations[2]]);
        var rating = Guid.NewGuid();
        var former = Guid.NewGuid();
        await _fx.Database.WriteAsync(
            (c, t) =>
            {
                StoreFixture.Execute(c, t, "INSERT INTO Tombstones (RatingId, UserId, ItemId, DeletedUtc, DeletedBy) VALUES ('" + rating.ToString("N") + "', '" + Guid.NewGuid().ToString("N") + "', '" + Guid.NewGuid().ToString("N") + "', '2026-10-19T03:20:00.0000000Z', 'User');");
                StoreFixture.Execute(c, t, "INSERT INTO FormerUsers (Id, OriginalUserId, DisplayName, DeletedUtc) VALUES ('" + former.ToString("N") + "', '" + Guid.NewGuid().ToString("N") + "', 'Former member', '2026-10-19T03:20:00.0000000Z');");
                return 0;
            },
            Ct);
        Assert.Equal(3, _fx.Database.SchemaVersion);

        _fx.Reopen();

        Assert.Equal((SqliteDatabaseStatus.Migrated, 4), (_fx.Database.Status, _fx.Database.SchemaVersion));
        Assert.Equal(RatingValues.DeletionOrigin.Local, _fx.Store.GetTombstone(rating)!.Origin);
        Assert.Equal(RatingValues.DeletionOrigin.Local, Assert.Single(_fx.Store.ReadTables().FormerUsers).Origin);
        Assert.Single(_fx.Database.ListSnapshots(), s => s.Kind == SnapshotKind.PreOp && s.Operation == SnapshotOperations.Migrate);
    }

    [Fact]
    public async Task AVersion1Database_UpgradesToTheCurrentVersion_KeepingItsRows_WhoseAttachedUtcIsNull_SoTheyCountAsAttachedSinceCreated()
    {
        // A 0.3.6 database (M001 only), with a rating written as 0.3.6 wrote it.
        _fx.Reopen([RatingsSchema.Migrations[0]]);
        var id = Guid.NewGuid();
        await _fx.Database.WriteAsync(
            (c, t) =>
            {
                StoreFixture.Execute(c, t, "INSERT INTO Ratings (Id, UserId, ItemId, Stars2, CreatedUtc, UpdatedUtc, ItemKind) VALUES ('" + id.ToString("N")
                    + "', '" + Guid.NewGuid().ToString("N") + "', '" + Guid.NewGuid().ToString("N") + "', 7, '2026-10-19T03:20:00.0000000Z', '2026-10-19T03:20:00.0000000Z', 'Movie');");
                return 0;
            },
            Ct);
        Assert.Equal(1, _fx.Database.SchemaVersion);

        _fx.Reopen();

        Assert.Equal((SqliteDatabaseStatus.Migrated, RatingsSchema.CurrentVersion), (_fx.Database.Status, _fx.Database.SchemaVersion));
        var row = _fx.Store.Get(id)!;
        Assert.Null(row.AttachedUtc);
        Assert.Equal(StoreFixture.T0, row.AttachedSinceUtc);
        Assert.Single(_fx.Database.ListSnapshots(), s => s.Kind == SnapshotKind.PreOp && s.Operation == SnapshotOperations.Migrate);

        // Written again, the column round-trips.
        await _fx.Store.UpsertAsync(row with { AttachedUtc = StoreFixture.T0.AddDays(1) }, Ct);
        Assert.Equal(StoreFixture.T0.AddDays(1), _fx.Store.Get(id)!.AttachedSinceUtc);
    }

    [Fact]
    public async Task ANewerSchema_IsReadOnly_WritesAre503_ReadsGoOn()
    {
        await _fx.Store.UpsertAsync(StoreFixture.Movie(Guid.NewGuid(), Guid.NewGuid()), Ct);
        await _fx.Database.WriteAsync((c, t) => { StoreFixture.Execute(c, t, "PRAGMA user_version = 99;"); return 0; }, Ct);

        _fx.Reopen();

        Assert.Equal(SqliteDatabaseStatus.ReadOnlyNewerVersion, _fx.Database.Status);
        Assert.True(_fx.Store.IsReadOnly);
        Assert.Equal(SqliteMessages.NewerVersion, _fx.Store.StatusMessage);
        var write = await Assert.ThrowsAsync<PluginException>(() => _fx.Store.UpsertAsync(StoreFixture.Movie(Guid.NewGuid(), Guid.NewGuid()), Ct));
        Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.NewerVersion), (write.Kind, write.Message));
        await Assert.ThrowsAsync<PluginException>(() => _fx.Store.SetMetaAsync("k", "v", Ct));
        Assert.Equal(1, _fx.Store.Count().Attached);
        Assert.Equal(99, _fx.Database.LoadedVersion);
    }

    public static TheoryData<string, string> CheckViolations => new()
    {
        { "stars 0", "Stars2 = 0" },
        { "stars 11", "Stars2 = 11" },
        { "empty review", "ReviewText = ''" },
        { "review over 20000", "ReviewText = hex(zeroblob(10001))" },
        { "neither stars nor text", "Stars2 = NULL, ReviewText = NULL" },
        { "IsSpoiler 2", "IsSpoiler = 2" },
        { "detached without a reason", "DetachedUtc = '2026-10-19T00:00:00.0000000Z'" },
        { "a reason without a time", "DetachReason = 'Removed'" }
    };

    [Theory]
    [MemberData(nameof(CheckViolations))]
    public async Task TheCheckConstraints_RejectBadRows_EvenPastTheStore(string what, string set)
    {
        Assert.NotEmpty(what);
        var record = StoreFixture.Movie(Guid.NewGuid(), Guid.NewGuid(), 7, "text");
        await _fx.Store.UpsertAsync(record, Ct);

        var error = await Assert.ThrowsAsync<SqliteException>(() => _fx.Database.WriteAsync(
            (c, t) =>
            {
                StoreFixture.Execute(c, t, "UPDATE Ratings SET " + set + " WHERE Id = '" + record.Id.ToString("N") + "';");
                return 0;
            },
            Ct));

        Assert.Equal(19, error.SqliteErrorCode);
        Assert.Equal(record, _fx.Store.Get(record.Id));
    }

    [Fact]
    public async Task GrowableEnums_AreNotChecked_ByTheDatabase_OnlyByTheStore()
    {
        // The CHECK policy: a value a later version adds needs no rebuild, so the table takes it; the store refuses it.
        var record = StoreFixture.Movie(Guid.NewGuid(), Guid.NewGuid());
        await _fx.Store.UpsertAsync(record, Ct);

        await _fx.Database.WriteAsync((c, t) => { StoreFixture.Execute(c, t, "UPDATE Ratings SET Origin = 'Trakt';"); return 0; }, Ct);

        Assert.Equal("Trakt", _fx.Store.Get(record.Id)!.Origin);
        await Assert.ThrowsAsync<ArgumentException>(() => _fx.Store.UpsertAsync(_fx.Store.Get(record.Id)!, Ct));
    }

    // ---------------------------------------------------------------------------------------------- the FK-safe rebuild

    [Fact]
    public async Task ARebuildOfRatings_ThroughTheKitsFkSafeProcedure_KeepsEveryRatingKeysRow()
    {
        await SeedWithKeysAsync(40);
        var keys = _fx.Count("RatingKeys");
        Assert.True(keys >= 80);

        _fx.Reopen([.. RatingsSchema.Migrations, new SqliteMigration(5, "rebuild Ratings", RebuildRatingsSql) { RequiresRebuild = true }]);

        Assert.Equal(SqliteDatabaseStatus.Migrated, _fx.Database.Status);
        Assert.Equal(5, _fx.Database.SchemaVersion);
        Assert.Equal(40, _fx.Count("Ratings"));
        Assert.Equal(keys, _fx.Count("RatingKeys"));
        Assert.Equal(0, _fx.Database.Read(c => StoreFixture.Scalar(c, "SELECT COUNT(*) FROM pragma_foreign_key_check;")));
        Assert.Equal(1, _fx.Database.Read(c => StoreFixture.Scalar(c, "PRAGMA foreign_keys;")));
        Assert.Contains("Extra", _fx.Database.Read(c => Text(c, "SELECT sql FROM sqlite_master WHERE name = 'Ratings';")), StringComparison.Ordinal);
        Assert.Equal(Indexes, Names("index").Where(n => !n.StartsWith("sqlite_autoindex", StringComparison.Ordinal)));

        // The upgrade's safety copy, then the store still writes, and a delete still cascades into the new table's keys.
        Assert.Single(_fx.Database.ListSnapshots(), s => s.Kind == SnapshotKind.PreOp && s.Operation == SnapshotOperations.Migrate);
        var first = _fx.Database.Read(c => Text(c, "SELECT Id FROM Ratings ORDER BY Id LIMIT 1;"));
        Assert.True(await _fx.Store.DeleteAsync(Guid.Parse(first), RatingValues.DeletedBy.Purge, StoreFixture.T0, Ct));
        Assert.Equal(keys - 2, _fx.Count("RatingKeys"));
    }

    [Fact]
    public async Task TheSameRebuild_WithoutRequiresRebuild_LosesEveryRatingKeysRow_TheCascadeTheProcedurePrevents()
    {
        // The control: with foreign keys on (every kit connection), DROP TABLE Ratings deletes every child row.
        await SeedWithKeysAsync(10);
        Assert.True(_fx.Count("RatingKeys") > 0);

        _fx.Reopen([.. RatingsSchema.Migrations, new SqliteMigration(5, "rebuild Ratings (no flag)", RebuildRatingsSql)]);

        Assert.Equal(5, _fx.Database.SchemaVersion);
        Assert.Equal(10, _fx.Count("Ratings"));
        Assert.Equal(0, _fx.Count("RatingKeys"));
    }

    [Fact]
    public async Task ARebuildThatOrphansKeys_IsRolledBack_AndTheDatabaseStaysAtVersion4ReadOnly()
    {
        // foreign_key_check before COMMIT: a rebuild that drops parent rows fails the whole upgrade.
        await SeedWithKeysAsync(5);
        const string Orphaning = "CREATE TABLE new_Ratings AS SELECT * FROM Ratings WHERE 0; DROP TABLE Ratings; ALTER TABLE new_Ratings RENAME TO Ratings;";

        _fx.Reopen([.. RatingsSchema.Migrations, new SqliteMigration(5, "orphan the keys", Orphaning) { RequiresRebuild = true }]);

        Assert.Equal(SqliteDatabaseStatus.ReadOnlyMigrationFailed, _fx.Database.Status);
        Assert.True(_fx.Store.IsReadOnly);
        _fx.Reopen();
        Assert.Equal(SqliteDatabaseStatus.Ok, _fx.Database.Status);
        Assert.Equal(4, _fx.Database.SchemaVersion);
        Assert.Equal(5, _fx.Count("Ratings"));
    }

    // ----------------------------------------------------------------------------------------------------------- WAL

    [Fact]
    public async Task AReader_IsNotBlockedByAWriteInProgress_AndSeesTheStateBeforeIt()
    {
        var item = Guid.NewGuid();
        await _fx.Store.UpsertAsync(StoreFixture.Movie(Guid.NewGuid(), item, 4), Ct);
        using var inWrite = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var write = Task.Run(
            () => _fx.Database.WriteAsync(
                (c, t) =>
                {
                    StoreFixture.Execute(c, t, "UPDATE Ratings SET Stars2 = 10;");
                    inWrite.Set();
                    release.Wait(TimeSpan.FromSeconds(30), Ct);
                    return 0;
                },
                Ct),
            Ct);
        Assert.True(inWrite.Wait(TimeSpan.FromSeconds(30), Ct));

        var watch = Stopwatch.StartNew();
        var during = _fx.Store.GetSummaryRows([item]);
        watch.Stop();
        release.Set();
        await write;

        Assert.Equal(4, Assert.Single(during).Stars2);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"The read waited {watch.Elapsed} for the write.");
        Assert.Equal(10, Assert.Single(_fx.Store.GetSummaryRows([item])).Stars2);
    }

    // ------------------------------------------------------------------------------------------------------- snapshots

    [Fact]
    public async Task DailyAndPreOperationSnapshots_RotateIndependently()
    {
        await SeedWithKeysAsync(3);
        for (var day = 0; day < 3; day++)
        {
            Assert.NotNull(await _fx.Database.SnapshotAsync(2, Ct));
            _fx.Clock.Advance(TimeSpan.FromDays(1));
        }

        for (var i = 0; i < 6; i++)
        {
            Assert.NotNull(await _fx.Database.PreOpSnapshotAsync(SnapshotOperations.Import, Ct));
            _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var snapshots = _fx.Database.ListSnapshots();
        Assert.Equal(["userratings-20261020-032000.db", "userratings-20261021-032000.db"], snapshots.Where(s => s.Kind == SnapshotKind.Daily).Select(s => s.Name).Order(StringComparer.Ordinal));
        Assert.All(snapshots, s => Assert.Equal(RatingsSchema.CurrentVersion, s.SchemaVersion));

        // The pre-op class keeps every one of the last 7 days (since 0.3.13) and, since 0.3.14, none older,
        // not even the newest five: a new one deletes them, and so does the daily snapshot.
        Assert.Equal(6, snapshots.Count(s => s.Kind == SnapshotKind.PreOp));
        _fx.Clock.Advance(TimeSpan.FromDays(8));
        Assert.NotNull(await _fx.Database.PreOpSnapshotAsync(SnapshotOperations.Import, Ct));
        Assert.Equal(1, _fx.Database.ListSnapshots().Count(s => s.Kind == SnapshotKind.PreOp));
        Assert.Equal(2, _fx.Database.ListSnapshots().Count(s => s.Kind == SnapshotKind.Daily));
        _fx.Clock.Advance(TimeSpan.FromDays(8));
        Assert.NotNull(await _fx.Database.SnapshotAsync(2, Ct));
        Assert.DoesNotContain(_fx.Database.ListSnapshots(), s => s.Kind == SnapshotKind.PreOp);
        Assert.Equal(2, _fx.Database.ListSnapshots().Count(s => s.Kind == SnapshotKind.Daily));
    }

    // ------------------------------------------------------------------------------------------------------- integrity

    [Fact]
    public async Task ASnapshotOfADamagedDatabase_IsDiscarded_TheGoodOneKept_AndTheStoreGoesReadOnly()
    {
        await SeedBulkAsync(2000);
        var good = Path.GetFileName((await _fx.Database.SnapshotAsync(3, Ct))!);
        var root = StoreFixture.RootPage(_fx.Database, "Ratings");
        _fx.Database.Release("test");
        StoreFixture.CorruptPage(_fx.File, root);
        _fx.Clock.Advance(TimeSpan.FromDays(1));

        Assert.Null(await _fx.Database.SnapshotAsync(3, Ct));

        Assert.Equal([good], StoreFixture.Names(_fx.Snapshots));
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, _fx.Database.Status);
        Assert.True(_fx.Store.IsReadOnly);
        var write = await Assert.ThrowsAsync<PluginException>(() => _fx.Store.SetMetaAsync("k", "v", Ct));
        Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.Corrupt), (write.Kind, write.Message));
        Assert.Contains(_fx.Logger.At(LogLevel.Error), e => e.Message.Contains("failed its integrity check", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADamagedPage_IsCaughtByQuickCheck_AndByAQueryThatTouchesIt()
    {
        var seeded = await SeedBulkAsync(2000);
        var root = StoreFixture.RootPage(_fx.Database, "Ratings");
        _fx.Database.Release("test");
        StoreFixture.CorruptPage(_fx.File, root);

        // A query that touches the page (a row's columns, read from the table itself): SQLITE_CORRUPT turns into the 503
        // and read-only.
        var read = Assert.Throws<PluginException>(() => _fx.Store.Get(seeded[0].Id));
        Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.Corrupt), (read.Kind, read.Message));
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, _fx.Database.Status);

        // quick_check agrees, and undamaged tables can still be read.
        Assert.False(_fx.Database.QuickCheck()!.Ok);
        Assert.Null(_fx.Store.GetMeta("anything"));
    }

    [Fact]
    public async Task TheCorruptionRestore_WithAHotWal_QuarantinesTheFilesTogether_AndTheStoreHasTheSnapshotsRows()
    {
        await SeedBulkAsync(2000);
        var snapshot = Path.GetFileName((await _fx.Database.SnapshotAsync(7, Ct))!);
        var root = StoreFixture.RootPage(_fx.Database, "Ratings");
        _fx.Database.Release("test");
        await _fx.Store.SetMetaAsync("after-snapshot", "lost", Ct);
        Assert.True(new FileInfo(_fx.File + "-wal").Length > 0, "Uncheckpointed frames are in the WAL.");
        StoreFixture.CorruptPage(_fx.File, root);
        var damaged = StoreFixture.ReadOpenFile(_fx.File);
        Assert.False(_fx.Database.QuickCheck()!.Ok);
        _fx.Clock.Advance(TimeSpan.FromHours(3));

        var recovery = await _fx.Database.RecoverAsync(null, Ct);

        Assert.Equal(snapshot, recovery.RestoredFrom);
        Assert.Equal(["userratings.db", "userratings.db-shm", "userratings.db-wal"], recovery.QuarantinedFiles.Order(StringComparer.Ordinal));
        var quarantine = Path.Combine(_fx.Folder, recovery.QuarantineFolder!);
        Assert.Equal(damaged, await File.ReadAllBytesAsync(Path.Combine(quarantine, "userratings.db"), Ct));
        Assert.True(new FileInfo(Path.Combine(quarantine, "userratings.db-wal")).Length > 0, "The WAL went with its database.");
        Assert.False(_fx.Store.IsReadOnly);
        Assert.True(_fx.Database.QuickCheck()!.Ok);
        Assert.Equal(2000, _fx.Store.Count().Attached);
        Assert.Null(_fx.Store.GetMeta("after-snapshot"));
        await _fx.Store.SetMetaAsync("after-restore", "kept", Ct);
        Assert.Equal("kept", _fx.Store.GetMeta("after-restore"));
    }

    // ---------------------------------------------------------------------------------------------------- timing (loose)

    [Fact]
    public async Task TheSummaryQuery_For100Items_On50000Ratings_TakesUnder50Milliseconds()
    {
        var users = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
        var items = Enumerable.Range(0, 5000).Select(_ => Guid.NewGuid()).ToArray();
        var records = new List<Jellyfin.Plugin.UserRatings.Domain.RatingRecord>(50000);
        foreach (var item in items)
        {
            foreach (var user in users)
            {
                records.Add(StoreFixture.Movie(user, item, 1 + (records.Count % 10), records.Count % 7 == 0 ? "A short review." : null, tmdb: item.ToString("N")));
            }
        }

        var seed = Stopwatch.StartNew();
        await _fx.Store.UpsertManyAsync(records, Ct);
        seed.Stop();
        Assert.Equal(50000, _fx.Store.Count().Attached);

        var random = new Random(7);
        var ids = items.OrderBy(_ => random.Next()).Take(100).ToList();
        _fx.Store.GetSummaryRows(ids);
        var times = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            var batch = items.OrderBy(_ => random.Next()).Take(100).ToList();
            var watch = Stopwatch.StartNew();
            var rows = _fx.Store.GetSummaryRows(batch);
            watch.Stop();
            Assert.Equal(1000, rows.Count);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        var median = times.Order().ElementAt(times.Count / 2);
        Assert.True(median < 50, $"The summary query took {median:F1} ms (median of 10) on 50,000 rows; the target is under 50 ms. Seed: {seed.Elapsed}.");
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private List<string> Names(string type)
        => _fx.Database.Read(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type ORDER BY name;";
            command.Parameters.AddWithValue("$type", type);
            using var reader = command.ExecuteReader();
            var names = new List<string>();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }

            return names.Where(n => !n.StartsWith("sqlite_", StringComparison.Ordinal) || n.StartsWith("sqlite_autoindex", StringComparison.Ordinal)).ToList();
        });

    private static List<string> FilesHolding(string marker, params string[] files)
    {
        var needle = System.Text.Encoding.UTF8.GetBytes(marker);
        return [.. files.Where(File.Exists).Where(f => StoreFixture.ReadOpenFile(f).AsSpan().IndexOf(needle) >= 0).Select(f => Path.GetFileName(f))];
    }

    private static string Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private Task<int> SeedWithKeysAsync(int count)
        => _fx.Store.UpsertManyAsync(Enumerable.Range(0, count).Select(i => StoreFixture.Movie(Guid.NewGuid(), Guid.NewGuid(), 1 + (i % 10), tmdb: (1000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList(), Ct);

    // Enough rows for Ratings to span many pages, so its root page is an interior page nothing else uses.
    private async Task<List<Jellyfin.Plugin.UserRatings.Domain.RatingRecord>> SeedBulkAsync(int count)
    {
        var records = Enumerable.Range(0, count).Select(i => StoreFixture.Movie(Guid.NewGuid(), Guid.NewGuid(), 1 + (i % 10), "Review " + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + " – Zoë 星 ✓", tmdb: (5000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
        await _fx.Store.UpsertManyAsync(records, Ct);
        return records;
    }
}
