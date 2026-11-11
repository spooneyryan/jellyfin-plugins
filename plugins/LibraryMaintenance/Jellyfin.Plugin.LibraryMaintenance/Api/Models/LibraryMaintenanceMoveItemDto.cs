using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// An item as Move names it (<c>Source</c>).
/// </summary>
public sealed class LibraryMaintenanceMoveItemDto
{
    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the production year.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets the kind: <c>Movie</c>, <c>Episode</c> or <c>MusicVideo</c>.
    /// </summary>
    public string Kind { get; set; } = string.Empty;
}
