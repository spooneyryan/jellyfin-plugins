using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Web.Pages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TagCloud.Api;

/// <summary>
/// The shell of the Tag Cloud page, built by the kit: <c>cloud</c>, on while the setting
/// <c>EnableTagCloudPage</c> is, else a 200 with the note "Tag Cloud is turned off by your server admin." and no page
/// marker. Any other key is a 404.
/// </summary>
[ApiController]
[Authorize]
[Route("TagCloud/Pages")]
[KitAbiGate]
[PluginExceptionFilter]
public class TagCloudPagesController : ControllerBase
{
    private readonly IKitPages _pages;

    /// <summary>
    /// Initializes a new instance of the <see cref="TagCloudPagesController"/> class.
    /// </summary>
    /// <param name="pages">The page shells, built by the kit.</param>
    public TagCloudPagesController(IKitPages pages)
    {
        _pages = pages;
    }

    /// <summary>
    /// Gets the HTML shell of the user page, which the Plugin Pages plugin's host inserts and the web client's script
    /// mounts. While it is turned off, a plain-text "turned off" note instead.
    /// </summary>
    /// <param name="key">The page key: <c>cloud</c>.</param>
    /// <response code="200">The shell, or the note of a page that is turned off.</response>
    /// <response code="404">The plugin has no page with that key.</response>
    /// <returns>The HTML fragment.</returns>
    [HttpGet("{key}")]
    [Produces(MediaTypeNames.Text.Html)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetTagCloudPage([FromRoute, Required] string key) => _pages.Shell(key);
}
