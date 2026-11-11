using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AccessRoles.Api;

/// <summary>
/// The web client's health reports (the kit's).
/// </summary>
[ApiController]
[Authorize]
[Route("AccessRoles/Diagnostics")]
[KitAbiGate]
[AccessRolesInertGate]
[PluginExceptionFilter]
public class AccessRolesDiagnosticsController : ControllerBase
{
    private readonly IKitDiagnostics _diagnostics;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessRolesDiagnosticsController"/> class.
    /// </summary>
    /// <param name="diagnostics">The kit's diagnostics.</param>
    public AccessRolesDiagnosticsController(IKitDiagnostics diagnostics)
    {
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Receives a browser's report of jellyfin-web elements the Access Roles web client couldn't find:
    /// <c>{WebVersion|null, Client, ClientVersion, Layout, Experimental, PluginVersion, Missing:[{Anchor, View, Count}]}</c>.
    /// Every field is validated against the plugin's own list of elements and fixed patterns, and one report per user is
    /// accepted every ten minutes; anything else is dropped without an error.
    /// </summary>
    /// <response code="204">The report was received (whether it was kept or dropped).</response>
    /// <response code="403">API keys can't report; there is no user.</response>
    /// <response code="413">The report is over 16 KB.</response>
    /// <returns>No content.</returns>
    [HttpPost("ClientHealth")]
    [RequestSizeLimit(ClientHealthReport.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public Task<IActionResult> PostAccessRolesClientHealth() => _diagnostics.ReceiveClientHealthAsync(HttpContext);
}
