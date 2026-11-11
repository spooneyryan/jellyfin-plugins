namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// One folder of a destination library.
/// </summary>
public sealed class LibraryMaintenanceMoveDestinationLocationDto
{
    /// <summary>
    /// Gets or sets its index in the library's folders.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Gets or sets the folder as configured.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the drive's label.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the folder is on a local drive and there; network and device paths are never opened, so they read false.
    /// </summary>
    public bool Reachable { get; set; }

    /// <summary>
    /// Gets or sets the free space on its drive.
    /// </summary>
    public long? FreeBytes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is on the source's volume (by volume GUID, not drive letter).
    /// </summary>
    public bool SameVolumeAsSource { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it can be chosen.
    /// </summary>
    public bool Selectable { get; set; }

    /// <summary>
    /// Gets or sets why not: <c>cross-volume-later</c>, <c>network-location</c>, <c>device-path</c>, <c>not-a-local-path</c>, <c>unreachable</c>, <c>ignored</c> or <c>reparse-point</c> (the folder is a junction or symbolic link, never opened).
    /// </summary>
    public string? Reason { get; set; }
}
