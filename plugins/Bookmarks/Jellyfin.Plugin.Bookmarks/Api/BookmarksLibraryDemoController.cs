using System;
using System.Collections.Generic;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Kit.Api;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.Bookmarks.Api;

/// <summary>
/// The developer demo of the kit's record lifecycle and library reads, for sandbox checks only: admins, and
/// only while the developer setting <c>EnableLibraryDemo</c> is on (403 "turned off" otherwise). Its records are item-linked
/// demo records that the kit detaches, relinks, archives, reattaches and purges; its reads show <c>FreshUserData</c>,
/// <c>LibraryAccess</c> and <c>VersionSets</c> against the real library, as a user when a <c>userId</c> is given.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Bookmarks/Admin/LibraryDemo")]
[Produces(MediaTypeNames.Application.Json)]
[KitAbiGate]
[PluginExceptionFilter]
public class BookmarksLibraryDemoController : ControllerBase
{
    private readonly IBookmarkLibraryDemo _demo;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarksLibraryDemoController"/> class.
    /// </summary>
    /// <param name="demo">The library demo.</param>
    /// <param name="userManager">The user manager.</param>
    public BookmarksLibraryDemoController(IBookmarkLibraryDemo demo, IUserManager userManager)
    {
        _demo = demo;
        _userManager = userManager;
    }

    /// <summary>
    /// Describes the demo's records, live and archived, with the kit's verify sweep and detached records.
    /// </summary>
    /// <response code="200">The records.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <returns>The description.</returns>
    [HttpGet("Records")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IDictionary<string, object?>> GetBookmarksLibraryDemoRecords() => Ok(_demo.DescribeRecords());

    /// <summary>
    /// Writes a demo record on a library item, with the item's identity snapshot.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="userId">The record's user; the caller by default.</param>
    /// <param name="note">A free text.</param>
    /// <param name="withoutContainerIds">Leaves the series, season and album ids out of the snapshot.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The record.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <response code="404">No item the user can see, or no such user.</response>
    /// <returns>The record as written.</returns>
    [HttpPost("Records")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IDictionary<string, object?>>> AddBookmarksLibraryDemoRecord(
        [FromQuery] Guid itemId,
        [FromQuery] Guid? userId,
        [FromQuery] string? note,
        [FromQuery] bool withoutContainerIds,
        CancellationToken cancellationToken)
        => Ok(await _demo.AddRecordAsync(itemId, userId, note, withoutContainerIds, GetCaller(), cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Deletes every live demo record.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="204">Deleted.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <returns>No content.</returns>
    [HttpDelete("Records")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ClearBookmarksLibraryDemoRecords(CancellationToken cancellationToken)
    {
        await _demo.ClearRecordsAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Runs the kit's verify sweep now.
    /// </summary>
    /// <param name="cancellationToken">Cancels the waiting.</param>
    /// <response code="200">The sweep's result.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <returns>The result.</returns>
    [HttpPost("Sweep")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IDictionary<string, object?>>> SweepBookmarksLibraryDemo(CancellationToken cancellationToken)
        => Ok(await _demo.SweepAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Re-resolves the demo's detached records now.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">What was done.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <returns>The result.</returns>
    [HttpPost("Resolve")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IDictionary<string, object?>>> ResolveBookmarksLibraryDemo(CancellationToken cancellationToken)
        => Ok(await _demo.ResolveAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// The kit's <i>Purge</i> of the demo's detached records, the only hard delete, after a snapshot.
    /// </summary>
    /// <param name="olderThanDays">Only records detached at least this many days ago.</param>
    /// <param name="includeArchive">Whether archived records go too.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">What was deleted.</response>
    /// <response code="400">A negative day count.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <response code="409">The demo's data is read-only.</response>
    /// <returns>The result.</returns>
    [HttpPost("Purge")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IDictionary<string, object?>>> PurgeBookmarksLibraryDemo([FromQuery] int? olderThanDays, [FromQuery] bool includeArchive, CancellationToken cancellationToken)
        => Ok(await _demo.PurgeAsync(olderThanDays, includeArchive, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Exports the demo's data as the kit's export envelope, the archived records in its <c>Archive</c>.
    /// </summary>
    /// <response code="200">The export file.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <returns>The file.</returns>
    [HttpGet("Export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult ExportBookmarksLibraryDemo() => _demo.Export(HttpContext);

    /// <summary>
    /// Reads a user's data for an item: <c>Cached</c> as core's <c>GetUserData</c> reads its cached instance, or
    /// <c>Fresh</c> through the kit's <c>FreshUserData</c>.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="userId">The user; the caller by default.</param>
    /// <param name="read"><c>Fresh</c> (default) or <c>Cached</c>.</param>
    /// <param name="path"><c>Query</c> (default) or <c>Repository</c>, for a fresh read.</param>
    /// <response code="200">The data.</response>
    /// <response code="400">An unknown read or path.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <response code="404">No item the user can see, or no such user.</response>
    /// <response code="503">The item couldn't be read from the database.</response>
    /// <returns>The user's data and where it came from.</returns>
    [HttpGet("UserData/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<IDictionary<string, object?>> GetBookmarksLibraryDemoUserData([FromRoute] Guid itemId, [FromQuery] Guid? userId, [FromQuery] string? read, [FromQuery] string? path)
        => Ok(_demo.ReadUserData(itemId, userId, read, path, GetCaller()));

    /// <summary>
    /// The kit's <c>LibraryAccess</c> for a user: visible libraries, requested ones resolved, the factory's query.
    /// </summary>
    /// <param name="userId">The user; the caller by default.</param>
    /// <param name="libraryIds">Library or view ids.</param>
    /// <response code="200">The description.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <response code="404">No such user.</response>
    /// <returns>The libraries and the query.</returns>
    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IDictionary<string, object?>> GetBookmarksLibraryDemoLibraries([FromQuery] Guid? userId, [FromQuery] Guid[]? libraryIds)
        => Ok(_demo.DescribeLibraries(userId, libraryIds, GetCaller()));

    /// <summary>
    /// The kit's <c>VersionSets</c> over items, and the representatives a user's search would show.
    /// </summary>
    /// <param name="itemIds">The items (1 to 100).</param>
    /// <param name="userId">The user; the caller by default.</param>
    /// <response code="200">The sets.</response>
    /// <response code="400">No items, or too many.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <response code="404">No such user.</response>
    /// <returns>The sets and representatives.</returns>
    [HttpGet("VersionSets")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IDictionary<string, object?>> GetBookmarksLibraryDemoVersionSets([FromQuery] Guid[] itemIds, [FromQuery] Guid? userId)
        => Ok(_demo.DescribeVersionSets(itemIds ?? [], userId, GetCaller()));

    // The signed-in admin; null for an API key.
    private JellyfinUser? GetCaller() => User.IsApiKey() ? null : User.RequireUser(_userManager);
}
