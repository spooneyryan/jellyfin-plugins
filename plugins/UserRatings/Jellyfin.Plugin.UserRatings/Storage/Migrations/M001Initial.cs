using Jellyfin.Plugin.Kit.Sqlite;

namespace Jellyfin.Plugin.UserRatings.Storage.Migrations;

/// <summary>
/// <c>M001_Initial</c>, schema version 1 of <c>userratings.db</c>: the ratings with their identity snapshot and
/// container ids, the normalised provider keys, former users, the moderation log, the import ledger and its rows, the
/// deletion tombstones and the plugin's own key-value meta. CHECK constraints guard only fixed invariants (the star range,
/// booleans, the text length, the row's shape); the growable enums (<c>Origin</c>, <c>DetachReason</c>,
/// <c>ModerationLog.Action</c>, <c>Tombstones.DeletedBy</c>) are validated by the store (<see cref="RatingValues"/>), so
/// adding a value never needs a table rebuild.
/// </summary>
internal static class M001Initial
{
    /// <summary>The schema version this step leaves (<c>PRAGMA user_version</c>).</summary>
    public const int Version = 1;

    /// <summary>The step's name, in logs and in Diagnostics.</summary>
    public const string Name = "M001_Initial";

    /// <summary>The step's SQL, run in the kit's upgrade transaction.</summary>
    public const string Sql =
        """
        CREATE TABLE Ratings (
          Id                TEXT    PRIMARY KEY,
          UserId            TEXT    NOT NULL,
          ItemId            TEXT    NOT NULL,
          Stars2            INTEGER NULL CHECK (Stars2 BETWEEN 1 AND 10),
          ReviewText        TEXT    NULL CHECK (ReviewText IS NULL OR length(ReviewText) BETWEEN 1 AND 20000),
          IsSpoiler         INTEGER NOT NULL DEFAULT 0 CHECK (IsSpoiler IN (0, 1)),
          CreatedUtc        TEXT    NOT NULL,
          UpdatedUtc        TEXT    NOT NULL,
          ReviewUpdatedUtc  TEXT    NULL,
          EditCount         INTEGER NOT NULL DEFAULT 0,
          Origin            TEXT    NOT NULL DEFAULT 'User',
          ItemKind          TEXT    NOT NULL,
          ItemName          TEXT    NULL,
          ProductionYear    INTEGER NULL,
          ProviderIds       TEXT    NULL,
          SeriesProviderIds TEXT    NULL,
          SeriesName        TEXT    NULL,
          SeasonNumber      INTEGER NULL,
          EpisodeNumber     INTEGER NULL,
          ItemPath          TEXT    NULL,
          SeriesId          TEXT    NULL,
          SeasonId          TEXT    NULL,
          AlbumId           TEXT    NULL,
          DetachedUtc       TEXT    NULL,
          DetachReason      TEXT    NULL,
          MirroredValue     REAL    NULL,
          CHECK (Stars2 IS NOT NULL OR ReviewText IS NOT NULL),
          CHECK ((DetachedUtc IS NULL) = (DetachReason IS NULL))
        );
        CREATE UNIQUE INDEX UX_Ratings_User_Item ON Ratings(UserId, ItemId) WHERE DetachedUtc IS NULL;
        CREATE INDEX IX_Ratings_Item     ON Ratings(ItemId)           WHERE DetachedUtc IS NULL;
        CREATE INDEX IX_Ratings_User     ON Ratings(UserId, UpdatedUtc);
        CREATE INDEX IX_Ratings_Recent   ON Ratings(ReviewUpdatedUtc) WHERE ReviewText IS NOT NULL AND DetachedUtc IS NULL;
        CREATE INDEX IX_Ratings_Series   ON Ratings(SeriesId)         WHERE SeriesId IS NOT NULL;
        CREATE INDEX IX_Ratings_Season   ON Ratings(SeasonId)         WHERE SeasonId IS NOT NULL;
        CREATE INDEX IX_Ratings_Album    ON Ratings(AlbumId)          WHERE AlbumId IS NOT NULL;
        CREATE INDEX IX_Ratings_Detached ON Ratings(DetachedUtc)      WHERE DetachedUtc IS NOT NULL;

        CREATE TABLE RatingKeys (
          RatingId TEXT NOT NULL REFERENCES Ratings(Id) ON DELETE CASCADE,
          Key      TEXT NOT NULL,
          PRIMARY KEY (RatingId, Key)
        );
        CREATE INDEX IX_RatingKeys_Key ON RatingKeys(Key);

        CREATE TABLE FormerUsers (
          Id             TEXT PRIMARY KEY,
          OriginalUserId TEXT NOT NULL,
          DisplayName    TEXT NOT NULL,
          DeletedUtc     TEXT NOT NULL
        );

        CREATE TABLE ModerationLog (
          Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
          AtUtc              TEXT NOT NULL,
          ActorUserId        TEXT NULL,
          Action             TEXT NOT NULL,
          RatingId           TEXT NULL,
          TargetUserId       TEXT NULL,
          ItemId             TEXT NULL,
          Reason             TEXT NULL,
          TextSnapshot       TEXT NULL,
          SnapshotExpiresUtc TEXT NULL,
          Details            TEXT NULL
        );
        CREATE INDEX IX_ModLog_At ON ModerationLog(AtUtc);

        CREATE TABLE ImportLedger (
          Source           TEXT NOT NULL,
          SourceKey        TEXT NOT NULL,
          SourceUpdatedUtc TEXT NOT NULL,
          ImportedUtc      TEXT NOT NULL,
          PRIMARY KEY (Source, SourceKey)
        );
        CREATE TABLE ImportLedgerRows (
          Source    TEXT NOT NULL,
          SourceKey TEXT NOT NULL,
          RatingId  TEXT NOT NULL,
          PRIMARY KEY (Source, SourceKey, RatingId)
        );
        CREATE INDEX IX_ImportLedgerRows_Rating ON ImportLedgerRows(RatingId);

        CREATE TABLE Tombstones (
          RatingId   TEXT PRIMARY KEY,
          UserId     TEXT NOT NULL,
          ItemId     TEXT NOT NULL,
          DeletedUtc TEXT NOT NULL,
          DeletedBy  TEXT NOT NULL
        );

        CREATE TABLE Meta (
          Key   TEXT PRIMARY KEY,
          Value TEXT NOT NULL
        );
        """;

    /// <summary>
    /// Gets the step for the kit's runner.
    /// </summary>
    public static SqliteMigration Migration { get; } = new(Version, Name, Sql);
}
