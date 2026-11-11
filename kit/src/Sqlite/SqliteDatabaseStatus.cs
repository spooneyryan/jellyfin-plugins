namespace Jellyfin.Plugin.Kit.Sqlite;

/// <summary>
/// How a <see cref="SqliteDatabase"/> came up, or what it found since. Every status but
/// <see cref="Ok"/> and <see cref="Migrated"/> is shown as a banner on the plugin's settings page; the read-only ones also
/// stop every write, snapshot and background writer.
/// </summary>
internal enum SqliteDatabaseStatus
{
    /// <summary>The database was opened as it is, or created.</summary>
    Ok,

    /// <summary>The database was at an older schema version; a pre-operation snapshot was taken and it was upgraded.</summary>
    Migrated,

    /// <summary>
    /// The database failed its integrity check when it was opened (twice, the second time <c>PRAGMA integrity_check</c>
    /// on a fresh connection after a pause), or an admin's <i>Recover</i> restored it. It was moved into a
    /// <c>quarantine-&lt;ts&gt;</c> folder together with its <c>-wal</c> and <c>-shm</c>, and the newest snapshot that
    /// passed <c>PRAGMA integrity_check</c> was put in its place. Also when the database file was missing (or empty)
    /// while snapshots of its data existed. Changes made after that snapshot are missing.
    /// </summary>
    RestoredFromSnapshot,

    /// <summary>
    /// An admin chose to start empty (<c>RecoverAsync(null, startEmpty: true)</c>) while the database was
    /// <see cref="ReadOnlyCorrupt"/>: it was moved into a <c>quarantine-&lt;ts&gt;</c> folder with its sidecars and a new,
    /// empty database took its place. The kit never chooses this itself. The earlier snapshots are kept until one is
    /// restored.
    /// </summary>
    RecoveredEmpty,

    /// <summary>
    /// The database was written by a newer plugin version (its <c>user_version</c> is above what this build knows). It
    /// is opened read-only and nothing is written.
    /// </summary>
    ReadOnlyNewerVersion,

    /// <summary>
    /// Upgrading the database failed: a migration step threw, or the rebuild check found rows that break a foreign key.
    /// The transaction was rolled back, so the database is unchanged, and nothing is written.
    /// </summary>
    ReadOnlyMigrationFailed,

    /// <summary>
    /// The database turned out to be damaged while the server ran (<c>SQLITE_CORRUPT</c> or <c>SQLITE_NOTADB</c> on a
    /// query, or a failed <c>PRAGMA quick_check</c> in the daily maintenance); or it was damaged when it was opened and no
    /// snapshot passed its check, it couldn't be moved aside, or the snapshot couldn't be put in its place (then it was put
    /// back as it was found); or its file was missing (or empty) while snapshots of its
    /// data exist and none of them could be restored. Nothing was moved; undamaged tables can still be read, through
    /// read-only connections. Nothing is written until an admin restores a snapshot or chooses to start empty, or a
    /// restart restores the newest good snapshot.
    /// </summary>
    ReadOnlyCorrupt,

    /// <summary>
    /// The database couldn't be opened at all: the file is locked or can't be read (a check that couldn't read a page
    /// included, or a snapshot a restore at open needs that couldn't be read just then), or the server's
    /// Microsoft.Data.Sqlite couldn't be used by this build. Nothing was moved or changed; the next
    /// use after <c>OpenRetryInterval</c> tries again.
    /// </summary>
    Unavailable
}
