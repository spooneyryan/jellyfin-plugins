using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.TagCloud.Api.Models;
using Jellyfin.Plugin.TagCloud.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.TagCloud.Api;

/// <summary>
/// The clouds as the caller sees them: what the server offers, the caller's libraries, a cloud's words and a
/// word's titles, every count over only the titles the caller can see; and the web client's script.
/// </summary>
[ApiController]
[Authorize]
[Route("TagCloud")]
[Produces(MediaTypeNames.Application.Json)]
[KitAbiGate]
[PluginExceptionFilter]
[StrictEnumQuery]
public class TagCloudController : ControllerBase
{
    private const string NoUserMessage = "The Tag Cloud counts a user's titles; API keys have no user.";

    private readonly ITagCloudQuery _query;
    private readonly IUserManager _userManager;
    private readonly IEmbeddedScript _scripts;

    /// <summary>
    /// Initializes a new instance of the <see cref="TagCloudController"/> class.
    /// </summary>
    /// <param name="query">The clouds.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="scripts">The web client's script, built by the kit.</param>
    public TagCloudController(ITagCloudQuery query, IUserManager userManager, IEmbeddedScript scripts)
    {
        _query = query;
        _userManager = userManager;
        _scripts = scripts;
    }

    /// <summary>
    /// Gets what this server's Tag Cloud offers: the sources turned on, the words slider's range, the TV layout's starting
    /// word count, whether clouds may be shared, and whether the caller is an administrator.
    /// </summary>
    /// <response code="200">The capabilities.</response>
    /// <returns>What the server offers the caller.</returns>
    [HttpGet("Capabilities")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<TagCloudCapabilitiesDto>> GetTagCloudCapabilities()
    {
        GetCurrentUser();
        return Ok(_query.GetCapabilities(await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false)));
    }

    /// <summary>
    /// Gets the libraries the caller can open that hold titles in a scope, by name, each with how many of them the caller
    /// can see, for the library picker.
    /// </summary>
    /// <param name="scope">Movies, Shows or Both.</param>
    /// <response code="200">The libraries, by name.</response>
    /// <response code="503">The index is being built for the first time; the message says how far it is.</response>
    /// <returns>The libraries.</returns>
    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<IReadOnlyList<TagCloudLibraryDto>> GetTagCloudLibraries([FromQuery] TagCloudScope scope = TagCloudScope.Movies)
        => Ok(_query.GetLibraries(GetCurrentUser(), scope));

    /// <summary>
    /// Gets a cloud's words over the caller's titles, heaviest first.
    /// </summary>
    /// <param name="source">What to count.</param>
    /// <param name="scope">Which titles.</param>
    /// <param name="libraryIds">Comma-separated library ids; none for every library the caller can open. Ids the caller
    /// can't open are ignored.</param>
    /// <param name="narrow">Word keys the titles must all carry (repeat the parameter for each, at most 10).</param>
    /// <param name="limit">The most words, from 25 to the server's limit (100 by default).</param>
    /// <response code="200">The words, heaviest first.</response>
    /// <response code="403">The cloud is turned off by the server admin.</response>
    /// <response code="503">The index is being built; the message says how far it is.</response>
    /// <returns>The words.</returns>
    [HttpGet("Words")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<TagCloudWordsDto> GetTagCloudWords(
        [FromQuery, Required] TagCloudSource source,
        [FromQuery] TagCloudScope scope = TagCloudScope.Movies,
        [FromQuery, CommaSeparatedQuery] IReadOnlyList<Guid>? libraryIds = null,
        [FromQuery] string[]? narrow = null,
        [FromQuery] int? limit = null)
        => Ok(_query.GetWords(GetCurrentUser(), source, scope, libraryIds, narrow, limit));

    /// <summary>
    /// Gets a page of one word's titles over the caller's titles. A word the caller's titles don't carry gives an empty
    /// page.
    /// </summary>
    /// <param name="source">What the word is.</param>
    /// <param name="key">The word's key.</param>
    /// <param name="scope">Which titles.</param>
    /// <param name="libraryIds">Comma-separated library ids; none for every library the caller can open.</param>
    /// <param name="narrow">Word keys the titles must also carry.</param>
    /// <param name="order">Name (A–Z) or Random (fixed by <paramref name="seed"/>).</param>
    /// <param name="seed">The shuffle's seed.</param>
    /// <param name="startIndex">How many to skip.</param>
    /// <param name="limit">The most to return, 1–200 (50 by default).</param>
    /// <response code="200">The page of titles.</response>
    /// <response code="403">The cloud is turned off by the server admin.</response>
    /// <response code="503">The index is being built.</response>
    /// <returns>The page.</returns>
    [HttpGet("Titles")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<TagCloudTitlesDto> GetTagCloudTitles(
        [FromQuery, Required] TagCloudSource source,
        [FromQuery, Required] string key,
        [FromQuery] TagCloudScope scope = TagCloudScope.Movies,
        [FromQuery, CommaSeparatedQuery] IReadOnlyList<Guid>? libraryIds = null,
        [FromQuery] string[]? narrow = null,
        [FromQuery] TagCloudTitleOrder order = TagCloudTitleOrder.Name,
        [FromQuery] int seed = 0,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 50)
        => Ok(_query.GetTitles(GetCurrentUser(), source, scope, libraryIds, narrow, key, order, seed, startIndex, limit));

    /// <summary>
    /// Gets the Tag Cloud web client's script: the kit's web client and Tag Cloud's page in one IIFE. The script tag that
    /// loads it is added to <c>index.html</c> by the plugin itself. Anonymous, because <c>index.html</c> loads it before
    /// anyone signs in; it carries no user data. The ETag is the build's module id, so a new build is fetched at once.
    /// </summary>
    /// <response code="200">The script, as JavaScript.</response>
    /// <response code="304">The browser's copy is current.</response>
    /// <returns>The script.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public IActionResult GetClientScript() => _scripts.ClientScript();

    // API-key requests have no user, and a cloud counts a user's titles: both get 403.
    private JellyfinUser GetCurrentUser() => User.RequireUser(_userManager, NoUserMessage);
}
