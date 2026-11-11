using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// A plugin service that handles library changes in batches. Register it as a singleton
/// <c>ILibraryChangeConsumer</c> next to <c>AddKitLibraryChanges()</c>; <see cref="LibraryChangeMonitor"/> finds every one
/// when it is created.
/// </summary>
/// <remarks>
/// <para>Batches reach the consumers one after another, one batch at a time: a timer batch when the consumer's quiet
/// period or maximum wait is over (never while a library scan runs), and the full pass inside every scan, which the
/// post-scan task awaits (keep it short, or hand the work to a background worker and return). The full pass runs before
/// Jellyfin's own post-scan tasks: what they change (by-name music artists, genres, studios, box sets, items whose
/// parent is gone; a removed by-name artist in <see cref="LibraryChangeBatch.RemovedFolders"/>) comes in the next
/// batch, <see cref="LibraryChangeBatch.AfterFullPass"/>, as soon as the scan has ended, so handle Removed and
/// <c>RemovedFolders</c> in every batch, not only in full passes. Each call is wrapped in a try/catch: an exception is
/// logged and counted, and that batch is gone for this consumer (the startup verify pass and the daily sweep close such
/// gaps).</para>
/// <para>Handle a batch in the order Removed → verify sweep (if <see cref="LibraryChangeBatch.RemovedFolders"/> isn't
/// empty) → Added/Updated. When <see cref="LibraryChangeBatch.DuringPostScan"/> is true, <c>IsScanRunning</c> is true
/// and that is expected: don't defer or report the library unhealthy because of it. A consumer that writes a store asks
/// its plugin's background-write gate first, like any automatic writer.</para>
/// </remarks>
internal interface ILibraryChangeConsumer
{
    /// <summary>
    /// Gets the consumer's name for logs and Diagnostics (a short identifier, no user data).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the consumer's filter and timing, read once when the monitor is created.
    /// </summary>
    LibraryChangeFilter Filter { get; }

    /// <summary>
    /// Handles a batch.
    /// </summary>
    /// <param name="batch">The changes.</param>
    /// <param name="cancellationToken">Cancelled when the server stops or the scan whose full pass this is is cancelled.</param>
    /// <returns>A task that completes when the batch is handled.</returns>
    Task HandleAsync(LibraryChangeBatch batch, CancellationToken cancellationToken);
}
