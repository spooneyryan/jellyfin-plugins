namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// How a store came up. Every status but <see cref="Ok"/> and <see cref="Migrated"/> is shown as a banner on
/// the plugin's settings page; the read-only statuses also stop every write, snapshot and background writer.
/// </summary>
internal enum StoreStatus
{
    /// <summary>The file was read as it is, or there was none yet (and no snapshot holding data).</summary>
    Ok,

    /// <summary>The file was at an older schema version; it was copied to <c>.v&lt;N&gt;-&lt;ts&gt;</c>, upgraded and saved.</summary>
    Migrated,

    /// <summary>
    /// The file was missing or unreadable (and moved aside as <c>.corrupt-&lt;ts&gt;</c>); the previous version, the
    /// <c>.bak</c> file, was used. The most recent change may be missing.
    /// </summary>
    RecoveredFromBackup,

    /// <summary>
    /// The store started empty with nothing lost: the file and its <c>.bak</c> held no data (no bytes or white space only;
    /// moved aside as <c>.corrupt-&lt;ts&gt;</c>) and no snapshot holds any, or an admin chose to start empty from a
    /// read-only state (<see cref="JsonDocumentStore{TDoc}.RecoverAsync"/>). It never starts empty on its own where data may
    /// be lost (the SQLite database's rule): an unreadable file is <see cref="ReadOnlyDataUnreadable"/>. The snapshots taken
    /// before are kept until one is restored (<c>.keep-snapshots</c>).
    /// </summary>
    RecoveredEmpty,

    /// <summary>
    /// The file was missing, empty (no bytes or white space only) or unreadable, and there was no usable <c>.bak</c> (none,
    /// or an empty or unreadable one), while snapshots of its data existed: the first snapshot that could be read and held
    /// data (the newest; after a restore, the ones taken since it first; those dated more than a day after now last) was
    /// restored, given to the plugin's step after a restore, and saved, the file and <c>.bak</c> found moved aside first as
    /// <c>.corrupt-&lt;ts&gt;</c>. Or an admin restored a snapshot from a read-only state
    /// (<see cref="JsonDocumentStore{TDoc}.RecoverAsync"/>). Changes made after that snapshot are missing; every snapshot
    /// there was, daily and pre-operation, is kept until one is restored or the marker is deleted (<c>.keep-snapshots</c>).
    /// </summary>
    RestoredFromSnapshot,

    /// <summary>
    /// The file was written by a newer plugin version. Its data is shown as far as this version can read it, but nothing
    /// is written.
    /// </summary>
    ReadOnlyNewerVersion,

    /// <summary>
    /// Upgrading the file failed: a migration step threw, or the upgraded data couldn't be read. The file is untouched and
    /// the store serves an empty document without writing anything.
    /// </summary>
    ReadOnlyMigrationFailed,

    /// <summary>
    /// The file was missing or empty with no <c>.bak</c> (or an empty one), and snapshots that may hold its data exist, but
    /// none of them could be restored (a newer schema version, unreadable, or no permission to read them). Nothing is
    /// written and the snapshots are left as they are (an empty file or <c>.bak</c> has still been moved aside as
    /// <c>.corrupt-&lt;ts&gt;</c>); the store serves an empty document read-only until an admin restores a snapshot or starts
    /// empty (<see cref="JsonDocumentStore{TDoc}.RecoverAsync"/>), or a readable file is put back and the server restarted.
    /// </summary>
    ReadOnlyDataMissing,

    /// <summary>
    /// The file, or its <c>.bak</c> beside a missing or empty file, holds something that can't be read (not JSON, or not
    /// this schema version's data), and nothing could take its place: no readable <c>.bak</c>, and no snapshot that could
    /// be read and held data. The data may be in what couldn't be read, so the store never starts empty on its own (the
    /// SQLite database's rule; until Bookmarks 1.2 this was <see cref="RecoveredEmpty"/>): nothing unreadable is moved or
    /// written, the snapshots are left as they are, and the store serves an empty document read-only until an admin
    /// restores a snapshot or starts empty (<see cref="JsonDocumentStore{TDoc}.RecoverAsync"/>, which moves what couldn't be
    /// read aside as <c>.corrupt-&lt;ts&gt;</c> first), or a readable file is put back and the server restarted.
    /// </summary>
    ReadOnlyDataUnreadable
}
