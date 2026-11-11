using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Every user's clips, for admins: the My Clips page's "All users" scope with its owner filter, every user's clips in
/// numbers, the state of the clips being made, and a clip's file, its job's cancel, its delete and a delete of several.
/// The only place that reaches other users' clips (the admin bypass lives only in <c>AdminClipService</c>): its
/// controller requires Jellyfin's <c>RequiresElevation</c> policy on every request, and every member but
/// <see cref="GetSummary"/> answers 403 while the setting <c>AdminsCanManageAllClips</c> is off. A clip's note never
/// reaches its answers, nor takes part in a search or an order.
/// </summary>
/// <remarks>
/// <para>Public because the public controller takes it: DTOs, Jellyfin's public types and BCL types only; its
/// implementation maps the records to DTOs itself.</para>
/// <para>The caller is the request's principal (Jellyfin's claims): an admin, or an API key (which
/// <c>RequiresElevation</c> admits). The log line of each action names who acted (the admin's id, the API key's
/// name, never its token, or a user that no longer exists by its id), and the library is asked as the admin (an API
/// key, or a user that no longer exists, opens no video). Every file action goes through the clip file operations as
/// the owner's own does: the file first, a rename that isn't finished finished first, nothing touched in a clip folder
/// that is offline or not recognised, nor for a clip being made, nor a file another record still names.</para>
/// </remarks>
public interface IBookmarkAdminClips
{
    /// <summary>
    /// Lists every user's clips, or one user's, that match the filters, a page at a time (<c>GET /Bookmarks/Admin/Clips</c>),
    /// the clips being made first whatever the order; each with its owner and its file's full path, never its note (the search
    /// doesn't read notes either).
    /// </summary>
    /// <param name="caller">The request's principal: an admin, or an API key.</param>
    /// <param name="filter">The filters and page.</param>
    /// <param name="userId">Only this user's clips (the owner filter); every user's when <c>null</c>.</param>
    /// <param name="sortBy">The order; <see cref="BookmarkClipSortBy.Newest"/> by default.</param>
    /// <returns>The page, with the number of matching clips.</returns>
    /// <exception cref="Kit.Api.PluginException">403 while <c>AdminsCanManageAllClips</c> is off; 400 as for the user's
    /// list.</exception>
    QueryResult<BookmarkAdminClipItemDto> GetClips(ClaimsPrincipal caller, BookmarkClipQueryFilter? filter, Guid? userId, BookmarkClipSortBy? sortBy);

    /// <summary>
    /// Counts every user's clips (<c>GET /Bookmarks/Admin/Clips/Summary</c>): per user with at least one, and in all. Aggregates
    /// only, so not gated by <c>AdminsCanManageAllClips</c>.
    /// </summary>
    /// <returns>The numbers.</returns>
    BookmarkAdminClipSummaryDto GetSummary();

    /// <summary>
    /// Gets the state of some clips of any user (<c>GET /Bookmarks/Admin/Clips/Status</c>), for the page's poll of the clips
    /// being made in the "All users" scope; ids that are no clip are left out.
    /// </summary>
    /// <param name="caller">The request's principal: an admin sees the states of the clips they may see (since 1.2.1).</param>
    /// <param name="ids">The clips' ids: 1 to 200.</param>
    /// <returns>Each clip's state, in the order asked.</returns>
    /// <exception cref="Kit.Api.PluginException">403 while <c>AdminsCanManageAllClips</c> is off; 400 for no ids or more than
    /// 200.</exception>
    IReadOnlyList<BookmarkClipStatusDto> GetStatus(ClaimsPrincipal caller, IReadOnlyList<Guid>? ids);

    /// <summary>
    /// Gets the full path of a clip file of any user to serve (<c>GET /Bookmarks/Admin/Clips/{id}/File</c>), as the owner's own
    /// file route finds it: only a file in the clip folders that is there (served also while a rename moves it).
    /// </summary>
    /// <param name="caller">The request's principal: an admin, or an API key.</param>
    /// <param name="clipId">The clip's id (its bookmark's).</param>
    /// <param name="download">Whether it is sent as an attachment (a download) rather than inline (a preview), for the log.</param>
    /// <param name="logAccess">Whether this request may be logged at Information (the first request of a download, or a
    /// preview's request from byte 0, which a looping video repeats: logged once per caller and clip in
    /// <c>AdminClipService.PreviewLogWindow</c>; a video's later range requests are logged at Debug).</param>
    /// <returns>The file's full path.</returns>
    /// <exception cref="Kit.Api.PluginException">403 while <c>AdminsCanManageAllClips</c> is off; 404 no clip, no file, its
    /// file gone, or outside the clip folders.</exception>
    string GetClipFilePath(ClaimsPrincipal caller, Guid clipId, bool download, bool logAccess);

    /// <summary>
    /// Cancels the job of a clip of any user (<c>POST /Bookmarks/Admin/Clips/{id}/Cancel</c>): a waiting job ends at once, a
    /// running one when it stops; any earlier file stays. A clip that isn't being made is answered as it is.
    /// </summary>
    /// <param name="caller">The request's principal: an admin, or an API key.</param>
    /// <param name="clipId">The clip's id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The clip as it is now.</returns>
    /// <exception cref="Kit.Api.PluginException">403 while <c>AdminsCanManageAllClips</c> is off; 404 no clip; 503 read-only
    /// data.</exception>
    Task<BookmarkAdminClipItemDto> CancelAsync(ClaimsPrincipal caller, Guid clipId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a clip of any user (<c>DELETE /Bookmarks/Admin/Clips/{id}</c>) as its owner's delete does: the file first, three
    /// attempts 200 ms apart for a file in use; then a live bookmark keeps no clip and a deleted bookmark's record goes. With
    /// <paramref name="onlyIfMissing"/> (the page's Forget and Dismiss) no file is deleted: a file that is there after all is 409.
    /// </summary>
    /// <param name="caller">The request's principal: an admin, or an API key.</param>
    /// <param name="clipId">The clip's id.</param>
    /// <param name="onlyIfMissing">Whether only a clip without its file may go.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the clip is gone.</returns>
    /// <exception cref="Kit.Api.PluginException">403 while <c>AdminsCanManageAllClips</c> is off; 404 no clip; 409 its file in
    /// use, being made, outside the clip folders, or there after all; 503 read-only data, its clip folder offline or not
    /// recognised, its last rename not finished, or another refusal. Nothing changed in any case.</exception>
    Task DeleteAsync(ClaimsPrincipal caller, Guid clipId, bool onlyIfMissing, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes several clips of any users (<c>POST /Bookmarks/Admin/Clips/BulkDelete</c>), each as <see cref="DeleteAsync"/>
    /// would, the ones that can't go left exactly as they were and reported with why.
    /// </summary>
    /// <param name="caller">The request's principal: an admin, or an API key.</param>
    /// <param name="request">The ids (1 to 200).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Each distinct id in one of the result's lists.</returns>
    /// <exception cref="Kit.Api.PluginException">403 while <c>AdminsCanManageAllClips</c> is off; 400 for no ids or more than
    /// 200; 503 read-only data, before anything is looked up.</exception>
    Task<BookmarkClipBulkDeleteResultDto> BulkDeleteAsync(ClaimsPrincipal caller, BookmarkClipBulkDeleteRequest request, CancellationToken cancellationToken);
}
