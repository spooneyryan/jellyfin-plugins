namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A subfolder in the destination picker (<c>GET /Move/Folders</c>).
/// </summary>
public sealed class LibraryMaintenanceMoveFolderDto
{
    /// <summary>
    /// Gets or sets the name as on disk.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path relative to the library folder.
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets <c>Plain</c>, <c>MovieFolder</c>, <c>Series</c>, <c>Season</c>, <c>Ignored</c> or <c>ReparsePoint</c>.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether it can be chosen.
    /// </summary>
    public bool Selectable { get; set; }

    /// <summary>
    /// Gets or sets why not, as a sentence.
    /// </summary>
    public string? Reason { get; set; }
}
