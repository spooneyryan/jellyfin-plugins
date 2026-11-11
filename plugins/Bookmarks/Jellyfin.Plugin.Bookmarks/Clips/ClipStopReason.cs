namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Why a running clip job was stopped (<see cref="RunningClip.Stop"/>).
/// </summary>
internal enum ClipStopReason
{
    /// <summary>
    /// It wasn't stopped (a server shutdown stops it through the service's token, without a reason).
    /// </summary>
    None,

    /// <summary>
    /// It was cancelled, or its bookmark or clip was deleted: the job ends <c>Cancelled</c>.
    /// </summary>
    Cancelled,

    /// <summary>
    /// The free-space watchdog stopped it: the job ends <c>Failed</c> with "Stopped: the clip drive is almost
    /// full.".
    /// </summary>
    LowDiskSpace,
}
