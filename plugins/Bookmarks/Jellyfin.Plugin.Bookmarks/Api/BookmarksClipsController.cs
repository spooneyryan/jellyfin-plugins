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
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.Bookmarks.Api;

/// <summary>
/// The signed-in user's clips, for the My Clips page: the list with its filters, the numbers, the state of the clips being
/// made, a delete of several, and a clip's rename. Only the caller's own clips; another user's id is the same as one that
/// doesn't exist. A clip's file, its delete and its job keep their bookmark routes (<c>/Bookmarks/{id}/Clip…</c>), which
/// reach the clips of deleted bookmarks too.
/// </summary>
/// <remarks>
/// Named <c>BookmarksClipsController</c> (controller names start with the route prefix). A query-string value of an enum
/// that names no member, or an id that isn't a GUID, is a 400 (the kit's <see cref="StrictEnumQueryAttribute"/> and
/// <see cref="CommaSeparatedQueryAttribute"/>).
/// </remarks>
[ApiController]
[Authorize]
[Route("Bookmarks/Clips")]
[Produces(MediaTypeNames.Application.Json)]
[KitAbiGate]
[PluginExceptionFilter]
[StrictEnumQuery]
public class BookmarksClipsController : ControllerBase
{
    private readonly IBookmarkClips _clips;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarksClipsController"/> class.
    /// </summary>
    /// <param name="clips">The user's clips.</param>
    /// <param name="userManager">The user manager.</param>
    public BookmarksClipsController(IBookmarkClips clips, IUserManager userManager)
    {
        _clips = clips;
        _userManager = userManager;
    }

    /// <summary>
    /// Lists the current user's clips that match the filters, a page at a time: every clip of theirs across all videos,
    /// those kept when a bookmark was deleted included (unless includeDeleted is false), each with what it is right now
    /// (State: Ready, InProgress, Failed, Missing or FolderUnavailable). The clips being made come first, whatever the order.
    /// ItemAvailable says, for each clip on the page, whether the user can open its video now.
    /// </summary>
    /// <param name="filter">The filters and page (startIndex 0 and limit 50 by default, at most 200).</param>
    /// <param name="sortBy">The order: Newest (the default), Oldest, Name, ItemName, Size (largest first) or Duration (longest
    /// first).</param>
    /// <response code="200">The page of clips, with the number that match.</response>
    /// <response code="400">A value is out of range or not one of the endpoint's.</response>
    /// <returns>The page.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<QueryResult<BookmarkClipItemDto>> GetClips([FromQuery] BookmarkClipQueryFilter filter, [FromQuery] BookmarkClipSortBy? sortBy)
        => _clips.GetClips(GetCurrentUser(), filter, sortBy);

    /// <summary>
    /// Counts the current user's clips: how many, the bytes their files take, and how many are being made, failed, miss their
    /// file or are in a clip folder that is offline.
    /// </summary>
    /// <response code="200">The numbers.</response>
    /// <returns>The user's clips in numbers.</returns>
    [HttpGet("Summary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<BookmarkClipSummaryDto> GetClipSummary() => _clips.GetSummary(GetCurrentUser());

    /// <summary>
    /// Gets the state of some of the current user's clips, for following the clips being made with one request. Ids that are
    /// no clip of the user are left out.
    /// </summary>
    /// <param name="ids">The clips' ids (their bookmarks' ids), comma-separated or repeated: 1 to 200.</param>
    /// <response code="200">Each clip's state, in the order asked.</response>
    /// <response code="400">No ids, more than 200, or a value that isn't a GUID.</response>
    /// <returns>The clips' states.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<IReadOnlyList<BookmarkClipStatusDto>> GetClipStatus([FromQuery, CommaSeparatedQuery] IReadOnlyList<Guid>? ids)
        => Ok(_clips.GetStatus(GetCurrentUser(), ids));

    /// <summary>
    /// Deletes several of the current user's clips, each finished one as DELETE /Bookmarks/{id}/Clip would: the file first;
    /// then a live bookmark stays without its clip, and what was left of a deleted bookmark goes. A clip being made (cancel
    /// it, or wait for it, first), or whose file is in use, whose folder is offline or whose file can't be deleted otherwise,
    /// is left as it was.
    /// </summary>
    /// <param name="request">The ids (1 to 200).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">What happened to each id: FilesDeleted (the bookmark stays), RecordsRemoved (a deleted bookmark's
    /// clip), NotFound (no clip of the user), InProgress, InUse, FolderUnavailable or Failed (nothing changed).</response>
    /// <response code="400">No ids, or more than 200.</response>
    /// <response code="413">The request is over 64 KB.</response>
    /// <response code="503">The bookmark data is read-only; nothing changed.</response>
    /// <returns>What happened to each id.</returns>
    [HttpPost("BulkDelete")]
    [RequestSizeLimit(BookmarksController.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<BookmarkClipBulkDeleteResultDto>> DeleteClips(
        [FromBody, Required] BookmarkClipBulkDeleteRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _clips.BulkDeleteAsync(GetCurrentUser(), request, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Renames one of the current user's clips: the name of its bookmark (also of a deleted bookmark's kept clip), and with
    /// RenameFile its clip file, which then gets the name Bookmarks gives the clip now (the video's name, the new name, else the
    /// note, and the file's range), in the same folder, numbered when the name is taken. Either both are renamed or nothing is:
    /// a file in use (being played or downloaded) leaves the name as it was too. A clip in a library is a new video to
    /// Jellyfin once its file is renamed.
    /// </summary>
    /// <param name="bookmarkId">The clip's id (its bookmark's).</param>
    /// <param name="request">The name (none to leave it unnamed), whether the file follows, and the clip's UpdatedAt as last
    /// read.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The clip as it is now.</response>
    /// <response code="400">The name is over 100 characters.</response>
    /// <response code="404">No clip of the user's, or (with the file) no file, its file gone, or not in the server's clip
    /// folders; nothing changed.</response>
    /// <response code="409">The clip is being made, was changed since IfUpdatedAt, or its file is in use; nothing changed.</response>
    /// <response code="413">The request is over 64 KB.</response>
    /// <response code="503">The bookmark data is read-only, the clip folder is offline or not recognised, or the file or the
    /// data couldn't be written; nothing changed.</response>
    /// <returns>The renamed clip.</returns>
    [HttpPost("{bookmarkId:guid}/Name")]
    [RequestSizeLimit(BookmarksController.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<BookmarkClipItemDto>> RenameClip(
        [FromRoute, Required] Guid bookmarkId,
        [FromBody, Required] BookmarkClipRenameRequest request,
        CancellationToken cancellationToken)
    {
        var clip = await _clips.RenameAsync(GetCurrentUser(), bookmarkId, request, cancellationToken).ConfigureAwait(false);
        return clip;
    }

    /// <summary>
    /// Renames one of the current user's clip files to the name Bookmarks gives the clip now (after its bookmark was renamed:
    /// "Name changed"), in the same folder, numbered when that name is taken; the clip's own name doesn't change. A file in use
    /// is left as it is. With IfUpdatedAt (optional), a clip renamed since is refused, rather than given a file name other than
    /// the one the client showed.
    /// </summary>
    /// <param name="bookmarkId">The clip's id (its bookmark's).</param>
    /// <param name="request">The clip's UpdatedAt as last read; the body may be left out.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The clip as it is now (also when its file has that name already).</response>
    /// <response code="400">The body isn't one this route reads.</response>
    /// <response code="404">No clip of the user's, no file, its file gone, or not in the server's clip folders.</response>
    /// <response code="409">The clip is being made, was changed since IfUpdatedAt, or its file is in use; nothing
    /// changed.</response>
    /// <response code="413">The request is over 64 KB.</response>
    /// <response code="503">The bookmark data is read-only, the clip folder is offline or not recognised, the clip's last
    /// rename isn't finished, or the file or the data couldn't be written; nothing changed.</response>
    /// <returns>The clip.</returns>
    [HttpPost("{bookmarkId:guid}/RenameFile")]
    [RequestSizeLimit(BookmarksController.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<BookmarkClipItemDto>> RenameClipFile(
        [FromRoute, Required] Guid bookmarkId,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] BookmarkClipRenameFileRequest? request,
        CancellationToken cancellationToken)
    {
        var clip = await _clips.RenameFileAsync(GetCurrentUser(), bookmarkId, request?.IfUpdatedAt, cancellationToken).ConfigureAwait(false);
        return clip;
    }

    // API-key requests have no user, and clips always belong to one: both get 403, with Bookmarks' own wording.
    private JellyfinUser GetCurrentUser() => User.RequireUser(_userManager, BookmarksController.NoUserMessage);
}
