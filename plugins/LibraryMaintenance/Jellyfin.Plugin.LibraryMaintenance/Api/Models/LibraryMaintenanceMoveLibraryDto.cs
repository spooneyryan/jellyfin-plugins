using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A library as Move names it.
/// </summary>
public sealed class LibraryMaintenanceMoveLibraryDto
{
    /// <summary>
    /// Gets or sets the library id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library type: <c>movies</c>, <c>tvshows</c> or <c>musicvideos</c>.
    /// </summary>
    public string? CollectionType { get; set; }
}
