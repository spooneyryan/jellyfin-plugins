using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SmartSearch.Api;

/// <summary>
/// Smart Search's user endpoints: the web client's script, the field catalogue, the search and the query check. Every
/// one but the script needs a signed-in user (403 for an API key: a search is scoped by its user) and answers 403
/// "turned off" while the search page is off; for the search and the check both come before the body is read. The lookups are <see cref="SmartSearchLookupController"/>.
/// </summary>
[ApiController]
[Authorize]
[Route("SmartSearch")]
[KitAbiGate]
[PluginExceptionFilter]
public class SmartSearchController : ControllerBase
{
    private readonly IEmbeddedScript _scripts;
    private readonly ISmartSearchFields _fields;
    private readonly ISmartSearchService _search;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmartSearchController"/> class.
    /// </summary>
    /// <param name="scripts">The web client's script, built by the kit.</param>
    /// <param name="fields">The field catalogue.</param>
    /// <param name="search">The search and the query check.</param>
    /// <param name="userManager">The user manager.</param>
    public SmartSearchController(IEmbeddedScript scripts, ISmartSearchFields fields, ISmartSearchService search, IUserManager userManager)
    {
        _scripts = scripts;
        _fields = fields;
        _search = search;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets the web client script <c>index.html</c> loads: anonymous and static, holding no user data, with the
    /// build's MVID as its ETag.
    /// </summary>
    /// <response code="200">The script, <c>text/javascript</c>.</response>
    /// <response code="304">The browser's copy is current.</response>
    /// <returns>The script, or 304.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public IActionResult GetSmartSearchClientScript() => _scripts.ClientScript();

    /// <summary>
    /// Gets the fields, sort keys and item kinds a query may use, for the rule builder. Administrator-only fields
    /// (<c>Path</c>) are listed only for callers that pass Jellyfin's <c>RequiresElevation</c> policy. <c>AutoCollections</c>
    /// says whether auto-collections are on and whether the caller manages them.
    /// </summary>
    /// <response code="200">The fields, sort keys and item kinds.</response>
    /// <response code="403">The request authenticated with an API key, or its user no longer exists.</response>
    /// <returns>The catalogue.</returns>
    [HttpGet("Fields")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SmartSearchFieldCatalogDto>> GetSmartSearchFields()
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        var catalog = _fields.GetCatalog(isAdmin);
        catalog.AutoCollections = _fields.GetAutoCollectionAccess(isAdmin, CurrentUser.HasAny(user, PermissionKind.EnableCollectionManagement));
        return catalog;
    }

    /// <summary>
    /// Searches as the signed-in user and returns a page. The first page evaluates the query and returns a
    /// <c>ResultToken</c>; later pages send it back with the same query (a token that has expired evaluates again, with
    /// the warning <c>Reevaluated</c>). Results are only what the user may see: their libraries, parental limit and
    /// tags, one row per merged-version set and presentation key.
    /// </summary>
    /// <param name="request">The query and the page, read strictly (unknown or repeated properties are refused).</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <response code="200">The page.</response>
    /// <response code="400">The request or the query is invalid (<c>InvalidRequest</c>, <c>InvalidQuery</c>, with
    /// <c>errors</c>), the query would check too many items (<c>QueryTooBroad</c>, with <c>candidateCount</c> and
    /// <c>kinds</c>), or it uses a field this server can't answer (<c>FieldUnavailable</c>).</response>
    /// <response code="403">An API key, a user who no longer exists, an administrator-only field sent by another user
    /// (<c>AdminOnlyField</c>), or the search page is turned off.</response>
    /// <response code="413">The request is over 68 KB, or its query over 64 KB.</response>
    /// <response code="503">The search took too long (<c>Timeout</c>) or the server is running other searches (<c>Busy</c>).</response>
    /// <returns>A page of results.</returns>
    [HttpPost("Search")]
    [SearchPagePreflight]
    [RequestSizeLimit(SmartSearchRequestLimits.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SmartSearchResultDto>> SearchSmartSearch([SmartSearchBody] SmartSearchRequestDto request, CancellationToken cancellationToken)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        return await _search.SearchAsync(user, isAdmin, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks a query for the signed-in user: its errors, each with its path inside the query, and what it uses; for an
    /// administrator also the path a search of it would take. A query with errors is still a 200, with <c>Valid</c> false.
    /// </summary>
    /// <param name="request">The query, read strictly.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <response code="200">What was found.</response>
    /// <response code="400">The body isn't a validate request (<c>InvalidRequest</c>), or, for an administrator, the query
    /// would check too many items to plan (<c>QueryTooBroad</c>).</response>
    /// <response code="403">An API key, a user who no longer exists, or the search page is turned off.</response>
    /// <response code="413">The request is over 68 KB, or its query over 64 KB.</response>
    /// <response code="503">For an administrator, planning took too long (<c>Timeout</c>) or the server is running other
    /// searches (<c>Busy</c>).</response>
    /// <returns>The query's check.</returns>
    [HttpPost("Validate")]
    [SearchPagePreflight]
    [RequestSizeLimit(SmartSearchRequestLimits.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SmartSearchValidationDto>> ValidateSmartSearchQuery([SmartSearchBody] SmartSearchValidateRequestDto request, CancellationToken cancellationToken)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        return await _search.ValidateAsync(user, isAdmin, request, cancellationToken).ConfigureAwait(false);
    }
}
