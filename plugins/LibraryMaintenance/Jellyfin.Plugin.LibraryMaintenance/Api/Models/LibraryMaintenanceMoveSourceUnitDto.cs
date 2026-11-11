using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// What moves with an item: its folder, or its files and sidecars.
/// </summary>
public sealed class LibraryMaintenanceMoveSourceUnitDto
{
    /// <summary>
    /// Gets or sets <c>Folder</c> or <c>FileSet</c>.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the unit's folder (a folder unit) or the folder its files are in (a file set).
    /// </summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the files.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceMoveFileDto> Files { get; set; } = [];

    /// <summary>
    /// Gets or sets the subfolders, relative to the root.
    /// </summary>
    public IReadOnlyList<string> Dirs { get; set; } = [];

    /// <summary>
    /// Gets or sets the size of the files.
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Gets or sets the path of the item's own file (the owner's, when Source was asked for a version or part), so the
    /// dialog can tell the members from the sidecars and other files in <see cref="Files"/>.
    /// </summary>
    public string Primary { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the paths of the local alternate versions that move.
    /// </summary>
    public IReadOnlyList<string> Versions { get; set; } = [];

    /// <summary>
    /// Gets or sets the paths of the additional parts that move.
    /// </summary>
    public IReadOnlyList<string> Parts { get; set; } = [];

    /// <summary>
    /// Gets or sets the paths of the extras that move.
    /// </summary>
    public IReadOnlyList<string> Extras { get; set; } = [];
}
