using System.Collections.Generic;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Import;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Storage;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jellyfin.Plugin.Bookmarks.Api;

/// <summary>
/// Bookmarks' admin endpoints: Diagnostics, and since 1.1.0 the actions on records of users and videos that are gone
/// (<i>Remap by name</i>, <i>Apply deleted-user policy</i>, <i>Purge</i>), the Jellyfin Enhanced bookmark import, and
/// the settings page's tools: export and import of the bookmark data, its snapshots and
/// <i>Restore snapshot</i>, the daily maintenance run now, and the bookmark shortcut's check; since Bookmarks 1.2 the way
/// out of missing or unreadable data (<i>Restore snapshot</i> then, and <i>Start empty</i>).
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Bookmarks/Admin")]
[Produces(MediaTypeNames.Application.Json)]
[KitAbiGate]
[PluginExceptionFilter]
public class BookmarksAdminController : ControllerBase
{
    private readonly IKitDiagnostics _diagnostics;
    private readonly IBookmarkRecordsAdmin _records;
    private readonly IBookmarkJeImport _jeImport;
    private readonly IBookmarkAdminTools _tools;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarksAdminController"/> class.
    /// </summary>
    /// <param name="diagnostics">The kit's diagnostics.</param>
    /// <param name="records">The admin actions on records of users and videos that are gone.</param>
    /// <param name="jeImport">The Jellyfin Enhanced bookmark import.</param>
    /// <param name="tools">The settings page's tools: export, import, snapshots, maintenance, the shortcut check.</param>
    public BookmarksAdminController(IKitDiagnostics diagnostics, IBookmarkRecordsAdmin records, IBookmarkJeImport jeImport, IBookmarkAdminTools tools)
    {
        _diagnostics = diagnostics;
        _records = records;
        _jeImport = jeImport;
        _tools = tools;
    }

    /// <summary>
    /// Gets the plugin's diagnostics: <c>PluginVersion</c>, <c>KitVersion</c>, <c>Abi</c> (the Jellyfin line
    /// the build is for and whether it runs), <c>Store</c> (the bookmark file, its schema version and status),
    /// <c>Pages</c> (the user pages and their hosts), <c>WebAnchors</c> (the jellyfin-web elements the web client relies
    /// on, whether reports are recorded, and how many were accepted or dropped), <c>ClientHealth</c> (the missing elements
    /// browsers reported since the server started), and the entries of the kit and of Bookmarks: <c>ClipStorage</c>,
    /// <c>Users</c>, <c>BookmarkCounts</c> (each user's bookmarks counted, never shown), <c>JellyfinEnhanced</c>,
    /// <c>JeImport</c>, <c>Identity</c>, <c>LibraryChanges</c>, <c>Verifier</c>, <c>Detached</c> and <c>Maintenance</c>.
    /// </summary>
    /// <response code="200">The diagnostics, as JSON.</response>
    /// <returns>The diagnostics.</returns>
    [HttpGet("Diagnostics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IDictionary<string, object?>> GetBookmarksDiagnostics() => Ok(_diagnostics.Describe());

    /// <summary>
    /// <i>Remap by name</i>: each user id Diagnostics lists under <c>Users.OrphanUsers</c> whose last seen
    /// name is one user's name on this server gets that user's id, bookmarks and all, after a snapshot. Users deleted on
    /// this server are never remapped. A dry run (the default) only reports.
    /// </summary>
    /// <param name="by">How to match: <c>Name</c> (the only way).</param>
    /// <param name="dryRun">Whether to only report; true when left out.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The report.</response>
    /// <response code="400">Another way to match.</response>
    /// <response code="409">The bookmark data is read-only, or the list of users deleted on this server (kit-users.json) can't be read (a dry run too).</response>
    /// <returns>The remap report.</returns>
    [HttpPost("Users/Remap")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IDictionary<string, object?>>> RemapBookmarksUsers([FromQuery] string? by, [FromQuery] bool? dryRun, CancellationToken cancellationToken)
        => Ok(await _records.RemapUsersAsync(by, dryRun, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// <i>Apply deleted-user policy</i> to unknown user ids from <c>Users.OrphanUsers</c>: Bookmarks'
    /// <c>DeletedUserPolicy</c> (<c>KeepAsOrphan</c>: bookmarks without a clip file deleted, those with one kept as clip-only
    /// tombstones), after a snapshot.
    /// </summary>
    /// <param name="request">The user ids.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The report.</response>
    /// <response code="400">No ids, too many, or a current user's id.</response>
    /// <response code="409">The bookmark data is read-only.</response>
    /// <response code="503">The server's user list can't be read or is empty, so a current user couldn't be told from a
    /// deleted one (the shared kit's check); nothing was changed.</response>
    /// <returns>The policy report.</returns>
    [HttpPost("Users/ApplyPolicy")]
    [RequestSizeLimit(BookmarksController.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IDictionary<string, object?>>> ApplyBookmarksDeletedUserPolicy([FromBody] BookmarkApplyPolicyRequest request, CancellationToken cancellationToken)
        => Ok(await _records.ApplyDeletedUserPolicyAsync(request, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// <i>Purge</i>: deletes detached bookmarks (their video left the library and never came back) for
    /// good, after a snapshot: the only hard delete of detached bookmarks. Bookmarks with a clip file are kept.
    /// </summary>
    /// <param name="olderThanDays">Only bookmarks detached at least this many days ago; every detached one when left out.</param>
    /// <param name="includeArchive">Whether archived bookmarks go too.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">What was deleted.</response>
    /// <response code="400">A negative age.</response>
    /// <response code="409">The bookmark data is read-only.</response>
    /// <response code="503">The archive can't be read.</response>
    /// <returns>The purge report.</returns>
    [HttpPost("Detached/Purge")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IDictionary<string, object?>>> PurgeBookmarksDetached([FromQuery] int? olderThanDays, [FromQuery] bool includeArchive, CancellationToken cancellationToken)
        => Ok(await _records.PurgeDetachedAsync(olderThanDays, includeArchive, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Imports Jellyfin Enhanced's bookmarks: every user's JE <c>bookmark.json</c>, read-only, each
    /// bookmark on the video of its owner's libraries that JE's item id names, else the one with its TMDb or TVDb id (the
    /// first of several, reported as ambiguous), else kept as "Not in library"; names and positions kept, duplicates and
    /// JE's cross-version copies left out. A dry run (the default) only reports; the import writes the bookmarks with the
    /// import ledger in one write, after a snapshot, so running it again changes nothing, and its report carries the
    /// switch-off checklist once every JE user with bookmarks is imported. JE's files and settings are never changed.
    /// </summary>
    /// <param name="dryRun">Whether to only report; true when left out.</param>
    /// <param name="request">Which users (optional): <c>{"UserIds": [...]}</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The report: per user what was found and done, up to 50 samples, and after an import the checklist.</response>
    /// <response code="400">More than 1000 or empty user ids.</response>
    /// <response code="404">Jellyfin Enhanced has no bookmarks on this server (for those users).</response>
    /// <response code="503">Jellyfin Enhanced's data folder can't be listed, or (not in a dry run) the bookmark data is read-only.</response>
    /// <returns>The import report.</returns>
    [HttpPost("Import/JellyfinEnhanced")]
    [RequestSizeLimit(BookmarksController.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<BookmarkJeImportReportDto>> ImportBookmarksFromJellyfinEnhanced(
        [FromQuery] bool? dryRun,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] BookmarkJeImportRequest? request,
        CancellationToken cancellationToken)
        => Ok(await _jeImport.ImportAsync(dryRun ?? true, request, User.GetUserId(), cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Exports the bookmark data: every user's bookmarks, the clip-only tombstones, the naming preferences, the
    /// Jellyfin Enhanced import ledger and the archive, with the users' names and the settings for reference, as the kit's
    /// envelope (<c>jellyfin-plugin-export/1</c>), an attachment that isn't cached. It holds names and notes: keep it safe.
    /// </summary>
    /// <response code="200">The export file.</response>
    /// <response code="503">The bookmark data or its archive can't be read faithfully (an upgrade that failed).</response>
    /// <returns>The file.</returns>
    [HttpGet("Export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult ExportBookmarks() => _tools.Export(HttpContext);

    /// <summary>
    /// Imports an export of the bookmark data: the request body, <c>application/json</c>, at most 100 MB. A dry
    /// run (the default) only reports. Records are matched by id: <c>Merge</c> (the default) adds new bookmarks and
    /// replaces older copies, <c>Replace</c> makes the bookmarks the export's, after a snapshot. Users are matched by
    /// <c>Id</c> (the default) or <c>Name</c>; bookmarks of users not found are skipped. Clip files are kept only inside
    /// this server's clip folders; settings are never applied.
    /// </summary>
    /// <param name="mode"><c>Merge</c> or <c>Replace</c>.</param>
    /// <param name="dryRun">Whether to only report; true when left out.</param>
    /// <param name="mapUsersBy"><c>Id</c> or <c>Name</c>.</param>
    /// <response code="200">The report.</response>
    /// <response code="400">Not an export of Bookmarks, or a bad option.</response>
    /// <response code="409">A newer schema, read-only data, users matched by name while the list of users deleted on this server can't be read, or (not in a dry run) clips being made.</response>
    /// <response code="413">Over 100 MB.</response>
    /// <returns>The import report.</returns>
    [HttpPost("Import")]
    [RequestSizeLimit(ExportEnvelope.MaxImportBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<IDictionary<string, object?>>> ImportBookmarks([FromQuery] string? mode, [FromQuery] bool? dryRun, [FromQuery] string? mapUsersBy)
        => Ok(await _tools.ImportAsync(Request, mode, dryRun, mapUsersBy).ConfigureAwait(false));

    /// <summary>
    /// Lists the snapshots of the bookmark data, newest first: the daily ones (<c>SnapshotsToKeep</c>) and
    /// those taken before an operation (an import, a restore, a deleted user's policy, <i>Remap by name</i>, a purge).
    /// </summary>
    /// <response code="200">The snapshots.</response>
    /// <returns>The list.</returns>
    [HttpGet("Snapshots")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IDictionary<string, object?>> GetBookmarksSnapshots() => Ok(_tools.GetSnapshots());

    /// <summary>
    /// <i>Restore snapshot</i>: the bookmark data becomes a snapshot's, after a snapshot of the state it
    /// replaces; the clip folders and the clip files waiting to be deleted it knows now are kept. Then the users are checked
    /// and the verify sweep runs. While the data is missing or unreadable and no snapshot was restored on its own
    /// (<c>Snapshots</c>' <c>Recoverable</c>), it is the way out instead (Bookmarks 1.2): what couldn't be read is kept as
    /// <c>bookmarks.json.corrupt-&lt;time&gt;</c>, every snapshot is kept, and the data is writable at once.
    /// </summary>
    /// <param name="name">The snapshot's name from <c>Snapshots</c>.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <response code="200">What was restored, and the snapshot of the state it replaced (or, in a recovery, what was moved aside).</response>
    /// <response code="400">No name.</response>
    /// <response code="404">No snapshot of the bookmark data has that name.</response>
    /// <response code="409">A newer schema, an unreadable or empty snapshot, data that is read-only for another reason, a readable bookmarks.json put back while the server runs (in a recovery), or clips being made.</response>
    /// <response code="503">The snapshot couldn't be read just now, or the data couldn't be saved; nothing was changed.</response>
    /// <returns>The restore.</returns>
    [HttpPost("Snapshots/Restore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IDictionary<string, object?>>> RestoreBookmarksSnapshot([FromQuery] string? name, CancellationToken cancellationToken)
        => Ok(await _tools.RestoreSnapshotAsync(name, User.GetUserId(), cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Starts the bookmark data empty (Bookmarks 1.2), as only an admin can choose: while it is missing or unreadable and no
    /// snapshot could be restored on its own (<c>Snapshots</c>' <c>Recoverable</c>; Capabilities' <c>StoreReadOnlyReason</c>
    /// <c>DataMissing</c> or <c>DataUnreadable</c>). What couldn't be read is kept as
    /// <c>bookmarks.json.corrupt-&lt;time&gt;</c>, every snapshot is kept until one of them is restored, and the data is empty
    /// and writable at once. Then the users are checked.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200"><c>{StartedEmpty, MovedAside, KeepSnapshotsBeforeUtc, Bookmarks}</c>.</response>
    /// <response code="409">The bookmark data isn't missing or unreadable, or a readable bookmarks.json is in place now (restart the server to use it).</response>
    /// <response code="503">The data couldn't be saved; nothing was changed.</response>
    /// <returns>What was done.</returns>
    [HttpPost("Data/StartEmpty")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IDictionary<string, object?>>> StartBookmarksDataEmpty(CancellationToken cancellationToken)
        => Ok(await _tools.StartEmptyAsync(User.GetUserId(), cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Starts the list of users deleted on this server (<c>kit-users.json</c>) empty (Bookmarks 1.2), as only an admin can
    /// choose, while it can't be read (Diagnostics' <c>Users.Marks.Recoverable</c>): until then Remap by name and an import
    /// by name are refused, since a deleted user's bookmarks could go to a new user of the same name. What couldn't be read
    /// is kept as <c>kit-users.json.corrupt-&lt;time&gt;</c>, the marks made since the server started are kept, and the
    /// users are checked at once, so the users this server doesn't know are listed again; marks of users deleted while the
    /// list couldn't be read may be lost.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200"><c>{StartedEmpty, MovedAside, MarksKept, Orphans}</c>.</response>
    /// <response code="409">The list isn't unreadable, or a readable copy is in place now (restart the server to use it).</response>
    /// <response code="503">The list couldn't be saved; nothing was changed.</response>
    /// <returns>What was done.</returns>
    [HttpPost("Users/Marks/StartEmpty")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IDictionary<string, object?>>> StartBookmarksUserMarksEmpty(CancellationToken cancellationToken)
        => Ok(await _tools.StartUserMarksEmptyAsync(User.GetUserId(), cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Queues the daily "Bookmarks: Maintenance" task to run now: the snapshot, the user check, the verify sweep, the
    /// detached bookmarks' re-resolution and the clip files waiting to be deleted.
    /// </summary>
    /// <response code="202">Queued; Diagnostics' <c>Maintenance</c> entry shows the run.</response>
    /// <response code="503">The server's scheduled tasks aren't available.</response>
    /// <returns>What was queued.</returns>
    [HttpPost("Maintenance/Run")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<IDictionary<string, object?>> RunBookmarksMaintenance() => Accepted(_tools.RunMaintenance());

    /// <summary>
    /// Checks a bookmark shortcut before it is saved: the combination in the notation the player
    /// matches (<c>Shift+B</c>), whether it is one, and who else acts on it: jellyfin-web's video player, and Jellyfin
    /// Enhanced's keys as its settings and every user's own JE shortcuts have them now.
    /// </summary>
    /// <param name="combo">What was typed (<c>Shift+B</c>, <c>shift+b</c>, <c>F7</c>); empty for no shortcut.</param>
    /// <response code="200"><c>{Name, Label, Combo, Valid, Reserved, Warning, Reservations}</c>.</response>
    /// <returns>The check.</returns>
    [HttpGet("Shortcuts/Check")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IDictionary<string, object?>> CheckBookmarksShortcut([FromQuery] string? combo) => Ok(_tools.CheckShortcut(combo));

    /// <summary>
    /// Checks a clip folder before it is saved (since Bookmarks 1.2.1): the folder it is (the default one for an empty
    /// field), whether it is a folder path, and the warning when it lies in a Movies or TV library, where Jellyfin would show
    /// the clips as duplicates of the films and episodes they come from. The path is in the body, so no request log has it.
    /// </summary>
    /// <param name="request">The field's value.</param>
    /// <response code="200"><c>{Path, Valid, Warnings}</c>; each warning <c>{Code, Message}</c>.</response>
    /// <returns>The check.</returns>
    [HttpPost("ClipFolder/Check")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IDictionary<string, object?>> CheckBookmarksClipFolder([FromBody] BookmarkClipFolderCheckRequest? request)
        => Ok(_tools.CheckClipFolder(request?.Path, User.GetUserId()));

    /// <summary>
    /// "This is the clip folder; its clips are gone" (since 1.2.0): confirms that a recorded clip folder the
    /// daily maintenance's missing-clip sweep left out (Diagnostics' <c>ClipSweep.LastRun.UnavailableFolders</c>, by its
    /// <c>RootId</c>) is the folder its clips were made in, and that those of its clips whose file isn't there are gone: the
    /// folder is made when it isn't there and given its marker file, and those clips are marked missing now, so the retention
    /// (<c>MissingClipRetentionDays</c>) takes them as any. A dry run (the default) only counts; otherwise after a snapshot.
    /// No file is deleted.
    /// </summary>
    /// <param name="rootId">The folder's <c>RootId</c>.</param>
    /// <param name="dryRun">Whether to only count; true when left out.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200"><c>{DryRun, Folder, RootId, Existed, Created, Marker, Clips, Present, Gone, MarkedBefore, Marked}</c>.</response>
    /// <response code="404">No recorded clip folder has that id.</response>
    /// <response code="409">The bookmark data is read-only.</response>
    /// <response code="503">The folder couldn't be made, or its marker file couldn't be written; nothing was marked.</response>
    /// <returns>What was (or would be) done.</returns>
    [HttpPost("ClipFolders/{rootId}/Confirm")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IDictionary<string, object?>>> ConfirmBookmarksClipFolder([FromRoute] string rootId, [FromQuery] bool? dryRun, CancellationToken cancellationToken)
        => Ok(await _tools.ConfirmClipFolderAsync(rootId, dryRun, User.GetUserId(), cancellationToken).ConfigureAwait(false));
}
