using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Web.Pages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SmartSearch.Api;

/// <summary>
/// The shell of the Advanced Search page, built by the kit: <c>search</c>, on while the setting
/// <c>EnableSearchPage</c> is, else a 200 with the note "Advanced Search is turned off by your server admin." and no page
/// marker. Any other key is a 404.
/// </summary>
[ApiController]
[Authorize]
[Route("SmartSearch/Pages")]
[KitAbiGate]
[PluginExceptionFilter]
public class SmartSearchPagesController : ControllerBase
{
    private readonly IKitPages _pages;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmartSearchPagesController"/> class.
    /// </summary>
    /// <param name="pages">The page shells, built by the kit.</param>
    public SmartSearchPagesController(IKitPages pages)
    {
        _pages = pages;
    }

    /// <summary>
    /// Gets the HTML shell of the user page, which the Plugin Pages plugin's host inserts and the web client's script
    /// mounts. While it is turned off, a plain-text "turned off" note instead.
    /// </summary>
    /// <param name="key">The page key: <c>search</c>.</param>
    /// <response code="200">The shell, or the note of a page that is turned off.</response>
    /// <response code="404">The plugin has no page with that key.</response>
    /// <returns>The HTML fragment.</returns>
    [HttpGet("{key}")]
    [Produces(MediaTypeNames.Text.Html)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetSmartSearchPage([FromRoute, Required] string key) => _pages.Shell(key);
}
