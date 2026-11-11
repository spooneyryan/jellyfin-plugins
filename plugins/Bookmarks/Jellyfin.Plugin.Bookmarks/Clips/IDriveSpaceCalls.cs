namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The operating system calls behind <see cref="DriveSpaceProbe"/>: a seam, so tests can make a drive letter disappear, a
/// junction's target vanish or a share refuse the call without changing the machine. <see cref="DriveSpaceProbe"/> has the
/// real ones (Windows: <c>GetDiskFreeSpaceExW</c>; elsewhere <see cref="System.IO.DriveInfo"/>).
/// </summary>
internal interface IDriveSpaceCalls
{
    /// <summary>
    /// Whether a folder is at the path, as <see cref="System.IO.Directory.Exists"/> sees it: true for a junction or mount
    /// point even when its target is gone, which the free-space call then reports.
    /// </summary>
    /// <param name="path">A full path.</param>
    /// <returns>Whether a folder is there.</returns>
    bool FolderExists(string path);

    /// <summary>
    /// Reads the free space of the volume an existing folder is on.
    /// </summary>
    /// <param name="folder">The folder, with a trailing separator (UNC roots need one on Windows).</param>
    /// <returns>The numbers, or the error.</returns>
    DriveSpaceCall GetFreeSpace(string folder);

    /// <summary>
    /// Gets the mount point of the volume a folder is on, for messages.
    /// </summary>
    /// <param name="folder">An existing folder.</param>
    /// <returns>The mount point, or <c>null</c>.</returns>
    string? GetVolume(string folder);
}
