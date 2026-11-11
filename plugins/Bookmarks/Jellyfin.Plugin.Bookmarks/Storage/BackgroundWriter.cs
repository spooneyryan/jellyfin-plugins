namespace Jellyfin.Plugin.Bookmarks.Storage;

/// <summary>
/// The parts of Bookmarks that write <c>bookmarks.json</c> on their own, with no user waiting for the answer: each
/// asks <see cref="BackgroundWriteGate"/> before it works and stays idle while the store is read-only.
/// </summary>
internal enum BackgroundWriter
{
    /// <summary>
    /// The clip worker (<c>ClipExtractionService</c>): resuming the clip jobs interrupted by the last shutdown, and running
    /// queued ones, whose status changes are writes.
    /// </summary>
    ClipWorker,

    /// <summary>
    /// The startup clean-up of stale <c>.partial</c> clip files and the retries of <c>PendingFileDeletes</c>. It runs in
    /// the clip worker's startup, after its gate and before the resumed jobs.
    /// </summary>
    PartialFileJanitor,

    /// <summary>
    /// The identity service: the identity backfill, detaching and reattaching on library changes,
    /// the move-journal relinks and the deleted-user policy on the live event.
    /// </summary>
    IdentityService,

    /// <summary>
    /// The daily maintenance task: snapshots, user reconciliation (<c>KnownUsers</c>), re-resolving
    /// detached records, <c>PendingFileDeletes</c> retries and, from 1.2, the missing-clip sweep.
    /// </summary>
    Maintenance
}
