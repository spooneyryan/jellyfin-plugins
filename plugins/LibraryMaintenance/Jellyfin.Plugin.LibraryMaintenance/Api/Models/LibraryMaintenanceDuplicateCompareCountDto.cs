namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A count of special features of one kind, or in one place.
/// </summary>
public sealed class LibraryMaintenanceDuplicateCompareCountDto
{
    /// <summary>
    /// Gets or sets the kind or the place.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an extras folder's name (<c>Featurettes</c>) for the place <c>ExtrasFolder</c>, else null.
    /// </summary>
    public string? Folder { get; set; }

    /// <summary>
    /// Gets or sets how many.
    /// </summary>
    public int Count { get; set; }
}
