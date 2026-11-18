using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Kit.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.Bookmarks.Api;

/// <summary>
/// A playlist exported to the clip folder (since 1.5.0 / 2.3.0): its videos joined end to end, in playlist order, into one
/// MP4 made with the clip settings. The same permission as clips (clips enabled, the user allowed to download media); only
/// playlists the caller may open, and only their videos the caller may see. The caller's own exports only; another user's
/// export id is the same as one that doesn't exist.
/// </summary>
/// <remarks>
/// Named <c>BookmarksPlaylistExportController</c> (controller names start with the route prefix); its routes sit under
/// <c>/Bookmarks</c> beside the bookmark routes. An export is no bookmark clip: no record, never in the clip lists.
/// </remarks>
[ApiController]
[Authorize]
[Route("Bookmarks")]
[Produces(MediaTypeNames.Application.Json)]
[KitAbiGate]
[PluginExceptionFilter]
[StrictEnumQuery]
public class BookmarksPlaylistExportController : ControllerBase
{
    private readonly IBookmarkPlaylistExport _exports;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarksPlaylistExportController"/> class.
    /// </summary>
    /// <param name="exports">The playlist exports.</param>
    /// <param name="userManager">The user manager.</param>
    public BookmarksPlaylistExportController(IBookmarkPlaylistExport exports, IUserManager userManager)
    {
        _exports = exports;
        _userManager = userManager;
    }

    /// <summary>
    /// Works out what exporting a playlist to the clip folder would make (since 1.5.0 / 2.3.0): its videos joined end to
    /// end, in playlist order, into one MP4 with the clip settings; the entries left out and why, the length, picture size
    /// and frame rate, the estimated size and time, and the file name.
    /// </summary>
    /// <param name="playlistId">The playlist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The estimate (<c>CanExport</c> false with a <c>Problem</c> when it can't start now).</response>
    /// <response code="400">The clip settings can't make one (no ffmpeg, the clip folder setting isn't a folder).</response>
    /// <response code="403">Clips are disabled, or the user may not download media.</response>
    /// <response code="404">No playlist the caller may open.</response>
    /// <response code="503">The clip folder can't be reached.</response>
    /// <returns>The estimate.</returns>
    [HttpGet("Playlists/{playlistId:guid}/Export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<BookmarkPlaylistExportEstimateDto>> GetPlaylistExportEstimate(
        [FromRoute, Required] Guid playlistId,
        CancellationToken cancellationToken)
    {
        var user = GetCurrentUser();
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        return await _exports.EstimateAsync(user, playlistId, isAdmin, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts exporting a playlist to the clip folder (since 1.5.0 / 2.3.0). The same permission as clips; the export is
    /// made in the background, one at a time, and followed with <c>GET /Bookmarks/Exports/{exportId}</c>.
    /// </summary>
    /// <param name="playlistId">The playlist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="202">The export was queued.</response>
    /// <response code="400">No video to export, too many, or the clip settings can't make one.</response>
    /// <response code="403">Clips are disabled, or the user may not download media.</response>
    /// <response code="404">No playlist the caller may open.</response>
    /// <response code="409">The playlist is being exported for the caller already, or three exports of theirs wait.</response>
    /// <response code="503">Not enough free space, the clip folder away, or the bookmark data read-only; nothing was queued.</response>
    /// <returns>The export.</returns>
    [HttpPost("Playlists/{playlistId:guid}/Export")]
    [RequestSizeLimit(BookmarksController.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<BookmarkPlaylistExportJobDto>> StartPlaylistExport(
        [FromRoute, Required] Guid playlistId,
        CancellationToken cancellationToken)
    {
        var user = GetCurrentUser();
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        return Accepted(await _exports.StartAsync(user, playlistId, isAdmin, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Lists the caller's playlist exports since the server started, newest first (since 1.5.0 / 2.3.0).
    /// </summary>
    /// <response code="200">The list.</response>
    /// <returns>The _exports.</returns>
    [HttpGet("Exports")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<BookmarkPlaylistExportJobDto>> GetPlaylistExports()
    {
        return Ok(_exports.GetExports(GetCurrentUser()));
    }

    /// <summary>
    /// Gets one of the caller's playlist exports (since 1.5.0 / 2.3.0).
    /// </summary>
    /// <param name="exportId">The export's id.</param>
    /// <response code="200">Found.</response>
    /// <response code="404">No export of the caller's.</response>
    /// <returns>The export.</returns>
    [HttpGet("Exports/{exportId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BookmarkPlaylistExportJobDto> GetPlaylistExport([FromRoute, Required] Guid exportId)
    {
        return _exports.GetExport(GetCurrentUser(), exportId);
    }

    /// <summary>
    /// Cancels one of the caller's playlist exports that waits or is being made; its unfinished file is deleted (since
    /// 1.5.0 / 2.3.0).
    /// </summary>
    /// <param name="exportId">The export's id.</param>
    /// <response code="200">The export as it is now (one being made reports Cancelled a moment later).</response>
    /// <response code="404">No export of the caller's.</response>
    /// <returns>The export.</returns>
    [HttpPost("Exports/{exportId:guid}/Cancel")]
    [RequestSizeLimit(BookmarksController.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BookmarkPlaylistExportJobDto> CancelPlaylistExport([FromRoute, Required] Guid exportId)
    {
        return _exports.Cancel(GetCurrentUser(), exportId);
    }

    private JellyfinUser GetCurrentUser() => User.RequireUser(_userManager, BookmarksController.NoUserMessage);
}
