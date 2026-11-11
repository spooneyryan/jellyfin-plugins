using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Web.Pages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Bookmarks.Api;

/// <summary>
/// The shells of Bookmarks' user pages, built by the kit: <c>bookmarks</c>, the My Bookmarks page (since 1.1.0; on while
/// the setting <c>EnableBookmarksPage</c> is, else a 200 with the note "My Bookmarks is turned off by your server admin."
/// and no page marker, so Plugin Pages' host and the fallback route both show it), <c>clips</c>, the My Clips page (since
/// 1.2.0; the same with <c>EnableClipsPage</c> and "My Clips is turned off by your server admin."), and the page-hosting
/// demo's (<c>demo-a</c>, <c>demo-b</c>), turned off unless the developer setting <c>EnablePageDemo</c> is on. Any other
/// key is a 404.
/// </summary>
[ApiController]
[Authorize]
[Route("Bookmarks/Pages")]
[KitAbiGate]
[PluginExceptionFilter]
public class BookmarksPagesController : ControllerBase
{
    private readonly IKitPages _pages;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarksPagesController"/> class.
    /// </summary>
    /// <param name="pages">The page shells, built by the kit.</param>
    public BookmarksPagesController(IKitPages pages)
    {
        _pages = pages;
    }

    /// <summary>
    /// Gets the HTML shell of a user page, which the Plugin Pages plugin's host inserts and the web client's script
    /// mounts. For a known page that is turned off, a plain-text "turned off" note instead.
    /// </summary>
    /// <param name="key">The page key: <c>bookmarks</c>, <c>clips</c>, or the demo's <c>demo-a</c> and <c>demo-b</c>.</param>
    /// <response code="200">The shell, or the note of a page that is turned off.</response>
    /// <response code="404">The plugin has no page with that key.</response>
    /// <returns>The HTML fragment.</returns>
    [HttpGet("{key}")]
    [Produces(MediaTypeNames.Text.Html)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetBookmarksPage([FromRoute, Required] string key) => _pages.Shell(key);
}
