namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// The two provider ids the index keeps, as Jellyfin stores them.
/// </summary>
public sealed class LibraryMaintenanceDuplicateProviderIdsDto
{
    /// <summary>
    /// Gets or sets the TMDb id, or null.
    /// </summary>
    public string? Tmdb { get; set; }

    /// <summary>
    /// Gets or sets the IMDb id, or null.
    /// </summary>
    public string? Imdb { get; set; }
}
