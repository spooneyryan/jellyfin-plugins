using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LibraryMaintenance.Api;

/// <summary>
/// Library Maintenance's static and per-user endpoints: the Dashboard page's module, the web
/// client's script and the caller's capabilities.
/// </summary>
[ApiController]
[Authorize]
[Route("LibraryMaintenance")]
[KitAbiGate]
[PluginExceptionFilter]
public class LibraryMaintenanceController : ControllerBase
{
    /// <summary>The 403's message for an API key on <c>Capabilities</c>, which describes a signed-in user.</summary>
    internal const string CapabilitiesNeedUserMessage = "Capabilities describe a signed-in user; API keys can't ask for them.";

    private readonly IEmbeddedScript _scripts;
    private readonly IMoveMedia _move;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryMaintenanceController"/> class.
    /// </summary>
    /// <param name="scripts">The kit's embedded scripts.</param>
    /// <param name="move">Move media, for its capabilities.</param>
    public LibraryMaintenanceController(IEmbeddedScript scripts, IMoveMedia move)
    {
        _scripts = scripts;
        _move = move;
    }

    /// <summary>
    /// Gets the Dashboard page's ES module: the kit and the plugin's <c>Web/admin/js</c>, which the
    /// page's <c>LibraryMaintenanceJs</c> stub imports and mounts. Anonymous and static, like a ClientScript: it holds no
    /// data; every value the page shows comes from the <c>RequiresElevation</c> API.
    /// </summary>
    /// <response code="200">The module, <c>text/javascript</c>.</response>
    /// <response code="304">The browser's copy is current.</response>
    /// <returns>The module, or 304.</returns>
    [HttpGet("AdminScript")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public IActionResult GetLibraryMaintenanceAdminScript() => _scripts.AdminScript();

    /// <summary>
    /// Gets the web client's script: the kit and the plugin's <c>Web/js</c> in one IIFE, which
    /// the kit's middleware adds to <c>index.html</c> while <c>EnableWebUi</c> is on. It adds Move's entry to the item "⋮"
    /// menu of administrators and opens its dialog. Anonymous and static (the login page loads it too): it holds no data;
    /// every value it shows comes from the <c>RequiresElevation</c> API.
    /// </summary>
    /// <response code="200">The script, <c>text/javascript</c>.</response>
    /// <response code="304">The browser's copy is current.</response>
    /// <returns>The script, or 304.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public IActionResult GetLibraryMaintenanceClientScript() => _scripts.ClientScript();

    /// <summary>
    /// Gets what the signed-in user can do with Library Maintenance's web parts: whether they
    /// pass the <c>RequiresElevation</c> policy on this request, whether Move media is on and supported on this host,
    /// whether a planned move can run in this build (<c>CanMove</c>) and where its entries show. For a user who isn't an
    /// administrator every flag is false. Cosmetic: every admin endpoint checks again.
    /// </summary>
    /// <response code="200">The capabilities.</response>
    /// <response code="403">An API key: capabilities describe a signed-in user.</response>
    /// <returns>The caller's capabilities.</returns>
    [HttpGet("Capabilities")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<LibraryMaintenanceCapabilitiesDto>> GetLibraryMaintenanceCapabilities()
    {
        if (User.IsApiKey())
        {
            throw PluginException.Forbidden(CapabilitiesNeedUserMessage);
        }

        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        return Ok(_move.GetCapabilities(isAdmin));
    }
}
