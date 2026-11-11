using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A library an item can move to (<c>GET /Move/Destinations</c>): one of the source's type the caller can open.
/// </summary>
public sealed class LibraryMaintenanceMoveDestinationLibraryDto
{
    /// <summary>
    /// Gets or sets the library id.
    /// </summary>
    public Guid LibraryId { get; set; }

    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library type.
    /// </summary>
    public string? CollectionType { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is the item's own library.
    /// </summary>
    public bool IsSource { get; set; }

    /// <summary>
    /// Gets or sets its folders.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceMoveDestinationLocationDto> Locations { get; set; } = [];
}
