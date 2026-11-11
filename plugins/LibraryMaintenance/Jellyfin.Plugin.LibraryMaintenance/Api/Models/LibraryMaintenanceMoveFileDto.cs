namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// One file of a unit.
/// </summary>
public sealed class LibraryMaintenanceMoveFileDto
{
    /// <summary>
    /// Gets or sets the path relative to the unit root (a folder unit) or the file's folder (a file set).
    /// </summary>
    public string Rel { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the size.
    /// </summary>
    public long Bytes { get; set; }
}
