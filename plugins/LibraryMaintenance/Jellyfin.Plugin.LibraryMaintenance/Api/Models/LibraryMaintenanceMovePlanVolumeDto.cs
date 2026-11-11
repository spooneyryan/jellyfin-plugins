namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A volume a plan writes to.
/// </summary>
public sealed class LibraryMaintenanceMovePlanVolumeDto
{
    /// <summary>
    /// Gets or sets its mount point.
    /// </summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets its free space.
    /// </summary>
    public long? FreeBytes { get; set; }

    /// <summary>
    /// Gets or sets the space the move needs there (none for a rename on one volume).
    /// </summary>
    public long RequiredBytes { get; set; }
}
