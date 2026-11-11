using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// Where an item should go (<c>POST /Move/Plan</c>).
/// </summary>
public sealed class LibraryMaintenanceMovePlanDestinationDto
{
    /// <summary>
    /// Gets or sets the destination library.
    /// </summary>
    public Guid LibraryId { get; set; }

    /// <summary>
    /// Gets or sets the index of its folder.
    /// </summary>
    public int LocationIndex { get; set; }

    /// <summary>
    /// Gets or sets an existing folder under it, backslash- or slash-separated; empty for the library folder itself.
    /// </summary>
    public string? RelativePath { get; set; }

    /// <summary>
    /// Gets or sets a new folder to create there (a new series folder for an episode).
    /// </summary>
    public string? NewFolderName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a loose movie file gets its own folder; null follows <c>LooseFileFolderMode</c>.
    /// </summary>
    public bool? Wrap { get; set; }

    /// <summary>
    /// Gets or sets that folder's name; by default <c>Title (Year)</c>, cleaned.
    /// </summary>
    public string? WrapFolderName { get; set; }
}
