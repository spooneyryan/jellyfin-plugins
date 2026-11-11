using System.Collections.Generic;
using System.Net.Mime;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.TagCloud.Api.Models;
using Jellyfin.Plugin.TagCloud.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TagCloud.Api;

/// <summary>
/// The admin's endpoints: the kit's Diagnostics, and the index's status and rebuild.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("TagCloud/Admin")]
[Produces(MediaTypeNames.Application.Json)]
[KitAbiGate]
[PluginExceptionFilter]
public class TagCloudAdminController : ControllerBase
{
    private readonly IKitDiagnostics _diagnostics;
    private readonly ITagCloudAdmin _admin;

    /// <summary>
    /// Initializes a new instance of the <see cref="TagCloudAdminController"/> class.
    /// </summary>
    /// <param name="diagnostics">The kit's diagnostics.</param>
    /// <param name="admin">The index's admin view.</param>
    public TagCloudAdminController(IKitDiagnostics diagnostics, ITagCloudAdmin admin)
    {
        _diagnostics = diagnostics;
        _admin = admin;
    }

    /// <summary>
    /// Gets the plugin's diagnostics: the kit's sections (build, startup, web anchors, users, library changes), the saved
    /// clouds' store and the index.
    /// </summary>
    /// <response code="200">The diagnostics, by section.</response>
    /// <returns>The diagnostics.</returns>
    [HttpGet("Diagnostics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IDictionary<string, object?>> GetTagCloudDiagnostics() => Ok(_diagnostics.Describe());

    /// <summary>
    /// Gets the index's state: titles and words, when and how it was built, how long that took.
    /// </summary>
    /// <response code="200">The index's status.</response>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<TagCloudStatusDto> GetTagCloudStatus() => Ok(_admin.GetStatus());

    /// <summary>
    /// Asks for a full rebuild of the index, which runs in the background; poll the status to see it finish.
    /// </summary>
    /// <response code="200">The status as the rebuild is asked for.</response>
    /// <returns>The status.</returns>
    [HttpPost("Rebuild")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<TagCloudStatusDto> PostTagCloudRebuild() => Ok(_admin.RequestRebuild());
}
