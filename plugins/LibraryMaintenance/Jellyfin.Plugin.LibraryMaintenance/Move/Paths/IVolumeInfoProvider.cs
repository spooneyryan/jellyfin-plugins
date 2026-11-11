namespace Jellyfin.Plugin.LibraryMaintenance.Move.Paths;

/// <summary>
/// Tells which volume a path is on.
/// </summary>
internal interface IVolumeInfoProvider
{
    /// <summary>
    /// Gets the volume of a path, or of its nearest existing folder above it.
    /// </summary>
    /// <param name="path">A full local path.</param>
    /// <returns>The volume, or null when it can't be told (not a local path, nothing of it exists, Win32 refused).</returns>
    VolumeInfo? GetVolume(string path);
}
