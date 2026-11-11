namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Reads the free space for a clip folder: the free-space guard's pre-check, claim-time re-check and watchdog, the
/// disk-full re-probe after a failed job, and Diagnostics <c>ClipStorage</c>. <see cref="DriveSpaceProbe"/> is the real
/// one.
/// </summary>
internal interface IDriveSpaceProbe
{
    /// <summary>
    /// Probes the nearest existing folder at or above <paramref name="folder"/>. Synchronous, and possibly slow on a share
    /// that doesn't answer: callers on a request or a timer go through <see cref="ClipStorageGuard"/>, which waits a limited
    /// time. Never throws.
    /// </summary>
    /// <param name="folder">The folder clips are (or will be) written to; it need not exist yet.</param>
    /// <returns>What was found.</returns>
    DriveSpace Get(string folder);
}
