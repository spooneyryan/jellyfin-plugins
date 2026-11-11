using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// One item of a unit with its predicted new id.
/// </summary>
public sealed class LibraryMaintenanceMovePlanItemDto
{
    /// <summary>
    /// Gets or sets <c>Primary</c>, <c>Version</c>, <c>Part</c> or <c>Extra</c>.
    /// </summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the id now.
    /// </summary>
    public Guid OldId { get; set; }

    /// <summary>
    /// Gets or sets the id Jellyfin will give it at the new path.
    /// </summary>
    public Guid NewId { get; set; }

    /// <summary>
    /// Gets or sets the path now.
    /// </summary>
    public string From { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the new path.
    /// </summary>
    public string To { get; set; } = string.Empty;
}
