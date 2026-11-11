using System.Collections.Generic;
using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Bookmarks.Api;

/// <summary>
/// The developer demo of the kit's users and data services, for sandbox checks only: admins, and only while the
/// developer setting <c>EnableUserDataDemo</c> is on (403 "turned off" otherwise). The routes under it mirror the kit's
/// standard admin endpoints (<c>Users/Remap</c>, <c>Users/ApplyPolicy</c>, <c>Export</c>, <c>Import</c>,
/// <c>Snapshots</c>, <c>Snapshots/Restore</c>), which Bookmarks itself serves for its own store from 1.1 on, plus
/// <c>Records</c>, <c>Reset</c> and <c>Reconcile</c> to drive the demo.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Bookmarks/Admin/UserDataDemo")]
[Produces(MediaTypeNames.Application.Json)]
[KitAbiGate]
[PluginExceptionFilter]
public class BookmarksUserDataDemoController : ControllerBase
{
    private readonly IBookmarkUserDataDemo _demo;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarksUserDataDemoController"/> class.
    /// </summary>
    /// <param name="demo">The demo.</param>
    public BookmarksUserDataDemoController(IBookmarkUserDataDemo demo)
    {
        _demo = demo;
    }

    /// <summary>
    /// Lists the demo records with their owners' names, and the store's known users.
    /// </summary>
    /// <response code="200">The records.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <returns>An action result with the records.</returns>
    [HttpGet("Records")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IDictionary<string, object?>> GetBookmarksUserDataDemoRecords() => Ok(_demo.GetRecords());

    /// <summary>
    /// Adds a demo record for a user of the server.
    /// </summary>
    /// <param name="body"><c>{UserId, Text, HasFile}</c>; <c>HasFile</c> stands in for a bookmark with a clip file.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <response code="200">The record.</response>
    /// <response code="400">The body is invalid, or no user has the id.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <returns>An action result with the record.</returns>
    [HttpPost("Records")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IDictionary<string, object?>>> AddBookmarksUserDataDemoRecord([FromBody] JsonElement body, CancellationToken cancellationToken)
        => Ok(await _demo.AddRecordAsync(body, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Empties the demo store.
    /// </summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <response code="204">Emptied.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <returns>An empty action result.</returns>
    [HttpPost("Reset")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ResetBookmarksUserDataDemo(CancellationToken cancellationToken)
    {
        await _demo.ResetAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Runs the kit's user reconciler now, as the maintenance task will: unknown user ids become orphans, nothing is
    /// deleted, and the guard changes nothing when stored users don't match the server.
    /// </summary>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <response code="200">The run.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <returns>An action result with the run.</returns>
    [HttpPost("Reconcile")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IDictionary<string, object?>>> ReconcileBookmarksUserDataDemo(CancellationToken cancellationToken)
        => Ok(await _demo.ReconcileAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Describes the users now, as the <c>Users</c> entry of Diagnostics does.
    /// </summary>
    /// <response code="200">The description.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <returns>An action result with the description.</returns>
    [HttpGet("Users")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IDictionary<string, object?>> GetBookmarksUserDataDemoUsers() => Ok(_demo.GetUsers());

    /// <summary>
    /// <i>Remap by name</i>: unknown stored user ids go to the server user with their last seen name.
    /// </summary>
    /// <param name="by">Only <c>Name</c>.</param>
    /// <param name="dryRun">Only report (the default).</param>
    /// <param name="cancellationToken">Cancels the remap.</param>
    /// <response code="200">The report.</response>
    /// <response code="400">Another <c>by</c>.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <response code="409">The store is read-only.</response>
    /// <returns>An action result with the report.</returns>
    [HttpPost("Users/Remap")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IDictionary<string, object?>>> RemapBookmarksUserDataDemoUsers([FromQuery] string? by, [FromQuery] bool? dryRun, CancellationToken cancellationToken)
        => Ok(await _demo.RemapUsersAsync(by, dryRun, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// <i>Apply deleted-user policy</i> to users that are gone.
    /// </summary>
    /// <param name="body"><c>{UserIds: [...]}</c>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <response code="200">The report.</response>
    /// <response code="400">No ids, or an id of a current user.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <response code="409">The store is read-only.</response>
    /// <returns>An action result with the report.</returns>
    [HttpPost("Users/ApplyPolicy")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IDictionary<string, object?>>> ApplyBookmarksUserDataDemoPolicy([FromBody] JsonElement body, CancellationToken cancellationToken)
        => Ok(await _demo.ApplyPolicyAsync(body, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Exports the demo store: the kit's export envelope, as an attachment, not cached.
    /// </summary>
    /// <response code="200">The export file.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <response code="503">The store can't be read.</response>
    /// <returns>An action result with the file.</returns>
    [HttpGet("Export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult ExportBookmarksUserDataDemo() => _demo.Export(HttpContext);

    /// <summary>
    /// Imports an export file (the body, <c>application/json</c>, at most 100 MB) into the demo store.
    /// </summary>
    /// <param name="mode"><c>Merge</c> (default) or <c>Replace</c>.</param>
    /// <param name="dryRun">Only report (the default).</param>
    /// <param name="mapUsersBy"><c>Id</c> (default) or <c>Name</c>.</param>
    /// <response code="200">The report.</response>
    /// <response code="400">Not an export of this plugin.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <response code="409">A newer schema version, or the store is read-only.</response>
    /// <response code="413">Over 100 MB.</response>
    /// <returns>An action result with the report.</returns>
    [HttpPost("Import")]
    [RequestSizeLimit(ExportEnvelope.MaxImportBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<IDictionary<string, object?>>> ImportBookmarksUserDataDemo([FromQuery] string? mode, [FromQuery] bool? dryRun, [FromQuery] string? mapUsersBy)
        => Ok(await _demo.ImportAsync(Request, mode, dryRun, mapUsersBy).ConfigureAwait(false));

    /// <summary>
    /// Lists the demo store's snapshots, daily and pre-operation.
    /// </summary>
    /// <response code="200">The snapshots.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <returns>An action result with the snapshots.</returns>
    [HttpGet("Snapshots")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IDictionary<string, object?>> GetBookmarksUserDataDemoSnapshots() => Ok(_demo.GetSnapshots());

    /// <summary>
    /// Takes a daily snapshot of the demo store now, as the maintenance task will.
    /// </summary>
    /// <param name="keep">How many daily snapshots to keep (1 to 100; 7 by default).</param>
    /// <param name="cancellationToken">Cancels the snapshot.</param>
    /// <response code="200">The snapshot and the list.</response>
    /// <response code="400">keep out of range.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <returns>An action result with the snapshots.</returns>
    [HttpPost("Snapshots")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IDictionary<string, object?>>> SnapshotBookmarksUserDataDemo([FromQuery] int? keep, CancellationToken cancellationToken)
        => Ok(await _demo.TakeSnapshotAsync(keep, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// <i>Restore snapshot</i>: the demo store takes a snapshot's data, after a snapshot of its own.
    /// </summary>
    /// <param name="name">The snapshot's name from <c>Snapshots</c>.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <response code="200">What was restored.</response>
    /// <response code="400">No name.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <response code="404">No snapshot has the name.</response>
    /// <response code="409">A newer schema version, an unreadable snapshot, or the store is read-only.</response>
    /// <returns>An action result with the snapshot restored and the safety snapshot.</returns>
    [HttpPost("Snapshots/Restore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IDictionary<string, object?>>> RestoreBookmarksUserDataDemoSnapshot([FromQuery] string? name, CancellationToken cancellationToken)
        => Ok(await _demo.RestoreSnapshotAsync(name, cancellationToken).ConfigureAwait(false));
}
