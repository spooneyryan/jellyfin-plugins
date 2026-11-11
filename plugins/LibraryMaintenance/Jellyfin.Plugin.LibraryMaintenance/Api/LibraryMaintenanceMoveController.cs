using System;
using System.Collections.Generic;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LibraryMaintenance.Api;

/// <summary>
/// Move media's endpoints, for administrators only. Every route answers 403 with the kit's
/// <c>feature-disabled</c> problem type while <c>MoveEnabled</c> is off, and 503 (<c>move.unsupported-os</c>) on a host that
/// isn't Windows (<see cref="MoveGateAttribute"/>). Bodies are 64 KB at most.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("LibraryMaintenance/Move")]
[Produces(MediaTypeNames.Application.Json)]
[RequestSizeLimit(MaxBodyBytes)]
[KitAbiGate]
[MoveGate]
[PluginExceptionFilter]
public class LibraryMaintenanceMoveController : ControllerBase
{
    /// <summary>The largest request body: 64 KB.</summary>
    internal const long MaxBodyBytes = 64 * 1024;

    private readonly IMoveMedia _move;
    private readonly IUserManager _users;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryMaintenanceMoveController"/> class.
    /// </summary>
    /// <param name="move">Move media.</param>
    /// <param name="users">The user manager (the caller's library access).</param>
    public LibraryMaintenanceMoveController(IMoveMedia move, IUserManager users)
    {
        _move = move;
        _users = users;
    }

    /// <summary>
    /// Gets whether items can move: a movie, episode or music video with a local file in a Movies,
    /// Shows or Music Videos library the caller can open, not virtual, a placeholder, a shortcut, an extra or being
    /// recorded, and not held by a Duplicates operation. A version or part of another item answers for its owner. An item
    /// the caller can't open answers like one that isn't there.
    /// </summary>
    /// <param name="ids">The item ids, comma-separated; 1 to 100.</param>
    /// <response code="200">One answer per id, in the order asked.</response>
    /// <response code="400">No ids, more than 100, or one that isn't an id.</response>
    /// <response code="403">Not an administrator, or Move is turned off (problem type <c>feature-disabled</c>).</response>
    /// <response code="503">The host isn't Windows (<c>move.unsupported-os</c>).</response>
    /// <returns>The answers.</returns>
    [HttpGet("Eligibility")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<IReadOnlyList<LibraryMaintenanceMoveEligibilityDto>> GetLibraryMaintenanceMoveEligibility([FromQuery] string? ids)
    {
        RequireSingleValues(nameof(ids));
        return Ok(_move.GetEligibility(ids, Caller()));
    }

    /// <summary>
    /// Gets what moves with an item: its own folder, or its files and the sidecars that share their
    /// names, with its versions, parts and extras. A version or part answers for the item it belongs to, and says so.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <response code="200">The item, its library and location, and what moves.</response>
    /// <response code="404">No such item, or one in a library the caller can't open.</response>
    /// <response code="409">The item can't move (<c>move.not-eligible</c>, <c>move.item-busy</c>).</response>
    /// <returns>The source.</returns>
    [HttpGet("Source")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<LibraryMaintenanceMoveSourceDto> GetLibraryMaintenanceMoveSource([FromQuery] Guid itemId)
    {
        RequireSingleValues(nameof(itemId));
        return Ok(_move.GetSource(itemId, Caller()));
    }

    /// <summary>
    /// Gets the libraries an item can move to: those of its type the caller can open (only its own with
    /// <c>AllowCrossLibraryMoves</c> off), each folder with its drive's label and free space and whether it can be chosen
    /// (in this version only folders on the item's own volume can).
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <response code="200">The libraries, the item's own first.</response>
    /// <response code="404">No such item, or one in a library the caller can't open.</response>
    /// <response code="409">The item can't move.</response>
    /// <returns>The libraries.</returns>
    [HttpGet("Destinations")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<IReadOnlyList<LibraryMaintenanceMoveDestinationLibraryDto>> GetLibraryMaintenanceMoveDestinations([FromQuery] Guid itemId)
    {
        RequireSingleValues(nameof(itemId));
        return Ok(_move.GetDestinations(itemId, Caller()));
    }

    /// <summary>
    /// Gets a page of the subfolders of a folder in a library the caller can open, each with its badge (movie folder,
    /// series, season, ignored by Jellyfin, link) and whether it can be chosen.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="locationIndex">The index of its folder.</param>
    /// <param name="relativePath">A folder under it, backslash- or slash-separated; empty for the library folder.</param>
    /// <param name="filter">Only names containing this, ignoring case.</param>
    /// <param name="startIndex">The first folder of the page.</param>
    /// <param name="limit">The page size, 1 to 200.</param>
    /// <param name="itemId">The item being moved (optional): its own folder gets its badge, and a library it can't move to
    /// is refused.</param>
    /// <response code="200">The page.</response>
    /// <response code="400">A bad path or page (<c>move.destination-invalid</c>, <c>move.reparse-point</c>,
    /// <c>move.extras-folder-name</c>), or a library <paramref name="itemId"/> can't move to.</response>
    /// <response code="404">No such library or folder, or one the caller can't open.</response>
    /// <response code="409">With <paramref name="itemId"/>: the item can't move, with its code (<c>move.not-eligible</c>,
    /// <c>move.item-busy</c>, or <c>move.reparse-point</c> for an item reached through a link), as Source and Plan answer.</response>
    /// <response code="503">The folder isn't reachable.</response>
    /// <returns>The subfolders.</returns>
    [HttpGet("Folders")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<LibraryMaintenanceMoveFoldersDto> GetLibraryMaintenanceMoveFolders(
        [FromQuery] Guid libraryId,
        [FromQuery] int locationIndex,
        [FromQuery] string? relativePath,
        [FromQuery] string? filter,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = MoveDestinations.MaxFolders,
        [FromQuery] Guid? itemId = null)
    {
        RequireSingleValues(nameof(libraryId), nameof(locationIndex), nameof(relativePath), nameof(filter), nameof(startIndex), nameof(limit), nameof(itemId));
        return Ok(_move.GetFolders(libraryId, locationIndex, relativePath, filter, startIndex, limit, itemId, Caller()));
    }

    /// <summary>
    /// Plans moving one item to a folder: what moves, to where, by which method, the ids the items
    /// will get, what is kept and what is regenerated, with warnings and blockers. Nothing moves and nothing is written:
    /// whether the destination can be written is an access check that opens the folder and writes nothing; the plan is
    /// kept 15 minutes, in memory. Planning opens each of the item's files exclusively for an instant (whether something
    /// has it open) and reads the folders, which can update their last-access times.
    /// </summary>
    /// <param name="request">The item and the destination.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The plan, also when it has blockers.</response>
    /// <response code="400">A bad request, or a destination that can't be used (<c>move.destination-invalid</c>).</response>
    /// <response code="404">No such item, or one in a library the caller can't open.</response>
    /// <response code="409">The item can't move.</response>
    /// <response code="503">The destination isn't reachable, or the host isn't Windows.</response>
    /// <returns>The plan.</returns>
    [HttpPost("Plan")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<LibraryMaintenanceMovePlanDto>> PostLibraryMaintenanceMovePlan([FromBody] LibraryMaintenanceMovePlanRequestDto? request, CancellationToken cancellationToken)
        => Ok(await _move.PlanAsync(request, Caller(), cancellationToken).ConfigureAwait(false));

    // A query option given twice is a 400, as on the Duplicates routes.
    private void RequireSingleValues(params string[] names)
    {
        foreach (var name in names)
        {
            if (Request.Query.TryGetValue(name, out var values) && values.Count > 1)
            {
                throw PluginException.Invalid("The option '" + name + "' was given more than once; give it once (a list is comma-separated).");
            }
        }
    }

    // The caller whose library access applies: none for an API key (every library); a user that no longer exists is a 403.
    private User? Caller() => User.IsApiKey() ? null : User.RequireUser(_users);
}
