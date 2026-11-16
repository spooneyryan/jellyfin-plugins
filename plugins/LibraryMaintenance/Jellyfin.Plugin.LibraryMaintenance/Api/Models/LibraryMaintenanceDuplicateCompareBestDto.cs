using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// The copies with the best value of one compared detail.
/// </summary>
public sealed class LibraryMaintenanceDuplicateCompareBestDto
{
    /// <summary>
    /// Gets or sets the detail, such as <c>Resolution</c> or <c>AudioTracks</c>.
    /// </summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item ids of the copies with the highest value (more than one on a tie).
    /// </summary>
    public IReadOnlyList<Guid> ItemIds { get; set; } = [];
}
