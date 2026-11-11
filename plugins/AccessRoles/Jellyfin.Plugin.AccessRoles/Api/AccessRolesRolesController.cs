using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.Kit.Api;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AccessRoles.Api;

/// <summary>
/// Access Roles' roles and default roles: <c>RequiresElevation</c>, API keys allowed (the
/// break-glass path). Writes take <c>?dryRun=true</c>, <c>IfUpdatedAt</c>, <c>onUnmanage=</c> and
/// <c>confirmNoFullAdmin=</c>, and answer what they did with the preview of what it does per user. Refusals are
/// ProblemDetails with a <c>Code</c> (<c>EmptyOnlyRole</c>, <c>UnmanageChoiceRequired</c>, <c>NoLibraries</c>, …) and the
/// <c>Users</c> concerned. A saved change starts the library sync, which writes what it means for users' library
/// access.
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
public class AccessRolesRolesController : ControllerBase
{
    /// <summary>The largest request body a write of the roles API reads (the kit's abuse limits).</summary>
    internal const int MaxBodyBytes = 256 * 1024;

    private readonly IAccessRolesAdmin _admin;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessRolesRolesController"/> class.
    /// </summary>
    /// <param name="admin">The roles API.</param>
    public AccessRolesRolesController(IAccessRolesAdmin admin)
    {
        _admin = admin;
    }

    /// <summary>
    /// Lists the roles, by name, with their member counts and <i>Dashboard</i> rows.
    /// </summary>
    /// <response code="200">The roles.</response>
    /// <returns>Every role.</returns>
    [HttpGet("Roles")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<AccessRolesRoleDto>> GetAccessRolesRoles() => Ok(_admin.GetRoles());

    /// <summary>
    /// Gets a role.
    /// </summary>
    /// <param name="roleId">The role's id.</param>
    /// <response code="200">The role.</response>
    /// <response code="404">There is no such role.</response>
    /// <returns>The role asked for.</returns>
    [HttpGet("Roles/{roleId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AccessRolesRoleDto> GetAccessRolesRole([FromRoute, Required] Guid roleId) => Ok(_admin.GetRole(roleId));

    /// <summary>
    /// Creates a role, from a preset (<c>Kid</c>, <c>Guest</c>, <c>Standard</c>) or from scratch.
    /// </summary>
    /// <param name="request">The role.</param>
    /// <param name="options">The dry run, <c>onUnmanage</c> and <c>confirmNoFullAdmin</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The role as saved (or as it would be, for a dry run), with the preview.</response>
    /// <response code="400">An invalid role, <c>EmptyOnlyRole</c>, or <c>UnmanageChoiceRequired</c>.</response>
    /// <response code="409"><c>DuplicateName</c>, <c>NoLibraries</c> or <c>NoFullAdministrator</c>.</response>
    /// <response code="503">The data is read-only.</response>
    /// <returns>The change.</returns>
    [HttpPost("Roles")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AccessRolesChangeDto>> CreateAccessRolesRole(
        [FromBody, Required] AccessRolesRoleRequest request,
        [FromQuery] AccessRolesWriteOptions options,
        CancellationToken cancellationToken)
        => Ok(await _admin.CreateRoleAsync(request, options ?? new AccessRolesWriteOptions(), User, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Replaces a role: rename, description, locks, libraries. <c>IfUpdatedAt</c> is required.
    /// </summary>
    /// <param name="roleId">The role's id.</param>
    /// <param name="request">The role.</param>
    /// <param name="options">The dry run, <c>onUnmanage</c> and <c>confirmNoFullAdmin</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The role as saved, with the preview.</response>
    /// <response code="400">An invalid role, <c>EmptyOnlyRole</c>, or <c>UnmanageChoiceRequired</c>.</response>
    /// <response code="404">There is no such role.</response>
    /// <response code="409"><c>Changed</c> (since IfUpdatedAt), <c>DuplicateName</c>, <c>NoLibraries</c>, <c>NoLedgerEntry</c> or <c>NoFullAdministrator</c>.</response>
    /// <response code="503">The data is read-only.</response>
    /// <returns>The change.</returns>
    [HttpPut("Roles/{roleId:guid}")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AccessRolesChangeDto>> UpdateAccessRolesRole(
        [FromRoute, Required] Guid roleId,
        [FromBody, Required] AccessRolesRoleRequest request,
        [FromQuery] AccessRolesWriteOptions options,
        CancellationToken cancellationToken)
        => Ok(await _admin.UpdateRoleAsync(roleId, request, options ?? new AccessRolesWriteOptions(), User, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Deletes a role. Its members lose it, or get <c>reassignTo</c> instead; members it takes out of library management need
    /// <c>onUnmanage</c>. A default role of new users can't be deleted.
    /// </summary>
    /// <param name="roleId">The role's id.</param>
    /// <param name="reassignTo">The role the members get instead.</param>
    /// <param name="ifUpdatedAt">The role's <c>UpdatedUtc</c> as last read, if given.</param>
    /// <param name="ifMembers">
    /// The role's <c>MembersToken</c> as last read, if given: a 409 <c>Changed</c> when its members, or their assignments,
    /// changed since (so a reassignment never moves a member the caller didn't see).
    /// </param>
    /// <param name="options">The dry run, <c>onUnmanage</c> and <c>confirmNoFullAdmin</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">What was deleted, with the preview.</response>
    /// <response code="400">An invalid <c>reassignTo</c>, or <c>UnmanageChoiceRequired</c>.</response>
    /// <response code="404">There is no such role.</response>
    /// <response code="409"><c>DefaultRole</c>, <c>Changed</c>, <c>NoLibraries</c>, <c>NoLedgerEntry</c> or <c>NoFullAdministrator</c>.</response>
    /// <response code="503">The data is read-only.</response>
    /// <returns>The change.</returns>
    [HttpDelete("Roles/{roleId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AccessRolesChangeDto>> DeleteAccessRolesRole(
        [FromRoute, Required] Guid roleId,
        [FromQuery] Guid? reassignTo,
        [FromQuery] DateTime? ifUpdatedAt,
        [FromQuery] string? ifMembers,
        [FromQuery] AccessRolesWriteOptions options,
        CancellationToken cancellationToken)
        => Ok(await _admin.DeleteRoleAsync(roleId, reassignTo, ifUpdatedAt, ifMembers, options ?? new AccessRolesWriteOptions(), User, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Sets the roles new users get (stored; given to new users by the user-created handling).
    /// </summary>
    /// <param name="request">The roles.</param>
    /// <param name="options">The dry run.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The default roles as saved.</response>
    /// <response code="404">A role doesn't exist.</response>
    /// <response code="503">The data is read-only.</response>
    /// <returns>The change.</returns>
    [HttpPut("DefaultRoles")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AccessRolesChangeDto>> SetAccessRolesDefaultRoles(
        [FromBody, Required] AccessRolesDefaultRolesRequest request,
        [FromQuery] AccessRolesWriteOptions options,
        CancellationToken cancellationToken)
        => Ok(await _admin.SetDefaultRolesAsync(request, options ?? new AccessRolesWriteOptions(), User, cancellationToken).ConfigureAwait(false));
}
