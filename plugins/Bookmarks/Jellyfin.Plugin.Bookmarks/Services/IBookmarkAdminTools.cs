using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// The settings page's admin tools: export and import of the bookmark data, its snapshots and <i>Restore snapshot</i>,
/// running the daily maintenance now, and the check of a bookmark shortcut before it is saved. Public only so the admin
/// controller can take it; its results are plain dictionaries, like
/// the Diagnostics body.
/// </summary>
public interface IBookmarkAdminTools
{
    /// <summary>
    /// Exports the bookmark data: the kit's envelope with <c>bookmarks.json</c>'s whole document, the archive of
    /// bookmarks whose video has been gone longer than the retention, the users with their names, and the settings for
    /// reference, as an attachment that isn't cached.
    /// </summary>
    /// <param name="context">The request, for the file result's headers.</param>
    /// <returns>The file.</returns>
    /// <exception cref="Kit.Api.PluginException">503 when the data can't be read faithfully (an upgrade that failed) or the
    /// archive can't be read.</exception>
    IActionResult Export(HttpContext context);

    /// <summary>
    /// Imports an export: the request body, <c>application/json</c>, at most 100 MB. A dry run (the default) only
    /// reports. Records are matched by id: <c>Merge</c> adds new ones and replaces older copies, <c>Replace</c> makes the
    /// bookmarks the export's, after a snapshot. Clip files are kept only inside this server's clip folders.
    /// </summary>
    /// <param name="request">The request with the body.</param>
    /// <param name="mode"><c>Merge</c> (the default) or <c>Replace</c>.</param>
    /// <param name="dryRun">Whether to only report; true when left out.</param>
    /// <param name="mapUsersBy"><c>Id</c> (the default) or <c>Name</c>.</param>
    /// <returns>The report.</returns>
    /// <exception cref="Kit.Api.PluginException">400 not an export of Bookmarks or a bad option; 409 a newer schema, read-only
    /// data, users mapped by name while the list of users deleted on this server can't be read, or (not in a dry run) clips
    /// being made; 413 over 100 MB.</exception>
    Task<IDictionary<string, object?>> ImportAsync(HttpRequest request, string? mode, bool? dryRun, string? mapUsersBy);

    /// <summary>
    /// Lists the snapshots of <c>bookmarks.json</c>, the daily ones and those taken before an operation, newest first.
    /// </summary>
    /// <returns><c>{Snapshots:[{Name, Kind, Operation, Utc, Size, SchemaVersion, Kept}], KeepSnapshotsBeforeUtc, KeptBecause,
    /// SnapshotsToKeep, StoreReadOnly, StoreReadOnlyReason, Recoverable, ClipsBeingMade}</c>: <c>Recoverable</c> while the
    /// data is missing or unreadable, so a snapshot can be restored or the data started empty; <c>KeptBecause</c> how the data
    /// was found when the kept snapshots were recorded (<c>RecoveredEmpty</c>, <c>RestoredFromSnapshot</c>,
    /// <c>RecoveredFromBackup</c>, or null: none kept, or not said by an earlier version).</returns>
    IDictionary<string, object?> GetSnapshots();

    /// <summary>
    /// <i>Restore snapshot</i>: <c>bookmarks.json</c> takes a snapshot's data, after a snapshot of its own; the
    /// clip folders and clip files waiting to be deleted that the data knows now are kept, then the users are checked and
    /// the verify sweep runs. While the data is missing or unreadable with no snapshot restored on its own (read-only:
    /// <c>DataMissing</c>, <c>DataUnreadable</c>), this is the kit's recovery instead (Bookmarks 1.2): what couldn't be read
    /// is moved aside, the snapshot is restored with every snapshot kept, the clip jobs it holds are ended, and the data is
    /// writable at once; logged as a Warning with the admin's id.
    /// </summary>
    /// <param name="name">The snapshot's name from <see cref="GetSnapshots"/>.</param>
    /// <param name="adminId">The admin's user id, for the log.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <returns>What was restored, and the snapshot of the state it replaced (none in a recovery, which lists what it moved
    /// aside instead).</returns>
    /// <exception cref="Kit.Api.PluginException">400 no name; 404 no such snapshot; 409 a newer schema, an unreadable or empty
    /// snapshot, read-only data of a newer version or a failed upgrade, or clips being made; 503 the snapshot couldn't be
    /// read just now, or the write failed.</exception>
    Task<IDictionary<string, object?>> RestoreSnapshotAsync(string? name, Guid adminId, CancellationToken cancellationToken);

    /// <summary>
    /// Starts the bookmark data empty, as an admin chooses, while it is missing or unreadable with no snapshot restored on its
    /// own (<c>DataMissing</c>, <c>DataUnreadable</c>; the kit's recovery, Bookmarks 1.2): what couldn't be read is kept as
    /// <c>bookmarks.json.corrupt-&lt;time&gt;</c>, every snapshot is kept until one is restored, and the data is empty and
    /// writable at once; then the users are checked. Logged as a Warning with the admin's id. The store never does this on
    /// its own.
    /// </summary>
    /// <param name="adminId">The admin's user id, for the log.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><c>{StartedEmpty, MovedAside, KeepSnapshotsBeforeUtc, Bookmarks}</c>.</returns>
    /// <exception cref="Kit.Api.PluginException">409 the data isn't missing or unreadable, or a readable bookmarks.json (or
    /// .bak) is in place now; 503 the write failed.</exception>
    Task<IDictionary<string, object?>> StartEmptyAsync(Guid adminId, CancellationToken cancellationToken);

    /// <summary>
    /// Starts the list of users deleted on this server (<c>kit-users.json</c>) empty, as an admin chooses, while it can't be
    /// read (Diagnostics' <c>Users.Marks.Recoverable</c>; since Bookmarks 1.2): what couldn't be
    /// read is kept as <c>kit-users.json.corrupt-&lt;time&gt;</c>, the marks made since the server started are kept, and the
    /// users are checked at once, so the list of users this server doesn't know is rebuilt from the server's users. Marks of
    /// users deleted while it couldn't be read may be lost. Logged as a Warning with the admin's id.
    /// </summary>
    /// <param name="adminId">The admin's user id, for the log.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><c>{StartedEmpty, MovedAside, MarksKept, Orphans}</c>.</returns>
    /// <exception cref="Kit.Api.PluginException">409 the list isn't unreadable, or a readable copy is in place now; 503 the
    /// write failed.</exception>
    Task<IDictionary<string, object?>> StartUserMarksEmptyAsync(Guid adminId, CancellationToken cancellationToken);

    /// <summary>
    /// Queues the daily "Bookmarks: Maintenance" task to run now (<c>Admin/Maintenance/Run</c>).
    /// </summary>
    /// <returns><c>{Queued, Task}</c>.</returns>
    /// <exception cref="Kit.Api.PluginException">503 when the server's task manager isn't there.</exception>
    IDictionary<string, object?> RunMaintenance();

    /// <summary>
    /// Checks a bookmark shortcut before it is saved: the combination it is in the kit's notation, and
    /// who else acts on it (jellyfin-web's video player, Jellyfin Enhanced's keys as JE has them now, a user's own JE keys),
    /// with the sentence the settings page shows under the field.
    /// </summary>
    /// <param name="combo">What was typed; empty for no shortcut.</param>
    /// <returns><c>{Name, Label, Combo, Valid, Reserved, Warning, Reservations}</c>.</returns>
    IDictionary<string, object?> CheckShortcut(string? combo);

    /// <summary>
    /// Checks a clip folder as the admin types it on the settings page (since Bookmarks 1.2.1): the folder it is (the default
    /// one for an empty field), whether it is a folder path, and a warning (<c>InVideoLibrary</c>) when it lies in a Movies or
    /// TV library, naming the library for an admin who may open it.
    /// </summary>
    /// <param name="path">The field's value.</param>
    /// <param name="adminId">The admin who asks.</param>
    /// <returns><c>{Path, Valid, Warnings}</c>.</returns>
    IDictionary<string, object?> CheckClipFolder(string? path, Guid adminId);

    /// <summary>
    /// "This is the clip folder; its clips are gone": confirms that a recorded clip folder
    /// the missing-clip sweep left out (Diagnostics' <c>ClipSweep</c> names it with its <c>RootId</c>) is the folder its clips
    /// were made in, and that those of its clips whose file isn't there are gone. The folder is made when it isn't there and
    /// given its marker file, and those clips are marked missing now, so the retention takes them as any. A dry run (the
    /// default) only counts. Not in a dry run: after a snapshot, logged with the admin's id.
    /// </summary>
    /// <param name="rootId">The folder's <c>RootId</c>.</param>
    /// <param name="dryRun">Whether to only count; true when left out.</param>
    /// <param name="adminId">The admin's user id, for the log.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><c>{DryRun, Folder, RootId, Existed, Created, Marker, Clips, Present, Gone, MarkedBefore, Marked}</c>.</returns>
    /// <exception cref="Kit.Api.PluginException">404 for an id no recorded clip folder has; 409 read-only data; 503 when the
    /// folder couldn't be made or its marker couldn't be written.</exception>
    Task<IDictionary<string, object?>> ConfirmClipFolderAsync(string? rootId, bool? dryRun, Guid adminId, CancellationToken cancellationToken);
}
