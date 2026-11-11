namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What one pass over <c>PendingFileDeletes</c> did (<see cref="ClipFileOps.RetryPendingDeletesAsync"/>).
/// </summary>
/// <param name="Deleted">Files deleted now; their entries are dropped.</param>
/// <param name="Gone">Files that were already gone; dropped.</param>
/// <param name="Refused">Entries <see cref="ClipFileGuard"/> refuses (outside the clip folders, a reparse point, not a clip
/// file); dropped without touching anything.</param>
/// <param name="Referenced">Entries whose path a record holds again (a new clip written under the same name); dropped
/// without deleting the file.</param>
/// <param name="Kept">Files still in use, or that couldn't be deleted or reached; kept with one more attempt counted.</param>
internal sealed record PendingFileDeleteRetry(int Deleted, int Gone, int Refused, int Referenced, int Kept)
{
    /// <summary>
    /// Gets the result of a pass that did nothing (an empty list, or read-only data).
    /// </summary>
    public static PendingFileDeleteRetry None { get; } = new(0, 0, 0, 0, 0);
}
