using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A library in a settings preview: whether the index reads it before and after the change, and its movies.
/// </summary>
public sealed class LibraryMaintenanceSettingsPreviewLibraryDto
{
    /// <summary>
    /// Gets or sets the library id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the library name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what the change does to it: <c>Joins</c>, <c>Leaves</c> or <c>Stays</c>.
    /// </summary>
    public string Change { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the movies the index holds from it now; null when not counted or not read.
    /// </summary>
    public int? MoviesBefore { get; set; }

    /// <summary>
    /// Gets or sets the movies it would hold from it after the change; null when not counted or not read.
    /// </summary>
    public int? MoviesAfter { get; set; }
}
