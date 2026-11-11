using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LibraryMaintenance.Api;

/// <summary>
/// Duplicates' endpoints, for administrators only: the movie index's status and its rebuild, and the duplicate
/// groups. With Duplicates turned off in the settings they answer 404.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("LibraryMaintenance/Duplicates")]
[Produces(MediaTypeNames.Application.Json)]
[KitAbiGate]
[PluginExceptionFilter]
public class LibraryMaintenanceDuplicatesController : ControllerBase
{
    private readonly IDuplicatesIndex _index;
    private readonly IDuplicateGroups _groups;
    private readonly IUserManager _users;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryMaintenanceDuplicatesController"/> class.
    /// </summary>
    /// <param name="index">The movie index.</param>
    /// <param name="groups">The duplicate groups.</param>
    /// <param name="users">The user manager, for the caller's library access.</param>
    public LibraryMaintenanceDuplicatesController(IDuplicatesIndex index, IDuplicateGroups groups, IUserManager users)
    {
        _index = index;
        _groups = groups;
        _users = users;
    }

    /// <summary>
    /// Gets the movie index's status: the last build's counts (movies, entries, the broken version-set states), whether a
    /// build is running and how far it got, and whether a library scan is running.
    /// </summary>
    /// <remarks>The libraries and counts are those of the libraries the caller can open.</remarks>
    /// <response code="200">The status.</response>
    /// <response code="404">Duplicates is turned off in the settings.</response>
    /// <returns>The index's status.</returns>
    [HttpGet("Index")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<LibraryMaintenanceDuplicateIndexDto> GetLibraryMaintenanceDuplicateIndex()
    {
        RequireEnabled();
        return Ok(_index.GetStatus(Caller()));
    }

    /// <summary>
    /// Rebuilds the movie index in the background. Single-flight: while a build runs, one more follows it.
    /// </summary>
    /// <response code="202">The build started or is queued; the status says which.</response>
    /// <response code="404">Duplicates is turned off in the settings.</response>
    /// <returns>The status right after asking.</returns>
    [HttpPost("Index/Rebuild")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<LibraryMaintenanceDuplicateIndexDto> RebuildLibraryMaintenanceDuplicateIndex()
    {
        RequireEnabled();
        return Accepted(_index.RequestRebuild(Caller()));
    }

    /// <summary>
    /// Gets a page of duplicate groups, for the libraries asked for that the caller can see. It reads the
    /// index as the freshness rules say: at once when it is current, else after up to <c>RequestWaitSeconds</c> of waiting for a
    /// build, marked <c>Stale</c> when that build isn't done.
    /// </summary>
    /// <param name="mode"><c>TitleAndYear</c> or <c>Title</c>; the settings' default when absent.</param>
    /// <param name="unknownYear"><c>MatchUnknownOnly</c> or <c>MatchAny</c>; the settings' default when absent.</param>
    /// <param name="libraryIds">Library ids, comma-separated; every library the caller can see when absent.</param>
    /// <param name="confidence">The confidences to keep, comma-separated (<c>Confirmed</c>, <c>Likely</c>, <c>Possible</c>,
    /// <c>Conflict</c>); all when absent.</param>
    /// <param name="search">A text filter on the title.</param>
    /// <param name="sort"><c>Confidence</c> (the default), <c>Title</c>, <c>Reclaimable</c>, <c>Size</c> or <c>Newest</c>.</param>
    /// <param name="startIndex">The page's first group (0).</param>
    /// <param name="limit">The page size, 1 to 200 (50).</param>
    /// <param name="cancellationToken">The request's token.</param>
    /// <response code="200">The page.</response>
    /// <response code="202">The server has no index yet; the first build runs.</response>
    /// <response code="400">An option is not valid, or given more than once.</response>
    /// <response code="404">Duplicates is turned off in the settings.</response>
    /// <response code="503">The server has no index because the last build failed, and none runs: Rebuild tries again.</response>
    /// <returns>The page, or the first build's state.</returns>
    [HttpGet("Groups")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(LibraryMaintenanceDuplicateGroupsPendingDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<LibraryMaintenanceDuplicateGroupsResultDto>> GetLibraryMaintenanceDuplicateGroups(
        [FromQuery] string? mode,
        [FromQuery] string? unknownYear,
        [FromQuery] string? libraryIds,
        [FromQuery] string? confidence,
        [FromQuery] string? search,
        [FromQuery] string? sort,
        [FromQuery] int? startIndex,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        RequireEnabled();
        RequireSingleValues(nameof(mode), nameof(unknownYear), nameof(libraryIds), nameof(confidence), nameof(search), nameof(sort), nameof(startIndex), nameof(limit));
        var page = await _groups.GetGroupsAsync(mode, unknownYear, libraryIds, confidence, search, sort, startIndex, limit, Caller(), cancellationToken).ConfigureAwait(false);
        return page is null ? Accepted(_groups.GetPending()) : Ok(page);
    }

    /// <summary>
    /// Gets one duplicate group by its id, grouped with the same options and libraries as the page it came from (the id
    /// depends on them).
    /// </summary>
    /// <param name="groupId">The group id.</param>
    /// <param name="mode">The match mode, as for the groups.</param>
    /// <param name="unknownYear">The unknown-year policy.</param>
    /// <param name="libraryIds">The libraries.</param>
    /// <param name="cancellationToken">The request's token.</param>
    /// <response code="200">The group.</response>
    /// <response code="202">The server has no index yet; the first build runs.</response>
    /// <response code="400">An option is not valid, or given more than once.</response>
    /// <response code="404">No such group is left, or Duplicates is turned off in the settings.</response>
    /// <response code="503">The server has no index because the last build failed, and none runs.</response>
    /// <returns>The group, or the first build's state.</returns>
    [HttpGet("Groups/{groupId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(LibraryMaintenanceDuplicateGroupsPendingDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<LibraryMaintenanceDuplicateGroupDto>> GetLibraryMaintenanceDuplicateGroup(
        [FromRoute] string groupId,
        [FromQuery] string? mode,
        [FromQuery] string? unknownYear,
        [FromQuery] string? libraryIds,
        CancellationToken cancellationToken)
    {
        RequireEnabled();
        RequireSingleValues(nameof(mode), nameof(unknownYear), nameof(libraryIds));
        var group = await _groups.GetGroupAsync(groupId, mode, unknownYear, libraryIds, Caller(), cancellationToken).ConfigureAwait(false);
        return group is null ? Accepted(_groups.GetPending()) : Ok(group);
    }

    // A query option given twice (`?mode=Title&mode=Title`) is a 400, not the first one silently; names
    // compare ignoring case, as ASP.NET Core's query does. A list is one value, comma-separated.
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

    private void RequireEnabled()
    {
        if (!_index.IsEnabled)
        {
            throw new PluginException(PluginErrorKind.NotFound, "Duplicates is turned off in Library Maintenance's settings.");
        }
    }
}
