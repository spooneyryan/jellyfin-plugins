using System;
using System.Collections.Generic;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SmartSearch.Api;

/// <summary>
/// The admin's endpoints (Jellyfin's <c>RequiresElevation</c>: administrators and API keys): the kit's Diagnostics,
/// every user's saved queries, a new owner for a query, export and import, the deleted-user tools and the snapshots of
/// the saved queries.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("SmartSearch/Admin")]
[Produces(MediaTypeNames.Application.Json)]
[KitAbiGate]
[PluginExceptionFilter]
[LookupParameters]
public class SmartSearchAdminController : ControllerBase
{
    private readonly IKitDiagnostics _diagnostics;
    private readonly ISmartSearchSavedQueriesAdmin _queries;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmartSearchAdminController"/> class.
    /// </summary>
    /// <param name="diagnostics">The kit's diagnostics.</param>
    /// <param name="queries">The saved queries' admin tools.</param>
    public SmartSearchAdminController(IKitDiagnostics diagnostics, ISmartSearchSavedQueriesAdmin queries)
    {
        _diagnostics = diagnostics;
        _queries = queries;
    }

    /// <summary>
    /// Gets the plugin's diagnostics: the kit's sections (build, startup, web anchors, page hosts, the saved-query store,
    /// users, maintenance).
    /// </summary>
    /// <response code="200">The diagnostics, by section.</response>
    /// <returns>The diagnostics.</returns>
    [HttpGet("Diagnostics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IDictionary<string, object?>> GetSmartSearchDiagnostics() => Ok(_diagnostics.Describe());

    /// <summary>
    /// Lists every user's saved queries, or one user's, ordered by owner and name; private ones included.
    /// </summary>
    /// <param name="userId">The owner to list; every owner when left out.</param>
    /// <param name="startIndex">The index of the page's first query.</param>
    /// <param name="limit">The page size, 1 to 500.</param>
    /// <response code="200">The page.</response>
    /// <response code="400">A parameter isn't valid (<c>InvalidRequest</c>).</response>
    /// <returns>A page of every user's queries.</returns>
    [HttpGet("Queries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<SmartSearchSavedQueryListDto> GetSmartSearchAllQueries([FromQuery] Guid? userId, [FromQuery] int startIndex = 0, [FromQuery] int limit = 100)
        => _queries.List(userId, startIndex, limit);

    /// <summary>
    /// Gives a saved query to another user: for a query kept after its owner was deleted. A name the user already uses
    /// gets a number. A query auto-collections use moves with them (reassign the auto-collection), alone only with
    /// <c>force=true</c>; a query that uses an administrator-only field
    /// goes only to an administrator; the user's <c>MaxSavedQueriesPerUser</c> holds.
    /// </summary>
    /// <param name="id">The query's id.</param>
    /// <param name="request">The new owner, read strictly.</param>
    /// <param name="force">Whether a query auto-collections use moves alone; they then stop until they follow another query.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="204">Done.</response>
    /// <response code="400">The body isn't valid, or no user has the id (<c>UnknownUser</c>).</response>
    /// <response code="404">No query of this id.</response>
    /// <response code="409">Auto-collections use the query and <c>force</c> isn't set (<c>BreaksAutoCollections</c>), it uses an administrator-only field and the user isn't an administrator (<c>AdminOnlyField</c>), or the user has as many queries as allowed (<c>QuotaReached</c>).</response>
    /// <response code="503">The saved queries can't be written now.</response>
    /// <returns>Nothing.</returns>
    [HttpPost("Queries/{id}/Owner")]
    [RequestSizeLimit(SmartSearchRequestLimits.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SetSmartSearchQueryOwner([FromRoute] Guid id, [SmartSearchBody] SmartSearchSavedQueryOwnerDto request, [FromQuery] bool? force, CancellationToken cancellationToken)
    {
        await _queries.SetOwnerAsync(id, request, force == true, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Exports the saved queries with their owners' names (and the settings, for reference) as the kit's envelope
    /// (<c>jellyfin-plugin-export/1</c>), an attachment that isn't cached.
    /// </summary>
    /// <response code="200">The export file.</response>
    /// <response code="503">The saved queries can't be read faithfully (an upgrade that failed).</response>
    /// <returns>The file.</returns>
    [HttpGet("Export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult ExportSmartSearchQueries() => _queries.Export(HttpContext);

    /// <summary>
    /// Imports an export of the saved queries: the request body, <c>application/json</c>, at most 100 MB. A dry run (the
    /// default) only reports. Queries are matched by id: <c>Merge</c> (the default) adds new ones and replaces older copies,
    /// <c>Replace</c> makes the saved queries the export's, after a snapshot. Owners are matched by <c>Id</c> (the default)
    /// or <c>Name</c>; queries of owners not found are skipped. Every query is kept as it is, those this version can't run
    /// included; a record without a query or an id, a repeated id, a query with an administrator-only field for an owner
    /// who isn't an administrator, and a query past its owner's quota are skipped with a note. Settings are never applied.
    /// The report lists the auto-collection references the import would break (<c>BrokenAutoCollections</c>).
    /// </summary>
    /// <param name="mode"><c>Merge</c> or <c>Replace</c>.</param>
    /// <param name="dryRun">Whether to only report; true when left out.</param>
    /// <param name="mapUsersBy"><c>Id</c> or <c>Name</c>.</param>
    /// <param name="force">Whether a real run goes ahead although it breaks auto-collection references.</param>
    /// <response code="200">The report.</response>
    /// <response code="400">Not an export of Smart Search, or a bad option.</response>
    /// <response code="409">A newer schema, read-only data, users matched by name while the list of users deleted on this server can't be read, or references it would break without <c>force</c> (<c>BreaksAutoCollections</c>).</response>
    /// <response code="413">Over 100 MB.</response>
    /// <returns>The import report.</returns>
    [HttpPost("Import")]
    [RequestSizeLimit(ExportEnvelope.MaxImportBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<IDictionary<string, object?>>> ImportSmartSearchQueries([FromQuery] string? mode, [FromQuery] bool? dryRun, [FromQuery] string? mapUsersBy, [FromQuery] bool? force)
        => Ok(await _queries.ImportAsync(Request, mode, dryRun, mapUsersBy, force).ConfigureAwait(false));

    /// <summary>
    /// Describes the owners of the saved queries: ids this server doesn't know, policies waiting, the deleted users marked.
    /// </summary>
    /// <response code="200">The description.</response>
    /// <returns>The users and their record counts.</returns>
    [HttpGet("Users")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IDictionary<string, object?>> GetSmartSearchUsers() => Ok(_queries.GetUsers());

    /// <summary>
    /// <i>Remap by name</i>: each owner id this server doesn't know whose last seen name is one user's name here gets
    /// that user's id, after a snapshot. Users deleted on this server are never remapped. A dry run (the default) only
    /// reports, with the auto-collection references the remap would break and why a real run would be refused.
    /// </summary>
    /// <param name="by">How to match: <c>Name</c> (the only way).</param>
    /// <param name="dryRun">Whether to only report; true when left out.</param>
    /// <param name="force">Whether a real run goes ahead although it breaks auto-collection references.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The report.</response>
    /// <response code="400">Another way to match.</response>
    /// <response code="409">The saved queries are read-only, the list of deleted users can't be read, references it would break without <c>force</c> (<c>BreaksAutoCollections</c>), a query with an administrator-only field for a user who isn't an administrator (<c>AdminOnlyField</c>), or a user past the quota (<c>QuotaReached</c>).</response>
    /// <returns>The remap report.</returns>
    [HttpPost("Users/Remap")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IDictionary<string, object?>>> RemapSmartSearchUsers([FromQuery] string? by, [FromQuery] bool? dryRun, [FromQuery] bool? force, CancellationToken cancellationToken)
        => Ok(await _queries.RemapUsersAsync(by, dryRun, force, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// <i>Apply deleted-user policy</i> to owner ids this server doesn't know (<c>DeletedUserPolicy</c>; under
    /// <c>Delete</c>, private queries nothing uses go and shared ones stay as "Deleted user"), after a snapshot.
    /// </summary>
    /// <param name="request">The user ids, read strictly.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The report.</response>
    /// <response code="400">No ids, too many, or a current user's id.</response>
    /// <response code="409">The saved queries are read-only.</response>
    /// <response code="503">The server's user list can't be read; nothing was changed.</response>
    /// <returns>The policy report.</returns>
    [HttpPost("Users/ApplyPolicy")]
    [RequestSizeLimit(SmartSearchRequestLimits.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IDictionary<string, object?>>> ApplySmartSearchDeletedUserPolicy([SmartSearchBody] SmartSearchApplyPolicyDto request, CancellationToken cancellationToken)
        => Ok(await _queries.ApplyPolicyAsync(request, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Lists the snapshots of the saved queries, newest first: the daily ones (<c>SnapshotsToKeep</c>) and those taken
    /// before an operation (an import, a restore, a deleted user's policy, <i>Remap by name</i>).
    /// </summary>
    /// <response code="200">The snapshots.</response>
    /// <returns>The list.</returns>
    [HttpGet("Snapshots")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IDictionary<string, object?>> GetSmartSearchSnapshots() => Ok(_queries.GetSnapshots());

    /// <summary>
    /// Restores a snapshot of the saved queries, after a snapshot of what is there now. A dry run reports the
    /// auto-collection references the restore would break (<c>BrokenAutoCollections</c>).
    /// </summary>
    /// <param name="name">The snapshot's name, as listed.</param>
    /// <param name="dryRun">Whether to only report; false when left out.</param>
    /// <param name="force">Whether a real run goes ahead although it breaks auto-collection references.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">What was, or would be, restored.</response>
    /// <response code="400">No name.</response>
    /// <response code="404">No snapshot of this name.</response>
    /// <response code="409">The snapshot can't be used, or references it would break without <c>force</c> (<c>BreaksAutoCollections</c>).</response>
    /// <returns>The restore report.</returns>
    [HttpPost("Snapshots/Restore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IDictionary<string, object?>>> RestoreSmartSearchSnapshot([FromQuery] string? name, [FromQuery] bool? dryRun, [FromQuery] bool? force, CancellationToken cancellationToken)
        => Ok(await _queries.RestoreSnapshotAsync(name, dryRun, force, cancellationToken).ConfigureAwait(false));
}
