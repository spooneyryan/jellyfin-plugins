namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// One extra of a copy.
/// </summary>
public sealed class LibraryMaintenanceDuplicateCompareFeatureDto
{
    /// <summary>
    /// Gets or sets the name Jellyfin gives it.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets its file's name (Jellyfin often names every extra after the film), or null for one with no file.
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// Gets or sets its kind (<c>Trailer</c>, <c>Featurette</c>…; <c>Unknown</c> for an extra of no kind).
    /// </summary>
    public string Kind { get; set; } = "Unknown";

    /// <summary>
    /// Gets or sets where it is: <c>BesideFile</c>, <c>ExtrasFolder</c> or <c>Elsewhere</c>.
    /// </summary>
    public string Location { get; set; } = "Elsewhere";

    /// <summary>
    /// Gets or sets the extras folder's path relative to the copy's folder (<c>Featurettes</c>) for
    /// <c>ExtrasFolder</c>, else null.
    /// </summary>
    public string? Folder { get; set; }

    /// <summary>
    /// Gets or sets its runtime in ticks, or null.
    /// </summary>
    public long? RunTimeTicks { get; set; }
}
