using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Model;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Queues and tracks clip extraction jobs.
/// </summary>
/// <remarks>
/// Jobs run only for live bookmarks. A clip-only tombstone gets no new job, and whatever a job still running
/// for a bookmark that became one reports (done, failed, cancelled, interrupted) leaves the tombstone as it is.
/// </remarks>
internal interface IClipExtractionService
{
    /// <summary>
    /// Queues a clip of the bookmark's current range. Does nothing if a job is already queued or running, and nothing for
    /// a record that isn't the owner's live bookmark (a tombstone, or another user's since the caller looked), checked
    /// under the store's lock.
    /// </summary>
    /// <param name="bookmarkId">The bookmark.</param>
    /// <param name="ownerId">The user the bookmark must belong to: the one asking.</param>
    /// <param name="mode">The extraction mode.</param>
    /// <param name="estimatedBytes">The free-space guard's estimate of the clip's size, kept with the job
    /// (<c>ClipInfo.EstimatedBytes</c>), or <c>null</c> when it can't be estimated.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The bookmark after the call, or <c>null</c> if it doesn't exist or isn't the owner's live bookmark.</returns>
    Task<Bookmark?> EnqueueAsync(Guid bookmarkId, Guid ownerId, BookmarkClipMode mode, long? estimatedBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels the bookmark's queued or running job, if there is one. A tombstone's clip is never changed.
    /// </summary>
    /// <param name="bookmarkId">The bookmark.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>A task that completes once the cancellation has been requested.</returns>
    Task CancelAsync(Guid bookmarkId, CancellationToken cancellationToken);

    /// <summary>
    /// Stops the bookmark's running job, if there is one, without writing anything: for a record that was just removed or
    /// made a tombstone, whose job could have been claimed after the delete's <see cref="CancelAsync"/> and before
    /// its write. The worker then records nothing on it.
    /// </summary>
    /// <param name="bookmarkId">The bookmark.</param>
    void StopRunning(Guid bookmarkId);

    /// <summary>
    /// Gets the progress of a running job.
    /// </summary>
    /// <param name="bookmarkId">The bookmark.</param>
    /// <returns>Progress from 0 to 1, or <c>null</c> if no job is running for the bookmark.</returns>
    double? GetProgress(Guid bookmarkId);
}
