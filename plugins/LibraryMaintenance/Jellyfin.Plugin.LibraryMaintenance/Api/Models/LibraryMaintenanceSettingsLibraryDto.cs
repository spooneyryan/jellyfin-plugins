using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A library the Settings tab offers: a <c>movies</c> or mixed library of the server, or an included id that is
/// no longer one.
/// </summary>
public sealed class LibraryMaintenanceSettingsLibraryDto
{
    /// <summary>
    /// Gets or sets the library id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the library name; empty for one that is <see cref="Missing"/>.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library's kind: <c>movies</c>, <c>mixed</c>, or empty for one that is <see cref="Missing"/>.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the library is turned on in Jellyfin; one turned off is never read.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the settings name it in <c>IncludedLibraryIds</c>.
    /// </summary>
    public bool Included { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the settings name an id that is no longer a movie or mixed library here.
    /// </summary>
    public bool Missing { get; set; }
}
