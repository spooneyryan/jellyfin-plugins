namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// One free-space call's answer (<see cref="IDriveSpaceCalls.GetFreeSpace"/>).
/// </summary>
/// <param name="Ok">Whether the call worked.</param>
/// <param name="AvailableBytes">The bytes the server may still write, when it worked.</param>
/// <param name="TotalBytes">The volume's size as the server sees it, when it worked.</param>
/// <param name="Error">The error code when it failed (a Win32 error on Windows).</param>
internal readonly record struct DriveSpaceCall(bool Ok, long AvailableBytes, long TotalBytes, int Error)
{
    /// <summary>
    /// A call that worked.
    /// </summary>
    /// <param name="availableBytes">The bytes the server may still write.</param>
    /// <param name="totalBytes">The volume's size.</param>
    /// <returns>The answer.</returns>
    public static DriveSpaceCall Worked(long availableBytes, long totalBytes) => new(true, availableBytes, totalBytes, 0);

    /// <summary>
    /// A call that failed.
    /// </summary>
    /// <param name="error">The error code.</param>
    /// <returns>The answer.</returns>
    public static DriveSpaceCall Failed(int error) => new(false, 0, 0, error);
}
