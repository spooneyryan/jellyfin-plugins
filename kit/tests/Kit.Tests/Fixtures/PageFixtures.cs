using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Web.Pages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Kit.Tests.Fixtures;

/// <summary>A plugin's pages controller, as the kit's user pages and IKitPages describe it.</summary>
[ApiController]
[Authorize]
[Route("Fx/Pages")]
[KitAbiGate]
[PluginExceptionFilter]
public sealed class FxPagesController : ControllerBase
{
    private readonly IKitPages _pages;

    public FxPagesController(IKitPages pages)
    {
        _pages = pages;
    }

    [HttpGet("{key}")]
    [Produces("text/html")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetPage([FromRoute, Required] string key) => _pages.Shell(key);
}
