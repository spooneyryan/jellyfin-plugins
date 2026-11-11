namespace Jellyfin.Plugin.Kit.Sqlite;

/// <summary>
/// The user-facing messages of the errors a <see cref="SqliteDatabase"/> throws. Tests compare
/// against them. The read-only ones match the JSON store's wording where the situation is the same
/// (<c>StoreMessages</c>).
/// </summary>
internal static class SqliteMessages
{
    /// <summary>Every write while the database comes from a newer plugin version.</summary>
    public const string NewerVersion = "Data was written by a newer plugin version; install that version or restore an export.";

    /// <summary>Every write (and read) after a failed upgrade.</summary>
    public const string MigrationFailed = "Stored data couldn't be upgraded; nothing was changed. Install the previous plugin version or restore an export.";

    /// <summary>Every write, and a read that touched damaged data, while the database is damaged.</summary>
    public const string Corrupt = "The data is damaged, so nothing can be changed. Restore a snapshot from the plugin's settings page, or restart the server to restore the newest good snapshot automatically.";

    /// <summary>
    /// Every write while only the database's indexes are damaged (<c>integrity_check</c> finds index entries that don't
    /// match their tables, which read intact): the restart rebuilds them (<c>REINDEX</c>, after a copy of the database
    /// as found), and restores the newest good snapshot only if that doesn't repair it.
    /// </summary>
    public const string IndexesDamaged = "The data's indexes are damaged, so nothing can be changed. Restart the server to rebuild them (a copy of the data as it is now is kept first), or restore a snapshot from the plugin's settings page.";

    /// <summary>Any use while the database can't be opened at all.</summary>
    public const string Unavailable = "The database couldn't be opened. The server log has the details.";

    /// <summary>A read that failed on disk (an I/O error, for example a lock another program holds); nothing was changed.</summary>
    public const string ReadFailed = "The data couldn't be read just now; try again in a moment. The server log has the details.";

    /// <summary>A write that failed on disk (full, or an I/O error); nothing was changed.</summary>
    public const string WriteFailed = "The data couldn't be saved, so nothing was changed. The server log has the details.";

    /// <summary>
    /// A read, write or export while another connection kept the database locked for more than
    /// <see cref="SqliteDatabase.CommandTimeoutSeconds"/> (SQLITE_BUSY or SQLITE_LOCKED), and a restore that couldn't start
    /// because the database stayed in use; nothing was changed.
    /// </summary>
    public const string Busy = "The database is busy; try again in a moment.";

    /// <summary>Every read, write and export while the database file is missing (or empty) and none of its snapshots could be restored.</summary>
    public const string Missing = "The database file is missing and none of its snapshots could be restored, so nothing can be read or changed. Restore a snapshot, or start empty, from the plugin's settings page.";

    /// <summary>A corruption restore whose snapshot couldn't be read just then (in use, or a read error); nothing was changed.</summary>
    public const string SnapshotUnavailable = "A snapshot couldn't be read just now, so nothing was changed; try again in a moment. The server log has the details.";

    /// <summary>A corruption restore without a snapshot that passes its check, and without the admin's choice to start empty.</summary>
    public const string NoSnapshotPassed = "No snapshot passed its integrity check, so nothing was changed. To go on, start empty from the plugin's settings page (the damaged database is moved to a quarantine folder and the snapshots are kept), then import an export.";

    /// <summary>
    /// An admin's recovery whose check of the database in place, before anything is moved, couldn't read it just then
    /// (in use, a read error); nothing was changed.
    /// </summary>
    public const string RecheckFailed = "The database couldn't be checked again just now before the recovery, so nothing was changed; try again in a moment. The server log has the details.";

    /// <summary>An export after a failed upgrade: this version can't read the data faithfully.</summary>
    public const string ExportMigrationFailed = "Stored data couldn't be upgraded, so this version has nothing to export. The database and its snapshot from before the upgrade are unchanged and are the backup; install the previous plugin version to export them.";

    /// <summary>An export while the database comes from a newer plugin version, whose schema this version can't read faithfully.</summary>
    public const string ExportNewerVersion = "The data was written by a newer plugin version, so this version can't export it faithfully. The database and its snapshots are unchanged; install that version to export it.";

    /// <summary>An export while the database is damaged.</summary>
    public const string ExportCorrupt = "The data is damaged, so it can't be exported faithfully. Restore a snapshot first; the damaged database is kept in a quarantine folder.";

    /// <summary>
    /// What failed, for <see cref="RecoveryNotPutBack"/>, when the open finds the journal of a recovery the server
    /// didn't finish (it stopped or was killed during it) and can't put back what it moved.
    /// </summary>
    public const string InterruptedRecovery = "A recovery that was under way when the server stopped couldn't be finished";

    /// <summary>
    /// A restore (the automatic one at open, or an admin's) that needs a snapshot which couldn't be read just then: another
    /// program has it open (a sharing or lock violation), or its check couldn't read it. Nothing was changed, and it is
    /// tried again; the snapshot's file name only, never a path.
    /// </summary>
    /// <param name="snapshotName">The snapshot's file name.</param>
    /// <returns>The message.</returns>
    public static string SnapshotInUse(string snapshotName)
        => "The snapshot " + snapshotName + " couldn't be read just now (another program may have it open), so nothing was changed; it is tried again in a moment. The server log has the details.";

    /// <summary>
    /// A recovery that moved the database aside and then couldn't put a snapshot or a new empty database in its place:
    /// what it had moved is back where it was (as for the JSON store).
    /// </summary>
    /// <param name="failure">What failed, path-free ("The snapshot … couldn't be put in place of the database").</param>
    /// <param name="movedAny">Whether anything had been moved aside.</param>
    /// <returns>The message.</returns>
    public static string RecoveryPutBack(string failure, bool movedAny)
        => failure + (movedAny ? ", so what had been moved aside was put back as it was and nothing was changed." : ", so nothing was changed.") + " The server log has the details.";

    /// <summary>
    /// A recovery as <see cref="RecoveryPutBack"/> whose move back failed too: the files are in the quarantine folder named,
    /// next to the database, and nothing reads or creates the database until an admin's recovery or a restart.
    /// </summary>
    /// <param name="failure">What failed, path-free.</param>
    /// <param name="folder">The quarantine folder's name.</param>
    /// <returns>The message.</returns>
    public static string RecoveryNotPutBack(string failure, string folder)
        => failure + ", and what had been moved aside couldn't all be put back: it is in the folder " + folder + " next to the database. Restore a snapshot or start empty from the plugin's settings page, or move its files back by hand with the server stopped. The server log has the details.";

    /// <summary>
    /// A recovery that couldn't move the database (or, with no database, what was found in its place) aside: nothing
    /// was moved (the words don't say "the damaged database", since it may have been missing).
    /// </summary>
    /// <param name="damaged">Whether the database failed its check (else it was missing or empty).</param>
    /// <returns>The message.</returns>
    public static string MoveAsideFailed(bool damaged)
        => (damaged ? "The damaged database couldn't be moved aside" : "What was found in place of the database couldn't be moved aside")
            + ", so nothing was changed. The server log has the details.";

    /// <summary>
    /// The open found the journal of a recovery the server didn't finish while the database isn't there, and the journal
    /// can't be read, so where the files it moved went isn't known: nothing is created until an admin acts.
    /// </summary>
    /// <param name="journal">The journal's file name.</param>
    /// <returns>The message.</returns>
    public static string UnreadableRecoveryJournal(string journal)
        => "A recovery of the data didn't finish, and its journal " + journal + " can't be read, so nothing can be changed: what it moved aside is in a quarantine folder next to the database. Move those files back by hand with the server stopped and delete " + journal + ", or restore a snapshot or start empty from the plugin's settings page. The server log has the details.";

    /// <summary>
    /// The notice (<see cref="SqliteDatabase.Notice"/>) while a recovery that didn't finish has its files in a
    /// quarantine folder and a readable database that isn't the one it put there is in place (put back by hand), which
    /// is used as it is (not only a Warning in the log, so the admin learns where the data is).
    /// </summary>
    /// <param name="folder">The quarantine folder's name.</param>
    /// <returns>The message.</returns>
    public static string KeptAside(string folder)
        => "A recovery of the data didn't finish, and the database in place now was put there by hand, so it is used as it is. What that recovery had moved aside is still in the folder " + folder + " next to the database: check that the database in place holds the data you want, then delete or move the whole folder, never one file of it on its own. The server log has the details.";

    /// <summary>
    /// The notice (<see cref="SqliteDatabase.Notice"/>) while the quarantine folder of a recovery that didn't finish
    /// holds only the damaged database's -wal or -shm (its database was taken out of the folder by hand) and a readable
    /// database that isn't the one the recovery put there is in place: the log files stay in the folder, since beside
    /// any database SQLite would replay them into it.
    /// </summary>
    /// <param name="folder">The quarantine folder's name.</param>
    /// <returns>The message.</returns>
    public static string KeptAsideLogFiles(string folder)
        => "A recovery of the data didn't finish, and the folder " + folder + " next to the database still holds the damaged database's log files (-wal, -shm) without the database itself. They were left there, since next to any database they would change it, and the database in place is used as it is. Delete or move the whole folder. The server log has the details.";
}
