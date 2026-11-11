using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Kit.Web.Pages;

/// <summary>
/// The shells of the plugin's user pages (<c>GET /&lt;Route&gt;/Pages/{key}</c>). The plugin's
/// pages controller takes it in its constructor:
/// <code>
/// [ApiController] [Authorize] [Route("UserRatings/Pages")] [KitAbiGate] [PluginExceptionFilter]
/// public class UserRatingsPagesController(IKitPages pages) : ControllerBase
/// {
///     [HttpGet("{key}")] [Produces("text/html")] public IActionResult GetUserRatingsPage([FromRoute] string key) => pages.Shell(key);
/// }
/// </code>
/// </summary>
/// <remarks>
/// Public only because a plugin's public controller can't take an internal type in its constructor (CS0051; kit types
/// are otherwise internal). <c>AddKitPages</c> registers the kit's implementation. The controller must carry
/// <c>[Authorize]</c>: Plugin Pages' host fetches the shell with the user's token.
/// </remarks>
public interface IKitPages
{
    /// <summary>
    /// Gets the shell of a page: <c>text/html; charset=utf-8</c> with <c>Cache-Control: no-cache</c>. For an enabled page,
    /// the page root <c>&lt;div class="&lt;pfx&gt;-page" data-&lt;pfx&gt;-page="&lt;key&gt;"&gt;</c> holding a plain-text
    /// note that shows only when our script doesn't run; for a known page the admin turned off, 200 with a script-free
    /// "&lt;Title&gt; is turned off by your server admin." note and no page marker; for any other key, 404.
    /// </summary>
    /// <param name="key">The page key from the route.</param>
    /// <returns>The result, evaluated with the request's services.</returns>
    IActionResult Shell(string key);
}
