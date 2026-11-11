using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.Kit.Api;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AccessRoles.Api;

/// <summary>
/// Access Roles' assignments: every user with their state, a user's assignment,
/// bulk role changes, leaving management, and the effective access with its sources. <c>RequiresElevation</c>, API keys
/// allowed. Every user id is resolved against the server (404 for one it doesn't have). These endpoints write no user's
/// policy themselves: <c>PendingWrite</c> and <c>PolicyWrite</c> say what the library sync, which a saved change
/// starts, will write.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("AccessRoles/Admin")]
[Produces(MediaTypeNames.Application.Json)]
[KitAbiGate]
[AccessRolesInertGate]
[PluginExceptionFilter]
[AccessRolesProblemFilter]
[StrictEnumQuery]
public class AccessRolesUsersController : ControllerBase
{
    private readonly IAccessRolesAdmin _admin;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessRolesUsersController"/> class.
    /// </summary>
    /// <param name="admin">The roles API.</param>
    public AccessRolesUsersController(IAccessRolesAdmin admin)
    {
        _admin = admin;
    }

    /// <summary>
    /// Lists every user of the server, by name: roles, state, locks counted, library management, a summary.
    /// </summary>
    /// <param name="roleId">Only users with this role.</param>
    /// <param name="state">Only users in this state.</param>
    /// <response code="200">The users.</response>
    /// <response code="400">A state that names none.</response>
    /// <returns>Every user of the server.</returns>
    [HttpGet("Users")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<IReadOnlyList<AccessRolesUserDto>> GetAccessRolesUsers([FromQuery] Guid? roleId, [FromQuery] AccessRolesUserState? state)
        => Ok(_admin.GetUsers(roleId, state));

    /// <summary>
    /// Replaces a user's assignment: roles, lock overrides, library mode and lists, state. <c>IfUpdatedAt</c> is the
    /// assignment's <c>UpdatedUtc</c>, or none for a user without one.
    /// </summary>
    /// <param name="userId">The user's id.</param>
    /// <param name="request">The assignment.</param>
    /// <param name="options">The dry run, <c>onUnmanage</c> and <c>confirmNoFullAdmin</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The user's effective access as saved, with the preview.</response>
    /// <response code="400">An invalid assignment, or <c>UnmanageChoiceRequired</c>.</response>
    /// <response code="404">The server has no such user.</response>
    /// <response code="409"><c>Changed</c>, <c>NoLibraries</c>, <c>NoLedgerEntry</c> or <c>NoFullAdministrator</c>.</response>
    /// <response code="503">The data is read-only.</response>
    /// <returns>The change.</returns>
    [HttpPut("Users/{userId:guid}")]
    [RequestSizeLimit(AccessRolesRolesController.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AccessRolesChangeDto>> UpdateAccessRolesUser(
        [FromRoute, Required] Guid userId,
        [FromBody, Required] AccessRolesUserRequest request,
        [FromQuery] AccessRolesWriteOptions options,
        CancellationToken cancellationToken)
        => Ok(await _admin.UpdateUserAsync(userId, request, options ?? new AccessRolesWriteOptions(), User, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Gives roles to, and takes roles from, several users at once.
    /// </summary>
    /// <param name="request">The users and roles.</param>
    /// <param name="options">The dry run, <c>onUnmanage</c> and <c>confirmNoFullAdmin</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The users changed, with the preview.</response>
    /// <response code="400">An invalid request, or <c>UnmanageChoiceRequired</c>.</response>
    /// <response code="404">The server has no such user.</response>
    /// <response code="409"><c>Changed</c> (a user of <c>IfUpdatedAt</c>), <c>NoLibraries</c>, <c>NoLedgerEntry</c> or <c>NoFullAdministrator</c>.</response>
    /// <response code="503">The data is read-only.</response>
    /// <returns>The change.</returns>
    [HttpPost("Users/Bulk")]
    [RequestSizeLimit(AccessRolesRolesController.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AccessRolesChangeDto>> BulkAccessRolesUsers(
        [FromBody, Required] AccessRolesBulkRequest request,
        [FromQuery] AccessRolesWriteOptions options,
        CancellationToken cancellationToken)
        => Ok(await _admin.BulkAsync(request, options ?? new AccessRolesWriteOptions(), User, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Takes a user out of management: their assignment goes, and their library access becomes what the choice says
    /// (<c>Restore</c>, <c>Keep</c>, <c>GrantAll</c>; written by the library sync).
    /// </summary>
    /// <param name="userId">The user's id.</param>
    /// <param name="request">The choice.</param>
    /// <param name="options">The dry run and <c>confirmNoFullAdmin</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The user's effective access as saved, with the preview.</response>
    /// <response code="400">No choice.</response>
    /// <response code="404">The server has no such user.</response>
    /// <response code="409"><c>Changed</c> (<c>IfUpdatedAt</c>), <c>NoLedgerEntry</c> (for <c>Restore</c>) or <c>NoFullAdministrator</c>.</response>
    /// <response code="503">The data is read-only.</response>
    /// <returns>The change.</returns>
    [HttpPost("Users/{userId:guid}/Unmanage")]
    [RequestSizeLimit(AccessRolesRolesController.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AccessRolesChangeDto>> UnmanageAccessRolesUser(
        [FromRoute, Required] Guid userId,
        [FromBody, Required] AccessRolesUnmanageRequest request,
        [FromQuery] AccessRolesWriteOptions options,
        CancellationToken cancellationToken)
        => Ok(await _admin.UnmanageAsync(userId, request, options ?? new AccessRolesWriteOptions(), User, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Gets a user's effective access with its sources ("Locked by role Kid", "Movies: granted by role Family"), optionally
    /// as it would be with other roles (<c>simulateRoleIds</c>, comma-separated or repeated).
    /// </summary>
    /// <param name="userId">The user's id.</param>
    /// <param name="simulateRoleIds">Roles to resolve the user with instead of theirs.</param>
    /// <response code="200">The effective access.</response>
    /// <response code="400">A role that doesn't exist, or a value that isn't a GUID.</response>
    /// <response code="404">The server has no such user.</response>
    /// <returns>The user's effective access.</returns>
    [HttpGet("Users/{userId:guid}/Effective")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AccessRolesEffectiveDto> GetAccessRolesEffective(
        [FromRoute, Required] Guid userId,
        [FromQuery, CommaSeparatedQuery] IReadOnlyList<Guid>? simulateRoleIds)
        => Ok(_admin.GetEffective(userId, simulateRoleIds));
}
