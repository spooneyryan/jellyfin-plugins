namespace Jellyfin.Plugin.Bookmarks.Model;

/// <summary>
/// Why the bookmark data is read-only, so a client can word its banner: "written by a
/// newer plugin version", "couldn't be upgraded; nothing was changed", "missing, and no snapshot could be restored" or
/// "couldn't be read, and no snapshot could be restored".
/// </summary>
/// <remarks>
/// Public because <c>BookmarkCapabilitiesDto.StoreReadOnlyReason</c> exposes it; prefixed, so its schema id can't collide.
/// Values are only ever added. The store's status is decided when it first loads and stays until the
/// server restarts, except that an admin can recover <see cref="DataMissing"/> and <see cref="DataUnreadable"/> data from
/// the settings page (restore a snapshot, or start empty), which makes it writable at once (Bookmarks 1.2).
/// </remarks>
public enum BookmarkStoreReadOnlyReason
{
    /// <summary>
    /// <c>bookmarks.json</c> was written by a newer Bookmarks (a higher schema version): its bookmarks are listed as far as
    /// this version can read them, and the file is never changed, until that version is installed again.
    /// </summary>
    NewerVersion,

    /// <summary>
    /// Upgrading <c>bookmarks.json</c> from an older Bookmarks failed: the file and its copy from before the upgrade are
    /// untouched, and this version shows no bookmarks until the previous version is installed or the file is fixed and the
    /// server restarted.
    /// </summary>
    MigrationFailed,

    /// <summary>
    /// <c>bookmarks.json</c> was missing (or empty) with no <c>.bak</c>, and its snapshots may hold the bookmarks but none of
    /// them could be restored (from a newer Bookmarks, or unreadable): nothing is written and the snapshots are left as they
    /// are until an admin restores a snapshot or starts empty on the settings page, or a readable file is put back, or that
    /// version installed, and the server restarted (kit store status <c>ReadOnlyDataMissing</c>).
    /// </summary>
    DataMissing,

    /// <summary>
    /// <c>bookmarks.json</c> (or its <c>.bak</c>) couldn't be read, and no readable <c>.bak</c> or snapshot holding bookmarks
    /// could take its place (Bookmarks 1.2; until then it started empty): nothing is written, and what couldn't be read is
    /// left as it is, until an admin restores a snapshot or starts empty on the settings page (it is kept as
    /// <c>bookmarks.json.corrupt-&lt;time&gt;</c> then), or a readable file is put back and the server restarted (kit store
    /// status <c>ReadOnlyDataUnreadable</c>).
    /// </summary>
    DataUnreadable
}
