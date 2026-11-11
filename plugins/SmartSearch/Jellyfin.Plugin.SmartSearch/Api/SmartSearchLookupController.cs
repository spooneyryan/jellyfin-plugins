using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
/// The rule builder's lookups, each scoped to the signed-in user: their libraries, a field's values with counts, and
/// series by name. A signed-in user only (403 for an API key); 403 "turned off" while the search page is off. Both are
/// checked before the parameters are read, and parameters that don't bind are a 400 in the kit's ProblemDetails shape.
/// </summary>
[ApiController]
[Authorize]
[Route("SmartSearch")]
[KitAbiGate]
[PluginExceptionFilter]
[SearchPagePreflight]
[LookupParameters]
public class SmartSearchLookupController : ControllerBase
{
    private readonly ISmartSearchLookups _lookups;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmartSearchLookupController"/> class.
    /// </summary>
    /// <param name="lookups">The lookups.</param>
    /// <param name="userManager">The user manager.</param>
    public SmartSearchLookupController(ISmartSearchLookups lookups, IUserManager userManager)
    {
        _lookups = lookups;
        _userManager = userManager;
    }

    /// <summary>Gets the libraries the signed-in user can open.</summary>
    /// <response code="200">The libraries.</response>
    /// <response code="403">An API key, a user who no longer exists, or the search page is turned off.</response>
    /// <returns>The user's libraries.</returns>
    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IReadOnlyList<SmartSearchLibraryDto>> GetSmartSearchLibraries()
        => Ok(_lookups.GetLibraries(User.RequireUser(_userManager)));

    /// <summary>
    /// Gets the values of a field (<c>Genres</c>, <c>Tags</c>, <c>Studios</c>, <c>OfficialRating</c>, <c>Container</c>)
    /// with how many items carry each, counting only the items the signed-in user can see.
    /// </summary>
    /// <param name="fieldId">The field.</param>
    /// <param name="prefix">Text the value contains, ignoring case and accents; values that start with it come first.</param>
    /// <param name="libraryIds">Libraries to count in (repeat the parameter for several); none for all of the user's.</param>
    /// <param name="limit">The most values, 1 to 100.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <response code="200">The values, most common first.</response>
    /// <response code="400">The field has no values to suggest, or the limit or text is out of range.</response>
    /// <response code="403">An API key, a user who no longer exists, or the search page is turned off.</response>
    /// <response code="503">The server couldn't read the libraries in time (<c>Busy</c>).</response>
    /// <returns>The values.</returns>
    [HttpGet("Values/{fieldId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<SmartSearchValueDto>>> GetSmartSearchValues(
        [FromRoute] string fieldId,
        [FromQuery] string? prefix,
        [FromQuery] Guid[]? libraryIds,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
        => Ok(await _lookups.GetValuesAsync(User.RequireUser(_userManager), fieldId, prefix, libraryIds, limit, cancellationToken).ConfigureAwait(false));

    /// <summary>Gets the series the signed-in user can see whose name contains the text, ignoring case and accents.</summary>
    /// <param name="prefix">The text, at least 2 characters.</param>
    /// <param name="limit">The most series, 1 to 100.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <response code="200">The series, those whose name starts with the text first.</response>
    /// <response code="400">The text is shorter than 2 characters, or the limit is out of range.</response>
    /// <response code="403">An API key, a user who no longer exists, or the search page is turned off.</response>
    /// <response code="503">The server couldn't read the libraries in time (<c>Busy</c>).</response>
    /// <returns>The series.</returns>
    [HttpGet("Series")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<SmartSearchSeriesDto>>> GetSmartSearchSeries(
        [FromQuery] string? prefix,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
        => Ok(await _lookups.GetSeriesAsync(User.RequireUser(_userManager), prefix, limit, cancellationToken).ConfigureAwait(false));
}
