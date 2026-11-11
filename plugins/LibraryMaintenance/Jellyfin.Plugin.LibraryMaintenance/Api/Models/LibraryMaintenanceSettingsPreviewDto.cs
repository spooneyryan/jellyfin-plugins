using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// What a change to the index's scope (the included libraries, the excluded folders) does, before it is saved:
/// the movies the index holds now and would hold after, from a build with the new settings that only reads the library.
/// </summary>
public sealed class LibraryMaintenanceSettingsPreviewDto
{
    /// <summary>
    /// Gets or sets a value indicating whether the movies were counted: not for an administrator who can't open every
    /// movie library, or with a parental rating or tag limit (the counts cover every library).
    /// </summary>
    public bool Counted { get; set; }

    /// <summary>
    /// Gets or sets why the movies weren't counted, or null.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// Gets or sets when the index the "before" counts come from was built; null when there is none yet.
    /// </summary>
    public DateTime? IndexBuiltUtc { get; set; }

    /// <summary>
    /// Gets or sets the movies the index holds now.
    /// </summary>
    public int? MoviesBefore { get; set; }

    /// <summary>
    /// Gets or sets the movies it would hold after the change.
    /// </summary>
    public int? MoviesAfter { get; set; }

    /// <summary>
    /// Gets or sets the movies the excluded folders leave out now.
    /// </summary>
    public int? ExcludedBefore { get; set; }

    /// <summary>
    /// Gets or sets the movies the excluded folders would leave out after the change.
    /// </summary>
    public int? ExcludedAfter { get; set; }

    /// <summary>
    /// Gets or sets the libraries read before or after, in the server's order.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceSettingsPreviewLibraryDto> Libraries { get; set; } = [];

    /// <summary>
    /// Gets or sets the excluded folders added or removed.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceSettingsPreviewFolderDto> Folders { get; set; } = [];
}
