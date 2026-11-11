using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserRatings.Storage;

/// <summary>
/// The checkpoint asked for after the commit of a write that took text away, so the text leaves the write-ahead log
/// too: a member's delete, review clear or review edit, a moderator's removal, a deleted user's policy, an archive purge.
/// </summary>
/// <remarks>
/// The request returns at once, so no request's thread waits for it (<see cref="IRatingsStore.RequestCheckpoint"/>); its
/// outcome is handled here: one the kit couldn't complete (its tries ran out, logged by the kit at Warning, or the store is
/// read-only) is noted at Information with what it followed, and stays pending for the next write and the daily
/// maintenance's <c>Checkpoint</c> step. The note names ids only, never text.
/// </remarks>
internal static class RatingsCheckpoint
{
    /// <summary>
    /// Asks for the checkpoint after a write and handles its outcome in the background.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="logger">The logger of the component that wrote.</param>
    /// <param name="after">What the write was, with ids only (for example "the member's delete of rating …").</param>
    /// <remarks>Tests wait for its outcome with <see cref="IRatingsStore.WaitForCheckpointAsync"/>.</remarks>
    public static void RequestAfter(IRatingsStore store, ILogger logger, string after)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        Task<bool> checkpoint;
        try
        {
            checkpoint = store.RequestCheckpoint();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The checkpoint of userratings.db after {After} couldn't be asked for; the daily maintenance's Checkpoint step does it", after);
            return;
        }

        _ = checkpoint.ContinueWith(
            done =>
            {
                if (done.IsCompletedSuccessfully && !done.Result)
                {
                    logger.LogInformation("The checkpoint of userratings.db after {After} is still pending (Diagnostics Store.Checkpoint): the earlier versions of its pages stay in userratings.db-wal until the next write or the daily maintenance completes it", after);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
