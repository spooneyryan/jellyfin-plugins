using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SmartSearch.Api;

/// <summary>
/// The signed-in user's saved queries: list, open, save, change, duplicate, delete and run. Every one needs a
/// signed-in user (403 for an API key: queries belong to users) and answers 403 "turned off" while the search page is
/// off; both come before the body or parameters are read. A private query of someone else is a 404, as an id that
/// doesn't exist; a shared query runs as the user who runs it, never as its owner.
/// </summary>
[ApiController]
[Authorize]
[Route("SmartSearch/Queries")]
[KitAbiGate]
[PluginExceptionFilter]
[SearchPagePreflight]
[LookupParameters]
public class SmartSearchQueriesController : ControllerBase
{
    private readonly ISmartSearchSavedQueries _queries;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmartSearchQueriesController"/> class.
    /// </summary>
    /// <param name="queries">The saved queries.</param>
    /// <param name="userManager">The user manager.</param>
    public SmartSearchQueriesController(ISmartSearchSavedQueries queries, IUserManager userManager)
    {
        _queries = queries;
        _userManager = userManager;
    }

    /// <summary>
    /// Lists saved queries, ordered by name: the caller's own (<c>Mine</c>), those others share (<c>Shared</c>), or both
    /// (<c>All</c>, the default).
    /// </summary>
    /// <param name="scope"><c>Mine</c>, <c>Shared</c> or <c>All</c>.</param>
    /// <param name="startIndex">The index of the page's first query.</param>
    /// <param name="limit">The page size, 1 to 500.</param>
    /// <response code="200">The page.</response>
    /// <response code="400">A parameter isn't valid (<c>InvalidRequest</c>, with <c>errors</c>).</response>
    /// <response code="403">An API key, a user who no longer exists, or the search page is turned off.</response>
    /// <returns>The page of queries.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SmartSearchSavedQueryListDto>> GetSmartSearchQueries([FromQuery] string? scope, [FromQuery] int startIndex = 0, [FromQuery] int limit = 100)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        return _queries.List(user, isAdmin, scope, startIndex, limit);
    }

    /// <summary>
    /// Gets a saved query with its definition: the caller's own, one shared with them, or, for an administrator, any.
    /// </summary>
    /// <param name="id">The query's id.</param>
    /// <response code="200">The query.</response>
    /// <response code="400">The id isn't one (<c>InvalidRequest</c>).</response>
    /// <response code="403">An API key, a user who no longer exists, or the search page is turned off.</response>
    /// <response code="404">No query of this id that the caller may open (<c>QueryNotFound</c>).</response>
    /// <returns>The query, with its definition.</returns>
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SmartSearchSavedQueryDto>> GetSmartSearchQuery([FromRoute] Guid id)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        return _queries.Get(user, isAdmin, id);
    }

    /// <summary>
    /// Saves a new query, owned by the caller and private unless <c>Shared</c>. The query is checked as for a search by
    /// the caller; one that uses a field this server can't answer now is saved, but can't be run.
    /// </summary>
    /// <param name="request">The name, description, sharing and query, read strictly.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="201">The saved query.</response>
    /// <response code="400">The body or the query isn't valid (<c>InvalidRequest</c>, <c>InvalidQuery</c>, with <c>errors</c>).</response>
    /// <response code="403">An API key, a user who no longer exists, the search page turned off, an administrator-only
    /// field sent by another user (<c>AdminOnlyField</c>), or sharing that isn't allowed (<c>SharingDisabled</c>,
    /// <c>SharedAdminOnlyField</c>).</response>
    /// <response code="409">The caller already has a query of this name (<c>DuplicateName</c>) or as many as allowed (<c>QuotaReached</c>).</response>
    /// <response code="413">The request is over 72 KB, or its query over 64 KB.</response>
    /// <response code="503">The saved queries can't be written now (read-only or unreadable).</response>
    /// <returns>The new query.</returns>
    [HttpPost]
    [RequestSizeLimit(SmartSearchRequestLimits.MaxSaveBodyBytes)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SmartSearchSavedQueryDto>> CreateSmartSearchQuery([SmartSearchBody] SmartSearchSavedQueryRequestDto request, CancellationToken cancellationToken)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        var saved = await _queries.CreateAsync(user, isAdmin, request, cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(GetSmartSearchQuery), new { id = saved.Id }, saved);
    }

    /// <summary>
    /// Changes a saved query: its owner, or an administrator. <c>IfUpdatedAt</c> is the <c>UpdatedUtc</c> last read; a
    /// query changed since is refused. When auto-collections use the query, they check the change first.
    /// </summary>
    /// <param name="id">The query's id.</param>
    /// <param name="request">The name, description, sharing, query and <c>IfUpdatedAt</c>, read strictly.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The changed query, with the auto-collections it affects.</response>
    /// <response code="400">The body or the query isn't valid.</response>
    /// <response code="403">As for a new query; also someone else's shared query (<c>NotOwner</c>), an administrator-only
    /// field in someone else's query, or a query auto-collections use, changed by someone who doesn't manage collections
    /// (<c>ManagerRequired</c>).</response>
    /// <response code="404">No query of this id that the caller may open.</response>
    /// <response code="409">Changed since it was read (<c>IfUpdatedAt</c>), a name taken (<c>DuplicateName</c>), written by
    /// a newer version (<c>NeedsNewerVersion</c>), or refused by the auto-collections that use it
    /// (<c>BreaksAutoCollections</c>, <c>AutoCollectionsNeedAttention</c>, with <c>autoCollections</c>).</response>
    /// <response code="413">The request is over 72 KB, or its query over 64 KB.</response>
    /// <response code="503">The saved queries can't be written now.</response>
    /// <returns>The changed query.</returns>
    [HttpPut("{id}")]
    [RequestSizeLimit(SmartSearchRequestLimits.MaxSaveBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SmartSearchSavedQueryDto>> UpdateSmartSearchQuery([FromRoute] Guid id, [SmartSearchBody] SmartSearchSavedQueryRequestDto request, CancellationToken cancellationToken)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        return await _queries.UpdateAsync(user, isAdmin, id, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies a query the caller may open into a new private query of theirs, named as given or as the original (with a
    /// number when the caller already uses the name). A query written by a newer version is copied as it is.
    /// </summary>
    /// <param name="id">The query's id.</param>
    /// <param name="request">The copy's name; the body may be left out.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="201">The copy.</response>
    /// <response code="400">The body isn't valid.</response>
    /// <response code="403">An API key, a user who no longer exists, the search page turned off, or a query with an
    /// administrator-only field copied by another user.</response>
    /// <response code="404">No query of this id that the caller may open.</response>
    /// <response code="409">The name is taken (<c>DuplicateName</c>) or the caller has as many queries as allowed (<c>QuotaReached</c>).</response>
    /// <response code="503">The saved queries can't be written now.</response>
    /// <returns>The new copy.</returns>
    [HttpPost("{id}/Duplicate")]
    [RequestSizeLimit(SmartSearchRequestLimits.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SmartSearchSavedQueryDto>> DuplicateSmartSearchQuery([FromRoute] Guid id, [SmartSearchBody] SmartSearchSavedQueryDuplicateDto request, CancellationToken cancellationToken)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        var copy = await _queries.DuplicateAsync(user, isAdmin, id, request, cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(GetSmartSearchQuery), new { id = copy.Id }, copy);
    }

    /// <summary>
    /// Deletes a saved query: its owner, or an administrator. When auto-collections use it, the delete is refused unless
    /// <c>detachAutoCollections</c> is true: they then keep a copy of the query, saved before the query is deleted.
    /// </summary>
    /// <param name="id">The query's id.</param>
    /// <param name="detachAutoCollections">Whether auto-collections that use it keep a copy instead.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="204">Deleted.</response>
    /// <response code="400">A parameter isn't valid.</response>
    /// <response code="403">An API key, a user who no longer exists, the search page turned off, or someone else's shared query (<c>NotOwner</c>).</response>
    /// <response code="404">No query of this id that the caller may open.</response>
    /// <response code="409">Auto-collections use it (<c>Referenced</c>, with <c>autoCollections</c>).</response>
    /// <response code="503">The saved queries can't be written now.</response>
    /// <returns>Nothing.</returns>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> DeleteSmartSearchQuery([FromRoute] Guid id, [FromQuery] bool detachAutoCollections = false, CancellationToken cancellationToken = default)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        await _queries.DeleteAsync(user, isAdmin, id, detachAutoCollections, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Runs a saved query the caller may open, as the caller (their libraries, parental limit and watch state; for a
    /// shared query never the owner's), and returns a page as <c>/SmartSearch/Search</c> does.
    /// </summary>
    /// <param name="id">The query's id.</param>
    /// <param name="request">The page; the body may be left out for the first.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <response code="200">The page.</response>
    /// <response code="400">The body isn't valid, the query has errors, would check too many items, or uses a field this
    /// server can't answer (as for <c>/Search</c>).</response>
    /// <response code="403">An API key, a user who no longer exists, the search page turned off, or an
    /// administrator-only field and a caller who isn't one.</response>
    /// <response code="404">No query of this id that the caller may open.</response>
    /// <response code="409">Written by a newer version (<c>NeedsNewerVersion</c>).</response>
    /// <response code="503">The search took too long or the server is running other searches.</response>
    /// <returns>A page of results.</returns>
    [HttpPost("{id}/Search")]
    [RequestSizeLimit(SmartSearchRequestLimits.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SmartSearchResultDto>> SearchSmartSearchQuery([FromRoute] Guid id, [SmartSearchBody] SmartSearchSavedQueryRunDto request, CancellationToken cancellationToken)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        return await _queries.RunAsync(user, isAdmin, id, request, cancellationToken).ConfigureAwait(false);
    }
}
