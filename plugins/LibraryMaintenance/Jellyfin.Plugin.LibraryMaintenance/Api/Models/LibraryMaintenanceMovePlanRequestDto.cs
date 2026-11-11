using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A plan request (<c>POST /Move/Plan</c>).
/// </summary>
public sealed class LibraryMaintenanceMovePlanRequestDto
{
    /// <summary>
    /// Gets or sets the items to move; exactly one in this version.
    /// </summary>
    public IReadOnlyList<Guid>? ItemIds { get; set; }

    /// <summary>
    /// Gets or sets where to.
    /// </summary>
    public LibraryMaintenanceMovePlanDestinationDto? Destination { get; set; }
}
