using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// One unit of a plan.
/// </summary>
public sealed class LibraryMaintenanceMovePlanUnitDto
{
    /// <summary>
    /// Gets or sets the item that moves.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets its name and year.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets <c>Folder</c> or <c>FileSet</c>.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the unit's folder (a folder unit) or the folder its files are in.
    /// </summary>
    public string From { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where that ends up.
    /// </summary>
    public string To { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many files move.
    /// </summary>
    public int Files { get; set; }

    /// <summary>
    /// Gets or sets their size.
    /// </summary>
    public long Bytes { get; set; }

    /// <summary>
    /// Gets or sets the folder a loose file is put in, when it is.
    /// </summary>
    public string? WrapFolderName { get; set; }

    /// <summary>
    /// Gets or sets the folders the move creates.
    /// </summary>
    public IReadOnlyList<string> CreatedFolders { get; set; } = [];

    /// <summary>
    /// Gets or sets its items and their new ids.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceMovePlanItemDto> PredictedIds { get; set; } = [];
}
