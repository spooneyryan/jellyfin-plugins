using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.TagCloud.Api.Models;
using Jellyfin.Plugin.TagCloud.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.TagCloud.Api;

/// <summary>
/// Saved clouds: the caller's own and the ones shared with everyone. A cloud keeps a configuration,
/// never counts.
/// </summary>
[ApiController]
[Authorize]
[Route("TagCloud/Clouds")]
[Produces(MediaTypeNames.Application.Json)]
[KitAbiGate]
[PluginExceptionFilter]
public class TagCloudCloudsController : ControllerBase
{
    private const string NoUserMessage = "Saved clouds belong to a user; API keys have no user.";

    /// <summary>The largest body a save may send.</summary>
    private const int MaxBodyBytes = 32 * 1024;

    private readonly ITagCloudClouds _clouds;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="TagCloudCloudsController"/> class.
    /// </summary>
    /// <param name="clouds">The saved clouds.</param>
    /// <param name="userManager">The user manager.</param>
    public TagCloudCloudsController(ITagCloudClouds clouds, IUserManager userManager)
    {
        _clouds = clouds;
        _userManager = userManager;
    }

    /// <summary>
    /// Lists the clouds the caller may open: their own, in the order saved, then the ones others share, by name.
    /// </summary>
    /// <response code="200">The clouds, the caller's own first.</response>
    /// <returns>The clouds.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<TagCloudSavedCloudDto>> GetTagCloudClouds() => Ok(_clouds.List(GetCurrentUser()));

    /// <summary>
    /// Gets one cloud the caller may open.
    /// </summary>
    /// <param name="id">The cloud's id.</param>
    /// <response code="200">The cloud, with how it looks.</response>
    /// <response code="404">The caller may not open it, or it is gone.</response>
    /// <returns>The cloud.</returns>
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<TagCloudSavedCloudDto> GetTagCloudCloud([FromRoute, Required] Guid id) => Ok(_clouds.Get(GetCurrentUser(), id));

    /// <summary>
    /// Saves a new cloud of the caller's.
    /// </summary>
    /// <param name="request">The cloud.</param>
    /// <param name="cancellationToken">Stops the save.</param>
    /// <response code="200">The cloud as saved.</response>
    /// <response code="400">A name that is empty or too long, or too many libraries, words or clouds.</response>
    /// <response code="403">Sharing is turned off by the server admin.</response>
    /// <response code="409">The caller already has a cloud of that name.</response>
    /// <returns>The saved cloud.</returns>
    [HttpPost]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TagCloudSavedCloudDto>> PostTagCloudCloud([FromBody, Required] TagCloudSaveRequest request, CancellationToken cancellationToken)
        => Ok(await _clouds.CreateAsync(GetCurrentUser(), request, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Saves a cloud of the caller's again: its name, sharing, what it counts and how it looks.
    /// </summary>
    /// <param name="id">The cloud's id.</param>
    /// <param name="request">The cloud; <c>IfUpdatedUtc</c> refuses to overwrite a newer save.</param>
    /// <param name="cancellationToken">Stops the save.</param>
    /// <response code="200">The cloud as saved.</response>
    /// <response code="400">A name that is empty or too long, or too many libraries or words.</response>
    /// <response code="403">Not the caller's cloud, or sharing is turned off.</response>
    /// <response code="404">It is gone.</response>
    /// <response code="409">It was saved again since <c>IfUpdatedUtc</c>, or the name is taken.</response>
    /// <returns>The saved cloud.</returns>
    [HttpPut("{id}")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TagCloudSavedCloudDto>> PutTagCloudCloud([FromRoute, Required] Guid id, [FromBody, Required] TagCloudSaveRequest request, CancellationToken cancellationToken)
        => Ok(await _clouds.UpdateAsync(GetCurrentUser(), id, request, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Copies a cloud the caller may open into a new private cloud of theirs, named "… (copy)".
    /// </summary>
    /// <param name="id">The cloud's id.</param>
    /// <param name="cancellationToken">Stops the save.</param>
    /// <response code="200">The copy, private and the caller's.</response>
    /// <response code="400">The caller has as many clouds as they may keep.</response>
    /// <response code="404">The caller may not open it, or it is gone.</response>
    /// <returns>The copy.</returns>
    [HttpPost("{id}/Duplicate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TagCloudSavedCloudDto>> PostTagCloudCloudDuplicate([FromRoute, Required] Guid id, CancellationToken cancellationToken)
        => Ok(await _clouds.DuplicateAsync(GetCurrentUser(), id, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Deletes a cloud: the caller's own, or as an administrator a shared one.
    /// </summary>
    /// <param name="id">The cloud's id.</param>
    /// <param name="cancellationToken">Stops the delete.</param>
    /// <response code="204">It is deleted.</response>
    /// <response code="403">Someone else's cloud.</response>
    /// <response code="404">The caller may not open it, or it is gone.</response>
    /// <returns>No content.</returns>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTagCloudCloud([FromRoute, Required] Guid id, CancellationToken cancellationToken)
    {
        var user = GetCurrentUser();
        await _clouds.DeleteAsync(user, id, await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    // API-key requests have no user, and clouds always belong to one: both get 403.
    private JellyfinUser GetCurrentUser() => User.RequireUser(_userManager, NoUserMessage);
}
