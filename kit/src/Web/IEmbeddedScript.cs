using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Kit.Web;

/// <summary>
/// The plugin's web client scripts, built by the kit. The plugin's controller takes it in
/// its constructor and returns its results from the two anonymous actions:
/// <code>
/// [HttpGet("ClientScript")] [AllowAnonymous] public IActionResult GetClientScript() => _scripts.ClientScript();
/// [HttpGet("AdminScript")]  [AllowAnonymous] public IActionResult GetAdminScript()  => _scripts.AdminScript();
/// </code>
/// </summary>
/// <remarks>
/// Public only because a plugin's public controller can't take an internal type in its constructor (CS0051; kit types
/// are otherwise internal). <see cref="KitWebServiceCollectionExtensions.AddEmbeddedScript"/> registers the kit's
/// implementation.
/// Both scripts are static code built from embedded resources, with no user or item data.
/// </remarks>
public interface IEmbeddedScript
{
    /// <summary>
    /// Gets the result of <c>GET /&lt;Route&gt;/ClientScript</c>: the plugin's classic script (one IIFE of
    /// <c>KIT_BUILD</c>, <c>KIT_CSS</c>, <c>kit.js</c> and the plugin's <c>Web/js</c> files), served as
    /// <c>text/javascript; charset=utf-8</c> with <c>Cache-Control: no-cache</c> and the assembly MVID as its <c>ETag</c>,
    /// so a conditional request answers 304.
    /// </summary>
    /// <returns>The script, or a 503 ProblemDetails when it couldn't be built.</returns>
    IActionResult ClientScript();

    /// <summary>
    /// Gets the result of the optional <c>GET /&lt;Route&gt;/AdminScript</c>: an ES module of <c>kit.js</c> and the plugin's
    /// <c>Web/admin/js</c> files that exports <c>mount(view, params)</c>, for the plugin's dashboard page, with
    /// the same headers as <see cref="ClientScript"/>.
    /// </summary>
    /// <returns>The module, a 404 ProblemDetails when the plugin has no <c>Web/admin/js</c> files, or a 503 when it couldn't
    /// be built.</returns>
    IActionResult AdminScript();
}
