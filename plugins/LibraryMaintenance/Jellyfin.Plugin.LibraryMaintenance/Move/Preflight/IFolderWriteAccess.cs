namespace Jellyfin.Plugin.LibraryMaintenance.Move.Preflight;

/// <summary>
/// Tells, without writing anything, whether Jellyfin may add entries to a folder ("Destination writable" at
/// plan time). A plan-time check must not change the disk: a file made and deleted in a non-dot
/// folder changes that folder, and the library watcher's <c>Changed</c> event on it starts a <c>FileRefresher</c>.
/// </summary>
internal interface IFolderWriteAccess
{
    /// <summary>
    /// Gets whether files, folders or both may be added to an existing folder.
    /// </summary>
    /// <param name="directory">The folder (it exists, and isn't a link).</param>
    /// <param name="files">Whether files will be added (a file set's members).</param>
    /// <param name="folders">Whether folders will be added (a folder unit, a trickplay folder, a new folder).</param>
    /// <returns>True when the access is granted and the volume isn't read-only.</returns>
    bool CanAdd(string directory, bool files, bool folders);
}
