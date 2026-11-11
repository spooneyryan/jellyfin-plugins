namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The outcome of a free-space probe.
/// </summary>
internal enum DriveSpaceStatus
{
    /// <summary>
    /// The free space is known.
    /// </summary>
    Ok,

    /// <summary>
    /// The folder's drive or share isn't there: no folder at or above it exists (a drive letter that is gone, a share or
    /// server that doesn't exist), or the call fails with an error that says so (a junction or mount point whose volume is
    /// gone, a drive that isn't ready, a network that can't be reached). Clips are refused; the guard is not switched off.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The folder is there but its free space can't be read (access denied, an unsupported file system, a probe that took
    /// too long): the guard is off for the folder (the clip folder's <c>FreeSpaceUnknown</c> warning).
    /// </summary>
    Unknown,
}
