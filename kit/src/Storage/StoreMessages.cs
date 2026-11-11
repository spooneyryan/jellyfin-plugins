namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// The user-facing messages of the 503 errors a store throws. Tests compare against them.
/// </summary>
internal static class StoreMessages
{
    /// <summary>Every write while the file comes from a newer plugin version (<see cref="StoreStatus.ReadOnlyNewerVersion"/>).</summary>
    public const string NewerVersion = "Data was written by a newer plugin version; install that version or restore an export.";

    /// <summary>Every write after a failed upgrade (<see cref="StoreStatus.ReadOnlyMigrationFailed"/>).</summary>
    public const string MigrationFailed = "Stored data couldn't be upgraded; nothing was changed. Install the previous plugin version or restore an export.";

    /// <summary>Every write while the file is missing and no snapshot of it could be restored (<see cref="StoreStatus.ReadOnlyDataMissing"/>).</summary>
    public const string DataMissing = "Stored data is missing and none of its snapshots could be restored; nothing was changed. The plugin's settings page says what to do.";

    /// <summary>An export while the file is missing and no snapshot of it could be restored (<see cref="StoreStatus.ReadOnlyDataMissing"/>).</summary>
    public const string ExportDataMissing = "Stored data is missing and none of its snapshots could be restored, so there is nothing to export. The snapshots are unchanged and are the backup.";

    /// <summary>Every write while the file (or its <c>.bak</c>) can't be read and no snapshot could take its place (<see cref="StoreStatus.ReadOnlyDataUnreadable"/>).</summary>
    public const string DataUnreadable = "Stored data couldn't be read and none of its snapshots could be restored; nothing was changed. The plugin's settings page says what to do.";

    /// <summary>An export while the file can't be read and no snapshot could take its place (<see cref="StoreStatus.ReadOnlyDataUnreadable"/>).</summary>
    public const string ExportDataUnreadable = "Stored data couldn't be read and none of its snapshots could be restored, so there is nothing to export. The data file and the snapshots are unchanged and are the backup.";

    /// <summary>An admin's recovery while the data is there: any status but <see cref="StoreStatus.ReadOnlyDataMissing"/> and <see cref="StoreStatus.ReadOnlyDataUnreadable"/>.</summary>
    public const string NothingToRecover = "The data isn't missing or unreadable, so there is nothing to recover. To go back to a snapshot, restore it instead.";

    /// <summary>A write that failed on disk; memory and file are unchanged.</summary>
    public const string WriteFailed = "The data couldn't be saved, so nothing was changed. The server log has the details.";

    /// <summary>Any use while the file exists but can't be read at all (locked, no access).</summary>
    public const string ReadFailed = "The data file couldn't be read. The server log has the details.";

    /// <summary>
    /// Any use while the data file, or its <c>.bak</c>, which couldn't be read (or held nothing), couldn't be moved
    /// aside for its backup or a snapshot to take its place; what the attempt had moved is put back.
    /// </summary>
    public const string MoveAsideFailed = "The data file or its backup couldn't be moved aside to put the data back in its place; it is tried again at the next use. The server log has the details.";

    /// <summary>
    /// Any use while the data needs an upgrade and the copy of it kept from before the upgrade
    /// (<c>.v&lt;N&gt;-&lt;ts&gt;</c>) couldn't be written: nothing is upgraded without it.
    /// </summary>
    public const string UpgradeCopyFailed = "The data needs an upgrade, and the copy of it kept from before the upgrade couldn't be written, so nothing was changed; it is tried again at the next use. The server log has the details.";

    /// <summary>An export after a failed upgrade (<see cref="StoreStatus.ReadOnlyMigrationFailed"/>): the store holds nothing faithful.</summary>
    public const string ExportMigrationFailed = "Stored data couldn't be upgraded, so this version has nothing to export. The data file and its copy from before the upgrade are unchanged and are the backup; install the previous plugin version to export them.";

    /// <summary>An export while the file comes from a newer plugin version and isn't a JSON object this version can pass on.</summary>
    public const string ExportNewerUnreadable = "The data was written by a newer plugin version and couldn't be exported by this one. The data file is unchanged; install that version to export it.";

    /// <summary>
    /// Any use while the file is missing (or was empty, or can't be read) and the snapshot the store restores from is in use
    /// just now (<see cref="StoreStatus.RestoredFromSnapshot"/> once it can be read). Names the snapshot's file, never its
    /// path.
    /// </summary>
    /// <param name="snapshot">The snapshot's file name.</param>
    /// <returns>The message.</returns>
    public static string SnapshotReadFailed(string snapshot)
        => "The data file is missing or can't be read, and its snapshot " + snapshot + " couldn't be read just now to restore it; it is tried again at the next use. The server log has the details.";

    /// <summary>
    /// An admin's recovery while a readable data file or <c>.bak</c> is in place (put back while the server runs): it
    /// would be moved aside as unreadable, so nothing is done, and a restart uses it.
    /// </summary>
    /// <param name="file">The file's name.</param>
    /// <returns>The message.</returns>
    public static string ReadableFileInPlace(string file)
        => "A readable data file is in place now (" + file + "); restart the server to use it. Nothing was changed.";

    /// <summary>
    /// What a user who isn't an administrator is told instead of the read-only 503s (<see cref="NewerVersion"/>,
    /// <see cref="MigrationFailed"/>, <see cref="DataMissing"/>, <see cref="DataUnreadable"/>), which send an admin to
    /// the plugin's settings page or to install a version (<c>PluginException.NonAdminMessage</c>).
    /// </summary>
    /// <param name="status">The store's read-only status.</param>
    /// <returns>The message.</returns>
    public static string ReadOnlyForUsers(StoreStatus status) => status switch
    {
        StoreStatus.ReadOnlyNewerVersion => "The data was written by a newer plugin version, so nothing can be changed right now. Ask your server's administrator.",
        StoreStatus.ReadOnlyMigrationFailed => "The data couldn't be upgraded, so nothing can be changed right now. Ask your server's administrator.",
        StoreStatus.ReadOnlyDataMissing => "The data is missing, so nothing can be changed right now. Ask your server's administrator.",
        StoreStatus.ReadOnlyDataUnreadable => "The data couldn't be read, so nothing can be changed right now. Ask your server's administrator.",
        _ => "The data is read-only right now, so nothing can be changed. Ask your server's administrator."
    };
}
