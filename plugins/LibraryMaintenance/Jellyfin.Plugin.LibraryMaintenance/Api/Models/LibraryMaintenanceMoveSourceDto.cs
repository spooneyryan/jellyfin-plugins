namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// An item's source (<c>GET /Move/Source</c>): the item, its library and location, and what moves.
/// </summary>
public sealed class LibraryMaintenanceMoveSourceDto
{
    /// <summary>
    /// Gets or sets the item that moves (the owner, for a version or part).
    /// </summary>
    public LibraryMaintenanceMoveItemDto Item { get; set; } = new();

    /// <summary>
    /// Gets or sets its library.
    /// </summary>
    public LibraryMaintenanceMoveLibraryDto Library { get; set; } = new();

    /// <summary>
    /// Gets or sets the library folder it is in.
    /// </summary>
    public string LocationPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what moves.
    /// </summary>
    public LibraryMaintenanceMoveSourceUnitDto Unit { get; set; } = new();

    /// <summary>
    /// Gets or sets why another item moves than the one asked about ("This version belongs to Alien (1979); the whole folder moves.").
    /// </summary>
    public string? Retargeted { get; set; }
}
