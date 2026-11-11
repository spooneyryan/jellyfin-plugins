using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The caller's clips, for the My Clips page: every clip of theirs across all videos, live bookmarks' and those kept when a
/// bookmark was deleted, filtered, ordered and paged in memory, with what each clip is right now (ready, being made,
/// failed, its file missing, its folder offline); their numbers; the state of the clips being made; a delete of several
/// clips, file first; and a clip's rename, its name, its file or both. Only the caller's own clips, ever (the admins' view
/// of every user's clips is <see cref="IBookmarkAdminClips"/>): another user's id is the same as one that doesn't exist.
/// </summary>
/// <remarks>
/// Public because the public controller takes it: DTOs, Jellyfin's public types and BCL types only; its implementation
/// maps the records to DTOs itself.
/// </remarks>
public interface IBookmarkClips
{
    /// <summary>
    /// Lists the caller's clips that match the filters, a page at a time (<c>GET /Bookmarks/Clips</c>), the clips being made
    /// first whatever the order. Whether the caller can open each video is asked of Jellyfin as the caller, for the clips on
    /// the page only.
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <param name="filter">The filters and page.</param>
    /// <param name="sortBy">The order; <see cref="BookmarkClipSortBy.Newest"/> by default.</param>
    /// <returns>The page, with the number of matching clips.</returns>
    /// <exception cref="Kit.Api.PluginException">400 for a search term over 100 characters, a negative start or a limit
    /// outside 1 to 200.</exception>
    QueryResult<BookmarkClipItemDto> GetClips(User user, BookmarkClipQueryFilter filter, BookmarkClipSortBy? sortBy);

    /// <summary>
    /// Counts the caller's clips (<c>GET /Bookmarks/Clips/Summary</c>): how many, the bytes their files take, and how many
    /// are being made, failed, missing their file or in a folder that is offline.
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <returns>The numbers.</returns>
    BookmarkClipSummaryDto GetSummary(User user);

    /// <summary>
    /// Gets the state of some of the caller's clips (<c>GET /Bookmarks/Clips/Status</c>), in the order asked, for the page's
    /// poll of the clips being made; ids that are no clip of the caller are left out.
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <param name="ids">The clips' ids: 1 to 200; an id given twice is answered once.</param>
    /// <returns>Each clip's state.</returns>
    /// <exception cref="Kit.Api.PluginException">400 for no ids or more than 200.</exception>
    IReadOnlyList<BookmarkClipStatusDto> GetStatus(User user, IReadOnlyList<Guid>? ids);

    /// <summary>
    /// Deletes several of the caller's clips (<c>POST /Bookmarks/Clips/BulkDelete</c>), each finished one as
    /// <c>DELETE /Bookmarks/{id}/Clip</c> would: the file first, a file in use tried again up to three times 200 ms apart;
    /// then a live bookmark keeps no clip and a deleted bookmark's record goes. A clip being made (a job waiting
    /// or running), one whose file is in use, whose folder is offline or whose file can't be deleted otherwise is
    /// left exactly as it was.
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <param name="request">The ids (1 to 200; one given twice counts once).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Each distinct id in one of the result's lists.</returns>
    /// <exception cref="Kit.Api.PluginException">400 for no ids or more than 200; 503 while the data is read-only, before
    /// anything is looked up.</exception>
    Task<BookmarkClipBulkDeleteResultDto> BulkDeleteAsync(User user, BookmarkClipBulkDeleteRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Renames one of the caller's clips (<c>POST /Bookmarks/Clips/{id}/Name</c>): its bookmark's name (a deleted
    /// bookmark's kept clip keeps one too), and with <see cref="BookmarkClipRenameRequest.RenameFile"/> its clip file,
    /// which then gets the name Bookmarks gives the clip now, in the same folder, numbered when that name is taken.
    /// Both in one change: a file in use (being played or downloaded) after three attempts 200 ms apart leaves the name
    /// as it was too.
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <param name="clipId">The clip's id (its bookmark's).</param>
    /// <param name="request">The name, whether the file follows, and the clip's UpdatedAt as last read.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The clip as it is now.</returns>
    /// <exception cref="Kit.Api.PluginException">400 a name over 100 characters; 404 no clip of the caller's, or (with the file)
    /// no file or its file gone; 409 a clip being made, changed since IfUpdatedAt, or its file in use; 503 read-only data, its
    /// clip folder offline or not recognised, or the file or the record couldn't be written. Nothing renamed in any
    /// case.</exception>
    Task<BookmarkClipItemDto> RenameAsync(User user, Guid clipId, BookmarkClipRenameRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Renames one of the caller's clip files to the name Bookmarks gives the clip now (<c>POST /Bookmarks/Clips/{id}/RenameFile</c>):
    /// after its bookmark was renamed elsewhere ("Name changed"); a number a taken name had goes once that name is
    /// free. The clip's name and UpdatedAt stay as they are; a file with that name already is left as it is.
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <param name="clipId">The clip's id.</param>
    /// <param name="ifUpdatedAt">The clip's UpdatedAt as the caller last read it: a clip renamed since is 409,
    /// rather than a file name other than the one the confirmation said.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The clip as it is now.</returns>
    /// <exception cref="Kit.Api.PluginException">404 no clip of the caller's, no file or its file gone; 409 being made, changed
    /// since <paramref name="ifUpdatedAt"/> or in use; 503 as for <see cref="RenameAsync"/>. Nothing renamed in any
    /// case.</exception>
    Task<BookmarkClipItemDto> RenameFileAsync(User user, Guid clipId, DateTime? ifUpdatedAt, CancellationToken cancellationToken);
}
