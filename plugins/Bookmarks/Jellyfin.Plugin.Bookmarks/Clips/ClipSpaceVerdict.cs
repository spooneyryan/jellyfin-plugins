namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The free-space guard's verdict on a clip.
/// </summary>
internal enum ClipSpaceVerdict
{
    /// <summary>
    /// Enough space would remain.
    /// </summary>
    Ok,

    /// <summary>
    /// Less than the reserve would remain: refused (503 on a request; the job fails when it is about to start).
    /// </summary>
    LowSpace,

    /// <summary>
    /// The clip folder's drive or share isn't there: refused (the guard is not switched off).
    /// </summary>
    Unavailable,

    /// <summary>
    /// Free space can't be read for the folder: the guard is off for it, and the clip goes ahead (the clip folder's
    /// <c>FreeSpaceUnknown</c> warning).
    /// </summary>
    GuardOff,
}
