using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Model;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// Bookmark operations on behalf of a user. Users only ever see and change their own records. A bookmark deleted with
/// its clip file kept stays as a clip-only tombstone: every bookmark operation answers 404 for it, and only
/// the clip file (<see cref="GetClipFilePath"/>) and deleting the clip (<see cref="DeleteClipAsync"/>) still reach it.
/// While the data is read-only every change is a 503 with the store's message, one that names a bookmark
/// before the bookmark is looked up (a failed upgrade's store is an empty stand-in). Expected failures are reported as
/// <see cref="BookmarkException"/>, the store's as the kit's <c>PluginException</c>.
/// </summary>
/// <remarks>
/// Public because the public controller takes it: its members use only DTOs, Jellyfin's public types and BCL
/// types, never the internal records, so the service maps records to DTOs itself.
/// </remarks>
public interface IBookmarkManager
{
    /// <summary>
    /// Gets the user's bookmarks.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="itemId">Limits the result to one item; ordered by position. Without it, most recently changed first.</param>
    /// <returns>The bookmarks.</returns>
    IReadOnlyList<BookmarkDto> GetBookmarks(User user, Guid? itemId);

    /// <summary>
    /// Gets one of the user's bookmarks.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="bookmarkId">The bookmark id.</param>
    /// <returns>The bookmark.</returns>
    BookmarkDto GetBookmark(User user, Guid bookmarkId);

    /// <summary>
    /// Creates a bookmark, named when the request gives a name (normalised; 400 over 100 characters).
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="request">The bookmark to create.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The new bookmark.</returns>
    Task<BookmarkDto> CreateBookmarkAsync(User user, BookmarkCreateRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a bookmark's position, range end, name and note: a name or note the request leaves out is cleared (replace
    /// semantics), except that Bookmarks 1.0's body, with neither <see cref="BookmarkUpdateRequest.Name"/> nor
    /// <see cref="BookmarkUpdateRequest.IfUpdatedAt"/>, keeps the name. With
    /// <see cref="BookmarkUpdateRequest.IfUpdatedAt"/> set, a bookmark changed since is a 409 and nothing is written;
    /// checked under the store's lock. Clears the "Relinked, check position" badge.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="bookmarkId">The bookmark id.</param>
    /// <param name="request">The new values.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated bookmark.</returns>
    Task<BookmarkDto> UpdateBookmarkAsync(User user, Guid bookmarkId, BookmarkUpdateRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Names, renames or unnames a bookmark; nothing else of it changes. Every rename is a
    /// change, the same name again too: it clears the "Relinked, check position" badge ("Looks right"). With
    /// <see cref="BookmarkRenameRequest.IfUpdatedAt"/> set, a bookmark changed since is a 409 and nothing is written. A
    /// clip file keeps the name it was made with; the next clip uses the new one.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="bookmarkId">The bookmark id.</param>
    /// <param name="request">The new name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The renamed bookmark.</returns>
    Task<BookmarkDto> RenameBookmarkAsync(User user, Guid bookmarkId, BookmarkRenameRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a bookmark, cancelling any clip job for it. With <paramref name="deleteClipFile"/> the record and its clip
    /// file go, the file first: a file in use is a 409 with the bookmark, its job and its file unchanged, and a
    /// file outside the clip folders a 404 with nothing touched. Without it, a bookmark with a clip file stays as a
    /// clip-only tombstone that keeps the file, and one without is removed.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="bookmarkId">The bookmark id.</param>
    /// <param name="deleteClipFile">Whether to delete the bookmark's clip file too.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the bookmark is deleted.</returns>
    Task DeleteBookmarkAsync(User user, Guid bookmarkId, bool deleteClipFile, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes several of the user's bookmarks at once (the bulk bar), each as
    /// <see cref="DeleteBookmarkAsync"/> would: without <see cref="BookmarkBulkDeleteRequest.DeleteClips"/> in one write, a
    /// bookmark with a clip file becoming a clip-only tombstone; with it, each clip file first, a file in use
    /// tried again up to three times 200 ms apart and then left, with its bookmark, as it was. Ids that are no bookmark of
    /// the user (unknown, another user's, a tombstone) change nothing and are reported as not found, so nothing is learned
    /// about other users' bookmarks.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="request">The ids (1 to 200; one given twice counts once) and whether their clip files go too.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Each distinct id in one of the result's lists.</returns>
    /// <exception cref="Kit.Api.PluginException">400 for no ids or more than 200; 503 while the data is read-only, before
    /// anything is looked up.</exception>
    Task<BookmarkBulkDeleteResultDto> BulkDeleteAsync(User user, BookmarkBulkDeleteRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Queues a clip of the bookmark's range. The free-space guard checks the clip folder's drive first: a
    /// 503 when the clip's estimated size and those of the clips waiting or being made would leave less than the reserve,
    /// or when the clip folder's drive isn't there; nothing is queued or written then.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="bookmarkId">The bookmark id.</param>
    /// <param name="mode">The extraction mode, or <c>null</c> for the server default.</param>
    /// <param name="isAdmin">Whether the request passes Jellyfin's <c>RequiresElevation</c> policy: an admin's 503 names the
    /// drive and the numbers, a user's doesn't.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The bookmark with its queued clip.</returns>
    Task<BookmarkDto> StartClipAsync(User user, Guid bookmarkId, BookmarkClipMode? mode, bool isAdmin, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels the bookmark's queued or running clip job. An existing clip file is kept.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="bookmarkId">The bookmark id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The bookmark.</returns>
    Task<BookmarkDto> CancelClipAsync(User user, Guid bookmarkId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the bookmark's clip file, then cancels its clip job, if any. The bookmark stays; a clip-only tombstone
    /// is only its clip, so its record is removed with the file. The file goes first: a file in use
    /// is a 409 with the record, its job and its file unchanged, and a file outside the clip folders a 404 with nothing
    /// touched.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="bookmarkId">The bookmark id, or a tombstone's.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated bookmark, or <c>null</c> when a tombstone was removed.</returns>
    Task<BookmarkDto?> DeleteClipAsync(User user, Guid bookmarkId, CancellationToken cancellationToken);

    /// <summary>
    /// Forgets the bookmark's clip as <see cref="DeleteClipAsync"/> deletes it, but only while it has no file there: its file
    /// gone from a clip folder that is there, or none at all (My Clips' Forget and Dismiss). It never deletes a file: one
    /// that is there (made again or put back since the caller looked) is a 409 with nothing changed; a clip folder that
    /// can't be reached is a 503 and a path outside the clip folders a 404, as for the delete.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="bookmarkId">The bookmark id, or a tombstone's.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated bookmark, or <c>null</c> when a tombstone was removed.</returns>
    Task<BookmarkDto?> ForgetClipAsync(User user, Guid bookmarkId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the path of a bookmark's clip file for download, a clip-only tombstone's too, only from the clip
    /// folders. The file may predate the latest job.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="bookmarkId">The bookmark id.</param>
    /// <returns>The clip file path.</returns>
    string GetClipFilePath(User user, Guid bookmarkId);

    /// <summary>
    /// Gets what the user can do with bookmarks, and since 1.1 the limits, the server's shortcuts and page settings and the
    /// user's own naming preferences with the server's defaults.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="isAdmin">Whether the request passes Jellyfin's <c>RequiresElevation</c> policy (the kit's
    /// <c>CurrentUser.IsElevatedAsync</c>), never the stored administrator flag.</param>
    /// <returns>The capabilities.</returns>
    BookmarkCapabilitiesDto GetCapabilities(User user, bool isAdmin);
}
