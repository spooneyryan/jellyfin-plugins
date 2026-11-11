using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A page of subfolders (<c>GET /Move/Folders</c>).
/// </summary>
public sealed class LibraryMaintenanceMoveFoldersDto
{
    /// <summary>
    /// Gets or sets the folders on this page.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceMoveFolderDto> Items { get; set; } = [];

    /// <summary>
    /// Gets or sets how many folders match.
    /// </summary>
    public int TotalRecordCount { get; set; }

    /// <summary>
    /// Gets or sets the index of the first folder on this page.
    /// </summary>
    public int StartIndex { get; set; }

    /// <summary>
    /// Gets or sets the folder listed.
    /// </summary>
    public string Path { get; set; } = string.Empty;
}
