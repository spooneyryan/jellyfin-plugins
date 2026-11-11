using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Web;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.Bookmarks.Api;

/// <summary>
/// Per-user bookmarks and clip extraction, and since 1.1 the My Bookmarks page's lists and the users' naming preferences.
/// </summary>
/// <remarks>
/// A query-string value of an enum that names no member (a <c>sortBy</c> of the other list, say) is a 400: the kit's
/// <see cref="StrictEnumQueryAttribute"/>, since Jellyfin's own binder of nullable enums would ignore it.
/// </remarks>
[ApiController]
[Authorize]
[Route("Bookmarks")]
[Produces(MediaTypeNames.Application.Json)]
[KitAbiGate]
[PluginExceptionFilter]
[StrictEnumQuery]
public class BookmarksController : ControllerBase
{
    /// <summary>
    /// The 403 message for API keys (Bookmarks 1.0's wording, kept on the wire).
    /// </summary>
    internal const string NoUserMessage = "Bookmarks require a signed-in user; API keys can't be used.";

    /// <summary>
    /// The largest request body a POST of this controller reads (an abuse limit): 64 KB, far more than a
    /// bookmark with a 100-character name and a 500-character note needs. Kestrel answers a larger one with a 413, which
    /// the kit's filter sends as ProblemDetails.
    /// </summary>
    internal const int MaxBodyBytes = 64 * 1024;

    private readonly IBookmarkManager _bookmarkManager;
    private readonly IBookmarkQuery _query;
    private readonly IBookmarkPreferences _preferences;
    private readonly IUserManager _userManager;
    private readonly IEmbeddedScript _scripts;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarksController"/> class.
    /// </summary>
    /// <param name="bookmarkManager">The bookmark manager.</param>
    /// <param name="query">The lists of the My Bookmarks page.</param>
    /// <param name="preferences">The users' own naming preferences.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="scripts">The web client script, built by the kit.</param>
    public BookmarksController(
        IBookmarkManager bookmarkManager,
        IBookmarkQuery query,
        IBookmarkPreferences preferences,
        IUserManager userManager,
        IEmbeddedScript scripts)
    {
        _bookmarkManager = bookmarkManager;
        _query = query;
        _preferences = preferences;
        _userManager = userManager;
        _scripts = scripts;
    }

    /// <summary>
    /// Gets the current user's bookmarks.
    /// </summary>
    /// <param name="itemId">Optional. Only return bookmarks on this item, ordered by position.</param>
    /// <response code="200">The bookmarks.</response>
    /// <returns>The bookmarks; without an item filter, most recently changed first.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<BookmarkDto>> GetBookmarks([FromQuery] Guid? itemId)
        => Ok(_bookmarkManager.GetBookmarks(GetCurrentUser(), itemId));

    /// <summary>
    /// Lists the current user's bookmarks that match the filters, one row each, a page at a time: the flat view of the My
    /// Bookmarks page. Filters, order and totals work on the bookmarks as stored (availability too: whether the video left
    /// the library); ItemAvailable says, for each row on the page, whether the user can open its video now.
    /// </summary>
    /// <param name="filter">The filters, order and page (startIndex 0 and limit 50 by default, at most 200).</param>
    /// <param name="sortBy">The order: UpdatedAt (the default, newest first), CreatedAt, Name, ItemName or Position.</param>
    /// <response code="200">The page of bookmarks, with the number that match.</response>
    /// <response code="400">A value is out of range or not one of the endpoint's (a sort of GET /Bookmarks/Groups among
    /// them).</response>
    /// <returns>The page.</returns>
    [HttpGet("Query")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<QueryResult<BookmarkDto>> QueryBookmarks([FromQuery] BookmarkQueryFilter filter, [FromQuery] BookmarkSortBy? sortBy)
        => _query.Query(GetCurrentUser(), filter, sortBy);

    /// <summary>
    /// Lists the videos with the current user's bookmarks that match the filters, one group each with its bookmarks by
    /// position (at most 100 per group), a page of groups at a time: the grouped view of the My Bookmarks page. The video's
    /// fields come from what its bookmarks stored; ItemAvailable says whether the user can open it now.
    /// </summary>
    /// <param name="filter">The filters, order and page (startIndex 0 and limit 25 by default, at most 200).</param>
    /// <param name="sortBy">The order of the groups: LatestUpdated (the default, newest first), ItemName or Count.</param>
    /// <response code="200">The page of groups, with the number that match.</response>
    /// <response code="400">A value is out of range or not one of the endpoint's (a sort of GET /Bookmarks/Query among
    /// them).</response>
    /// <returns>The page.</returns>
    [HttpGet("Groups")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<QueryResult<BookmarkGroupDto>> GetBookmarkGroups([FromQuery] BookmarkQueryFilter filter, [FromQuery] BookmarkGroupSortBy? sortBy)
        => _query.Groups(GetCurrentUser(), filter, sortBy);

    /// <summary>
    /// Deletes several of the current user's bookmarks at once, each as DELETE /Bookmarks/{id} would. Without DeleteClips a
    /// bookmark with a clip file keeps it (the file can still be downloaded or deleted by the bookmark's id); with it the
    /// clip files go first, and a bookmark whose file is in use, or can't be deleted, is left as it was.
    /// </summary>
    /// <param name="request">The ids (1 to 200) and whether their clip files go too.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">What happened to each id: Deleted, Tombstoned (clip file kept), NotFound (no bookmark of the
    /// user), InUse or Failed (clip file; nothing changed).</response>
    /// <response code="400">No ids, or more than 200.</response>
    /// <response code="413">The request is over 64 KB.</response>
    /// <response code="503">The bookmark data is read-only; nothing changed.</response>
    /// <returns>What happened to each id.</returns>
    [HttpPost("BulkDelete")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<BookmarkBulkDeleteResultDto>> DeleteBookmarks(
        [FromBody, Required] BookmarkBulkDeleteRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _bookmarkManager.BulkDeleteAsync(GetCurrentUser(), request, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Gets the current user's own naming preferences: whether the player asks for a name at once (null: the server's
    /// default) and whether playback pauses while naming.
    /// </summary>
    /// <response code="200">The preferences.</response>
    /// <returns>The user's preferences.</returns>
    [HttpGet("Preferences")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<BookmarkPreferencesDto> GetBookmarkPreferences() => _preferences.Get(GetCurrentUser());

    /// <summary>
    /// Replaces the current user's own naming preferences. They follow the user to every device; the capabilities report
    /// the effective values.
    /// </summary>
    /// <param name="preferences">The preferences: AskForName (null for the server's default) and PauseWhileNaming.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">The preferences were saved.</response>
    /// <response code="400">The body isn't valid.</response>
    /// <response code="413">The request is over 64 KB.</response>
    /// <response code="503">The bookmark data is read-only; nothing was saved.</response>
    /// <returns>No content.</returns>
    [HttpPost("Preferences")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> UpdateBookmarkPreferences(
        [FromBody, Required] BookmarkPreferencesDto preferences,
        CancellationToken cancellationToken)
    {
        await _preferences.SetAsync(GetCurrentUser(), preferences, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Gets what the current user can do with bookmarks and clips.
    /// </summary>
    /// <response code="200">The capabilities.</response>
    /// <returns>Which bookmark features the user can use.</returns>
    [HttpGet("Capabilities")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<BookmarkCapabilitiesDto>> GetCapabilities()
    {
        var user = GetCurrentUser();

        // Jellyfin's RequiresElevation policy, never the stored flag.
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        return _bookmarkManager.GetCapabilities(user, isAdmin);
    }

    /// <summary>
    /// Gets one of the current user's bookmarks.
    /// </summary>
    /// <param name="bookmarkId">The bookmark id.</param>
    /// <response code="200">The bookmark.</response>
    /// <response code="404">The bookmark doesn't exist or belongs to someone else.</response>
    /// <returns>The bookmark with its clip status.</returns>
    [HttpGet("{bookmarkId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BookmarkDto> GetBookmark([FromRoute, Required] Guid bookmarkId)
        => _bookmarkManager.GetBookmark(GetCurrentUser(), bookmarkId);

    /// <summary>
    /// Creates a bookmark for the current user.
    /// </summary>
    /// <param name="request">The bookmark, optionally with a name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="201">The bookmark was created.</response>
    /// <response code="400">The times are invalid, or the name or note is too long.</response>
    /// <response code="404">The video doesn't exist or isn't visible to the user.</response>
    /// <response code="413">The request is over 64 KB.</response>
    /// <response code="503">The bookmark data is read-only; nothing was created.</response>
    /// <returns>The new bookmark.</returns>
    [HttpPost]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<BookmarkDto>> CreateBookmark(
        [FromBody, Required] BookmarkCreateRequest request,
        CancellationToken cancellationToken)
    {
        var bookmark = await _bookmarkManager.CreateBookmarkAsync(GetCurrentUser(), request, cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(GetBookmark), new { bookmarkId = bookmark.Id }, bookmark);
    }

    /// <summary>
    /// Replaces a bookmark's position, range end, name and note: a name or note left out is cleared, except that a body
    /// with neither Name nor IfUpdatedAt (Bookmarks 1.0's) keeps the name. With IfUpdatedAt (the bookmark's UpdatedAt as
    /// last read), a bookmark changed since is left as it is and the answer is 409.
    /// </summary>
    /// <param name="bookmarkId">The bookmark id.</param>
    /// <param name="request">The new values.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The bookmark was updated.</response>
    /// <response code="400">The times are invalid, or the name or note is too long.</response>
    /// <response code="404">The bookmark doesn't exist, belongs to someone else, or was deleted.</response>
    /// <response code="409">The bookmark was changed since IfUpdatedAt; nothing changed.</response>
    /// <response code="413">The request is over 64 KB.</response>
    /// <response code="503">The bookmark data is read-only; nothing changed.</response>
    /// <returns>The updated bookmark.</returns>
    [HttpPost("{bookmarkId:guid}")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<BookmarkDto>> UpdateBookmark(
        [FromRoute, Required] Guid bookmarkId,
        [FromBody, Required] BookmarkUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var bookmark = await _bookmarkManager.UpdateBookmarkAsync(GetCurrentUser(), bookmarkId, request, cancellationToken).ConfigureAwait(false);
        return bookmark;
    }

    /// <summary>
    /// Names, renames or unnames a bookmark; nothing else changes. Renaming to the same name also counts as a change (it
    /// confirms a bookmark that was moved to another copy of its video). With IfUpdatedAt (the bookmark's UpdatedAt as last
    /// read), a bookmark changed since is left as it is and the answer is 409. A clip file already made keeps its name.
    /// </summary>
    /// <param name="bookmarkId">The bookmark id.</param>
    /// <param name="request">The name, or none to leave the bookmark unnamed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The bookmark was renamed.</response>
    /// <response code="400">The name is over 100 characters.</response>
    /// <response code="404">The bookmark doesn't exist, belongs to someone else, or was deleted.</response>
    /// <response code="409">The bookmark was changed since IfUpdatedAt; nothing changed.</response>
    /// <response code="413">The request is over 64 KB.</response>
    /// <response code="503">The bookmark data is read-only; nothing changed.</response>
    /// <returns>The renamed bookmark.</returns>
    [HttpPost("{bookmarkId:guid}/Name")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<BookmarkDto>> RenameBookmark(
        [FromRoute, Required] Guid bookmarkId,
        [FromBody, Required] BookmarkRenameRequest request,
        CancellationToken cancellationToken)
    {
        var bookmark = await _bookmarkManager.RenameBookmarkAsync(GetCurrentUser(), bookmarkId, request, cancellationToken).ConfigureAwait(false);
        return bookmark;
    }

    /// <summary>
    /// Deletes a bookmark and cancels any clip job for it. Without deleteClip, a clip file is kept: the bookmark is gone
    /// from every list, and its clip file can still be downloaded or deleted by its id. With it, the clip file is deleted
    /// first; when it is in use (being played or downloaded), nothing changes and the answer is 409.
    /// </summary>
    /// <param name="bookmarkId">The bookmark id.</param>
    /// <param name="deleteClip">Whether to delete the bookmark's clip file as well.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">The bookmark was deleted.</response>
    /// <response code="404">The bookmark doesn't exist, belongs to someone else, or was deleted already; or, with deleteClip,
    /// its clip file isn't in the server's clip folders (nothing changed).</response>
    /// <response code="409">With deleteClip, the clip file is in use; nothing changed.</response>
    /// <response code="503">The bookmark data is read-only, or the clip file couldn't be deleted; nothing changed.</response>
    /// <returns>No content.</returns>
    [HttpDelete("{bookmarkId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> DeleteBookmark(
        [FromRoute, Required] Guid bookmarkId,
        [FromQuery] bool deleteClip = false,
        CancellationToken cancellationToken = default)
    {
        await _bookmarkManager.DeleteBookmarkAsync(GetCurrentUser(), bookmarkId, deleteClip, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Queues a clip of the bookmark's range. Poll the bookmark to follow its progress.
    /// </summary>
    /// <param name="bookmarkId">The bookmark id.</param>
    /// <param name="request">Optional. The extraction mode.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="202">The clip was queued (or was already queued or running).</response>
    /// <response code="400">The bookmark has no end time, is too long, or the video can't be clipped.</response>
    /// <response code="403">Clips are disabled, or the user may not download media.</response>
    /// <response code="404">The bookmark doesn't exist or belongs to someone else.</response>
    /// <response code="503">Not enough free space would remain on the clip folder's drive, the clip folder's drive isn't
    /// available, or the bookmark data is read-only; nothing was queued. An admin's message names the drive and the
    /// numbers.</response>
    /// <returns>The bookmark with its clip status.</returns>
    [HttpPost("{bookmarkId:guid}/Clip")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<BookmarkDto>> StartClip(
        [FromRoute, Required] Guid bookmarkId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] BookmarkClipRequest? request,
        CancellationToken cancellationToken)
    {
        var user = GetCurrentUser();

        // Jellyfin's RequiresElevation policy, never the stored flag: the wording of a free-space refusal.
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        var bookmark = await _bookmarkManager.StartClipAsync(user, bookmarkId, request?.Mode, isAdmin, cancellationToken).ConfigureAwait(false);
        return Accepted(bookmark);
    }

    /// <summary>
    /// Cancels the bookmark's clip job if one is queued or running. An existing clip file is kept.
    /// </summary>
    /// <param name="bookmarkId">The bookmark id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The job was cancelled, or there was none.</response>
    /// <response code="404">The bookmark doesn't exist or belongs to someone else.</response>
    /// <response code="503">The bookmark data is read-only; nothing changed.</response>
    /// <returns>The bookmark.</returns>
    [HttpPost("{bookmarkId:guid}/Clip/Cancel")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<BookmarkDto>> CancelClip(
        [FromRoute, Required] Guid bookmarkId,
        CancellationToken cancellationToken)
    {
        var bookmark = await _bookmarkManager.CancelClipAsync(GetCurrentUser(), bookmarkId, cancellationToken).ConfigureAwait(false);
        return bookmark;
    }

    /// <summary>
    /// Deletes the bookmark's clip file, then cancels its clip job, if one is queued or running. The clip file of a
    /// bookmark deleted with its clip kept goes with what is left of the bookmark. When the file is in use (being played or
    /// downloaded), nothing changes and the answer is 409. With onlyIfMissing=true (forgetting a clip whose file is gone, or
    /// a failed clip without one) no file is ever deleted: a clip file that is there is a 409, and nothing changes.
    /// </summary>
    /// <param name="bookmarkId">The bookmark id.</param>
    /// <param name="onlyIfMissing">Whether only a clip without its file may go: its file gone from its clip folder, or none.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The clip was cancelled or deleted; the bookmark stays.</response>
    /// <response code="204">The kept clip of a deleted bookmark was deleted, and nothing of the bookmark is left.</response>
    /// <response code="404">The bookmark doesn't exist or belongs to someone else, or its clip file isn't in the server's
    /// clip folders (nothing changed).</response>
    /// <response code="409">The clip file is in use, or, with onlyIfMissing, there; nothing changed.</response>
    /// <response code="503">The bookmark data is read-only, or the clip file couldn't be deleted; nothing changed.</response>
    /// <returns>The updated bookmark, or no content.</returns>
    [HttpDelete("{bookmarkId:guid}/Clip")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<BookmarkDto>> DeleteClip(
        [FromRoute, Required] Guid bookmarkId,
        [FromQuery] bool? onlyIfMissing,
        CancellationToken cancellationToken)
    {
        var bookmark = onlyIfMissing == true
            ? await _bookmarkManager.ForgetClipAsync(GetCurrentUser(), bookmarkId, cancellationToken).ConfigureAwait(false)
            : await _bookmarkManager.DeleteClipAsync(GetCurrentUser(), bookmarkId, cancellationToken).ConfigureAwait(false);
        return bookmark is null ? NoContent() : bookmark;
    }

    /// <summary>
    /// Downloads or streams a bookmark's clip file, also the clip file kept when the bookmark was deleted; only from the
    /// server's clip folders.
    /// </summary>
    /// <param name="bookmarkId">The bookmark id.</param>
    /// <param name="download">Whether to send the file as an attachment (the default) or inline for playback.</param>
    /// <response code="200">The clip file.</response>
    /// <response code="404">The bookmark doesn't exist, belongs to someone else, or has no clip file in the server's clip
    /// folders.</response>
    /// <returns>A file result supporting range requests.</returns>
    [HttpGet("{bookmarkId:guid}/Clip/File")]
    [Authorize(Policy = Policies.Download)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetClipFile([FromRoute, Required] Guid bookmarkId, [FromQuery] bool download = true)
    {
        var path = _bookmarkManager.GetClipFilePath(GetCurrentUser(), bookmarkId);
        var contentType = MimeTypes.GetMimeType(path);
        return download
            ? PhysicalFile(path, contentType, Path.GetFileName(path), enableRangeProcessing: true)
            : PhysicalFile(path, contentType, enableRangeProcessing: true);
    }

    /// <summary>
    /// Gets the web client script that adds the bookmark UI: the kit's kit.js and the plugin's Web/js files in one script.
    /// Loaded by the script tag the kit's middleware adds to index.html, so it can't require
    /// authentication; it holds no user data. Served with <c>Cache-Control: no-cache</c> and an ETag that changes with
    /// each build, so updates show up at once.
    /// </summary>
    /// <response code="200">The script.</response>
    /// <response code="304">The browser's copy is current.</response>
    /// <returns>The JavaScript file.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public IActionResult GetClientScript() => _scripts.ClientScript();

    // API-key requests have no user, and bookmarks always belong to one: both get 403.
    private JellyfinUser GetCurrentUser() => User.RequireUser(_userManager, NoUserMessage);
}
