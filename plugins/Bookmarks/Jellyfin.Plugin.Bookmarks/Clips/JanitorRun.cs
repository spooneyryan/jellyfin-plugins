namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What <see cref="PartialFileJanitor.RunAtStartupAsync"/> did.
/// </summary>
/// <param name="Partials">The stale <c>.partial</c> clean-up, or <c>null</c> when it didn't run: turned off
/// (<c>CleanUpPartialFilesOnStartup</c>), read-only data, or a failure the log names.</param>
/// <param name="PendingDeletes">The retry of the files waiting to be deleted.</param>
internal sealed record JanitorRun(StalePartialCleanup? Partials, PendingFileDeleteRetry PendingDeletes)
{
    /// <summary>
    /// Gets the result of a start that did nothing (read-only data).
    /// </summary>
    public static JanitorRun Idle { get; } = new(null, PendingFileDeleteRetry.None);

    /// <summary>
    /// Gets the clip file renames that weren't finished before the start, finished first
    /// (<see cref="ClipFileOps.FinishRenamesAsync"/>).
    /// </summary>
    public ClipRenameRecovery Renames { get; init; } = ClipRenameRecovery.None;

    /// <summary>
    /// Gets how many records named a path their clip file left in a rename and name the renamed file again (a
    /// restore made while the clip folder was away; <see cref="ClipFileOps.RelinkRenamedAsync"/>).
    /// </summary>
    public int Relinked { get; init; }
}
