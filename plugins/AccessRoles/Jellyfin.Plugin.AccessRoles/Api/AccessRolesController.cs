using System;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AccessRoles.Api;

/// <summary>
/// Access Roles' user endpoints: the web client's script, since 0.9.5 <c>GET /AccessRoles/Me</c>, the
/// caller's own locks, and since 0.9.8 the admin page's module.
/// </summary>
[ApiController]
[Authorize]
[Route("AccessRoles")]
[KitAbiGate]
[AccessRolesInertGate]
[PluginExceptionFilter]
public class AccessRolesController : ControllerBase
{
    private readonly IEmbeddedScript _scripts;
    private readonly IAccessRolesMe? _me;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessRolesController"/> class.
    /// </summary>
    /// <param name="scripts">The kit's embedded scripts.</param>
    /// <param name="me">The caller's own locks; not registered only while the plugin is inert (the emergency stop at the
    /// server's start), when <see cref="AccessRolesInertGateAttribute"/> answers <c>/Me</c> before the controller is built
    /// and the admin page's script is all this controller serves.</param>
    public AccessRolesController(IEmbeddedScript scripts, IAccessRolesMe? me = null)
    {
        _scripts = scripts;
        _me = me;
    }

    /// <summary>
    /// Gets the web client script <c>index.html</c> loads: anonymous and static, holding no user data,
    /// with the build's MVID as its ETag.
    /// </summary>
    /// <response code="200">The script, <c>text/javascript</c>.</response>
    /// <response code="304">The browser's copy is current.</response>
    /// <returns>The script, or 304.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public IActionResult GetAccessRolesClientScript() => _scripts.ClientScript();

    /// <summary>
    /// Gets the admin page's ES module (since 0.9.8): the kit and the plugin's
    /// <c>Web/admin/js</c>, which the dashboard page's <c>AccessRolesJs</c> stub imports and mounts. Anonymous and static like
    /// the ClientScript: it holds no data; every value the page shows comes from the <c>RequiresElevation</c> admin API.
    /// </summary>
    /// <response code="200">The module, <c>text/javascript</c>.</response>
    /// <response code="304">The browser's copy is current.</response>
    /// <returns>The module, or 304.</returns>
    [HttpGet("AdminScript")]
    [AllowAnonymous]
    [AllowWhileInert]
    [Produces("text/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public IActionResult GetAccessRolesAdminScript() => _scripts.AdminScript();

    /// <summary>
    /// Gets the signed-in user's own locks: whether Access Roles manages them, the locked sections and
    /// surfaces (none for an administrator), whether every Settings section is locked, and whether they can open no library.
    /// The web guard caches the answer.
    /// </summary>
    /// <response code="200">The caller's locks.</response>
    /// <response code="403">An API key: there is no user.</response>
    /// <response code="503">Access Roles can't read its data just now.</response>
    /// <returns>The answer, <c>AccessRolesMeDto</c>.</returns>
    [HttpGet("Me")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<AccessRolesMeDto> GetAccessRolesMe()
    {
        var me = _me ?? throw new InvalidOperationException("Access Roles' /Me isn't registered.");
        Response.Headers.CacheControl = "no-store";
        return me.GetMe(User);
    }
}
