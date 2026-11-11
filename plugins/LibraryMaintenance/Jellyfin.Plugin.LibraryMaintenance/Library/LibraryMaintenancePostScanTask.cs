using System;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.LibraryMaintenance.Library;

/// <summary>
/// Library Maintenance's post-scan task: inside every library scan, once the scan has read the
/// library folders, it flushes the kit's library-change monitor as a full pass to Duplicates' consumer, which has the movie
/// index built once then (in the background). It runs before Jellyfin's own post-scan tasks, whose changes reach the
/// consumer as the batch right after the scan.
/// </summary>
/// <remarks>
/// Jellyfin creates it once at startup and runs the same instance for every scan, so it takes only the service provider and
/// keeps no state (the kit's <see cref="KitPostScanTaskBase"/>, which also makes it return at once on a Jellyfin line this
/// build isn't for).
/// </remarks>
public sealed class LibraryMaintenancePostScanTask : KitPostScanTaskBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryMaintenancePostScanTask"/> class.
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    public LibraryMaintenancePostScanTask(IServiceProvider services)
        : base(services)
    {
    }
}
