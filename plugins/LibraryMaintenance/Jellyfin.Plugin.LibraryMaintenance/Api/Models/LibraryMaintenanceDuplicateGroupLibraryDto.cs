using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A library a group entry's versions are in.
/// </summary>
public sealed class LibraryMaintenanceDuplicateGroupLibraryDto
{
    /// <summary>
    /// Gets or sets the library id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the library name.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}
