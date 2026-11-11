using System;
using Jellyfin.Plugin.Kit.Hosting;

namespace Jellyfin.Plugin.SmartSearch.Storage;

/// <summary>
/// Smart Search's daily maintenance task, "Smart Search: Maintenance" at 03:30: the snapshot of
/// <c>saved-queries.json</c> (<c>SnapshotsToKeep</c>), then the kit's user reconciliation (unknown owners listed as
/// orphans, never a deletion). Every step leaves read-only data alone.
/// </summary>
/// <remarks>
/// Jellyfin creates it once through <c>GetExports</c>, so it takes only the service provider and resolves the kit's
/// maintenance when it runs (and returns at once on a Jellyfin line this build isn't for).
/// </remarks>
public sealed class SmartSearchMaintenanceTask : KitMaintenanceTaskBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SmartSearchMaintenanceTask"/> class.
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    public SmartSearchMaintenanceTask(IServiceProvider services)
        : base(services, "Smart Search", new TimeSpan(3, 30, 0))
    {
    }
}
