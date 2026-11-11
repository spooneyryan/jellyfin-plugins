using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Kit.Api;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Bookmarks.Api;

/// <summary>
/// Every user's clips, for admins (since 1.2.0): the My Clips page's "All users" scope with its owner filter, the
/// per-user totals, the state of the clips being made, and a clip's file, cancel, delete and a delete of several. Jellyfin's
/// <c>RequiresElevation</c> policy on every request (a demoted admin's token is refused at its next request, a disabled user's
/// isn't authenticated; an API key passes); every route but <c>Summary</c> also needs the setting
/// <c>AdminsCanManageAllClips</c> (403 otherwise: admins then see only their own clips), and the file route Jellyfin's
/// download permission. A clip's note is never in an answer.
/// </summary>
/// <remarks>
/// Named <c>BookmarksAdminClipsController</c> (controller names start with the route prefix); a controller of its own,
/// not the admin controller, keeps the clip routes' parameter rules (<see cref="StrictEnumQueryAttribute"/>) apart from
/// the settings page's.
/// </remarks>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Bookmarks/Admin/Clips")]
[Produces(MediaTypeNames.Application.Json)]
[KitAbiGate]
[PluginExceptionFilter]
[StrictEnumQuery]
public class BookmarksAdminClipsController : ControllerBase
{
    private readonly IBookmarkAdminClips _clips;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarksAdminClipsController"/> class.
    /// </summary>
    /// <param name="clips">Every user's clips; it reads who called from the request's claims (an admin, or an API key by its
    /// name), for the library and the audit.</param>
    public BookmarksAdminClipsController(IBookmarkAdminClips clips)
    {
        _clips = clips;
    }

    /// <summary>
    /// Lists every user's clips (or one user's) that match the filters, a page at a time, the clips being made first: each with
    /// its owner (UserName, or "Deleted user (id)"), its state and its file's full path (FilePath, RootId), never its bookmark's
    /// note (the search doesn't read notes either). ItemAvailable says whether the admin can open the video now.
    /// </summary>
    /// <param name="filter">The filters and page, as GET /Bookmarks/Clips takes them (startIndex 0 and limit 50 by default, at
    /// most 200).</param>
    /// <param name="userId">Only this user's clips; every user's when left out.</param>
    /// <param name="sortBy">The order: Newest (the default), Oldest, Name, ItemName, Size or Duration.</param>
    /// <response code="200">The page of clips, with the number that match.</response>
    /// <response code="400">A value is out of range or not one of the endpoint's.</response>
    /// <response code="403">Not an admin, or admins' access to other users' clips is turned off.</response>
    /// <returns>The page.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<QueryResult<BookmarkAdminClipItemDto>> GetAdminClips([FromQuery] BookmarkClipQueryFilter filter, [FromQuery] Guid? userId, [FromQuery] BookmarkClipSortBy? sortBy)
        => _clips.GetClips(User, filter, userId, sortBy);

    /// <summary>
    /// Counts every user's clips: per user with at least one clip (UserName, or "Deleted user (id)"; the count, the bytes their
    /// files take, the oldest and the newest) and in all. Aggregates only, also while admins' access to other users' clips is
    /// turned off.
    /// </summary>
    /// <response code="200">The numbers.</response>
    /// <response code="403">Not an admin.</response>
    /// <returns>Every user's clips in numbers.</returns>
    [HttpGet("Summary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<BookmarkAdminClipSummaryDto> GetAdminClipSummary() => _clips.GetSummary();

    /// <summary>
    /// Gets the state of some clips of any user, for following the clips being made with one request. Ids that are no clip are
    /// left out.
    /// </summary>
    /// <param name="ids">The clips' ids, comma-separated or repeated: 1 to 200.</param>
    /// <response code="200">Each clip's state, in the order asked.</response>
    /// <response code="400">No ids, more than 200, or a value that isn't a GUID.</response>
    /// <response code="403">Not an admin, or admins' access to other users' clips is turned off.</response>
    /// <returns>The clips' states.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IReadOnlyList<BookmarkClipStatusDto>> GetAdminClipStatus([FromQuery, CommaSeparatedQuery] IReadOnlyList<Guid>? ids)
        => Ok(_clips.GetStatus(User, ids));

    /// <summary>
    /// Gets a clip file of any user, with range support, as the owner's own file route serves it: an attachment named as the
    /// file is (the default), or inline for a preview. Only a file in the server's clip folders that is there.
    /// </summary>
    /// <param name="bookmarkId">The clip's id (its bookmark's).</param>
    /// <param name="download">Whether to send the file as an attachment (the default) or inline for playback.</param>
    /// <response code="200">The file, its name the clip file's own (no folder).</response>
    /// <response code="403">Not an admin, no permission to download media, or admins' access to other users' clips is turned off.</response>
    /// <response code="404">No clip, no file, its file gone, or not in the server's clip folders.</response>
    /// <returns>The clip file.</returns>
    [HttpGet("{bookmarkId:guid}/File")]
    [Authorize(Policy = Policies.Download)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetAdminClipFile([FromRoute, Required] Guid bookmarkId, [FromQuery] bool download = true)
    {
        // A preview's <video> asks again for later ranges; only the first request of a preview or a download may be logged at
        // Information.
        var range = Request.Headers.Range.ToString();
        var first = range.Length == 0 || range.StartsWith("bytes=0-", StringComparison.OrdinalIgnoreCase);
        var path = _clips.GetClipFilePath(User, bookmarkId, download, first);
        var contentType = MimeTypes.GetMimeType(path);
        return download
            ? PhysicalFile(path, contentType, Path.GetFileName(path), enableRangeProcessing: true)
            : PhysicalFile(path, contentType, enableRangeProcessing: true);
    }

    /// <summary>
    /// Cancels the clip being made of any user: a waiting one ends at once, a running one when its job stops; any earlier file
    /// stays. A clip that isn't being made is answered as it is.
    /// </summary>
    /// <param name="bookmarkId">The clip's id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The clip as it is now.</response>
    /// <response code="403">Not an admin, or admins' access to other users' clips is turned off.</response>
    /// <response code="404">No clip.</response>
    /// <response code="503">The bookmark data is read-only.</response>
    /// <returns>The clip.</returns>
    [HttpPost("{bookmarkId:guid}/Cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<BookmarkAdminClipItemDto>> CancelAdminClip([FromRoute, Required] Guid bookmarkId, CancellationToken cancellationToken)
    {
        var clip = await _clips.CancelAsync(User, bookmarkId, cancellationToken).ConfigureAwait(false);
        return clip;
    }

    /// <summary>
    /// Deletes a clip of any user as its owner's delete does: the file first; then a live bookmark stays without its clip, and
    /// what was left of a deleted bookmark goes. With onlyIfMissing=true (forgetting a clip whose file is gone, or clearing a
    /// failed one) no file is deleted: a file that is there after all is 409.
    /// </summary>
    /// <param name="bookmarkId">The clip's id.</param>
    /// <param name="onlyIfMissing">Whether only a clip without its file may go.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">The clip is gone.</response>
    /// <response code="403">Not an admin, or admins' access to other users' clips is turned off.</response>
    /// <response code="404">No clip.</response>
    /// <response code="409">Its file is in use, it is being made (cancel it first), its file is outside the clip folders, or
    /// (onlyIfMissing) its file is there; nothing changed.</response>
    /// <response code="503">The bookmark data is read-only, its clip folder is offline or not recognised, its last rename
    /// isn't finished, or its file couldn't be deleted; nothing changed.</response>
    /// <returns>No content.</returns>
    [HttpDelete("{bookmarkId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> DeleteAdminClip([FromRoute, Required] Guid bookmarkId, [FromQuery] bool? onlyIfMissing, CancellationToken cancellationToken)
    {
        await _clips.DeleteAsync(User, bookmarkId, onlyIfMissing == true, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Deletes several clips of any users, each as DELETE /Bookmarks/Admin/Clips/{id} would. A clip being made, or whose file is
    /// in use, whose folder is offline or whose file can't be deleted otherwise, is left as it was.
    /// </summary>
    /// <param name="request">The ids (1 to 200).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">What happened to each id: FilesDeleted (the bookmark stays), RecordsRemoved (a deleted bookmark's
    /// clip), NotFound (no clip), InProgress, InUse, FolderUnavailable or Failed (nothing changed).</response>
    /// <response code="400">No ids, or more than 200.</response>
    /// <response code="403">Not an admin, or admins' access to other users' clips is turned off.</response>
    /// <response code="413">The request is over 64 KB.</response>
    /// <response code="503">The bookmark data is read-only; nothing changed.</response>
    /// <returns>What happened to each id.</returns>
    [HttpPost("BulkDelete")]
    [RequestSizeLimit(BookmarksController.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<BookmarkClipBulkDeleteResultDto>> DeleteAdminClips(
        [FromBody, Required] BookmarkClipBulkDeleteRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _clips.BulkDeleteAsync(User, request, cancellationToken).ConfigureAwait(false);
        return result;
    }
}
