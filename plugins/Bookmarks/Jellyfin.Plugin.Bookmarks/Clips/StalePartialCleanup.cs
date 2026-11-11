namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What one pass of the <c>.partial</c> janitor over the clip folders did.
/// </summary>
/// <param name="Deleted">Stale <c>.partial</c> files deleted.</param>
/// <param name="NotDeleted">Stale <c>.partial</c> files that couldn't be deleted (in use, no permission, a drive not ready,
/// refused by the guard); left for the next start.</param>
/// <param name="Newer">Files named as clips being written but last written at or after the server's start, so not left by
/// an earlier run; kept.</param>
/// <param name="Folders">Recorded clip folders that were there and listed.</param>
/// <param name="FoldersNotThere">Recorded clip folders that weren't there (an unplugged drive, a folder moved or removed)
/// or couldn't be listed; nothing in them was touched.</param>
internal sealed record StalePartialCleanup(int Deleted, int NotDeleted, int Newer, int Folders, int FoldersNotThere);
