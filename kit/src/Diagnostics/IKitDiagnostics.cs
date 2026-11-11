using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Kit.Diagnostics;

/// <summary>
/// The kit's two diagnostics endpoints. The plugin's controllers take it in their
/// constructors and return its results:
/// <code>
/// [Authorize] [Route("&lt;Route&gt;/Diagnostics")] ...
/// [HttpPost("ClientHealth")] [RequestSizeLimit(ClientHealthReport.MaxBodyBytes)]
/// public Task&lt;IActionResult&gt; Post&lt;Route&gt;ClientHealth() => _diagnostics.ReceiveClientHealthAsync(HttpContext);
///
/// [Authorize(Policy = Policies.RequiresElevation)] [Route("&lt;Route&gt;/Admin")] ...
/// [HttpGet("Diagnostics")]
/// public ActionResult&lt;IDictionary&lt;string, object?&gt;&gt; Get&lt;Route&gt;Diagnostics() => Ok(_diagnostics.Describe());
/// </code>
/// </summary>
/// <remarks>
/// Public only because a plugin's public controller can't take an internal type in its constructor (CS0051; kit types
/// are otherwise internal). <c>AddKitDiagnostics</c> registers the kit's implementation.
/// </remarks>
public interface IKitDiagnostics
{
    /// <summary>
    /// Handles <c>POST /&lt;Route&gt;/Diagnostics/ClientHealth</c>: a browser's report of missing jellyfin-web anchors
    /// (<c>{WebVersion|null, Client, ClientVersion, Layout, Experimental, PluginVersion, Missing:[{Anchor, View, Count}]}</c>).
    /// Nothing in it is trusted: it is validated against the plugin's anchors and fixed patterns, rate-limited per user,
    /// and dropped silently when it fails, so the answer is 204 whatever happened to it.
    /// </summary>
    /// <param name="context">The request, from a signed-in user.</param>
    /// <returns>204; 413 ProblemDetails for a body over 16 KB.</returns>
    /// <exception cref="Api.PluginException">403 for API keys, which have no user to rate-limit.</exception>
    Task<IActionResult> ReceiveClientHealthAsync(HttpContext context);

    /// <summary>
    /// Builds the body of <c>GET /&lt;Route&gt;/Admin/Diagnostics</c> (admin only): <c>PluginVersion</c>,
    /// <c>KitVersion</c>, <c>Abi</c>, the plugin's registered sources (such as <c>Store</c>), <c>Pages</c>,
    /// <c>WebAnchors</c> (the declared anchors, the reporting setting, the report counts and <c>MaxEntries</c>) and
    /// <c>ClientHealth</c> (the accepted reports since the server started, at most <c>MaxEntries</c>, the ones updated longest
    /// ago making room for new ones). Keys and values are plain dictionaries, lists, strings, numbers and
    /// dates, so the server's JSON options write them unchanged; the plugin may add its own keys.
    /// </summary>
    /// <returns>The diagnostics.</returns>
    IDictionary<string, object?> Describe();
}
