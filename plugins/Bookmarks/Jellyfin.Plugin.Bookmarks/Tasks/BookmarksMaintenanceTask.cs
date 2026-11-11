using System;
using Jellyfin.Plugin.Kit.Hosting;

namespace Jellyfin.Plugin.Bookmarks.Tasks;

/// <summary>
/// Bookmarks' daily maintenance task, "Bookmarks: Maintenance" at 03:10: the
/// maintenance steps in order: the snapshot of <c>bookmarks.json</c> (<c>SnapshotsToKeep</c>), the retention archive step,
/// the user reconciliation (orphans only, never a deletion), the verify sweep of attached records, the re-resolution of
/// detached records, the reattach from the archive, and the retries of clip files waiting to be deleted. Since 1.1.0 they
/// work on the bookmarks (and on the developer demos, off by default); every step leaves read-only data alone.
/// </summary>
/// <remarks>
/// Jellyfin creates it once through <c>GetExports</c>, so it takes only the service provider and resolves the kit's
/// maintenance when it runs (the kit's <see cref="KitMaintenanceTaskBase"/>, which also makes it return at once on a
/// Jellyfin line this build isn't for).
/// </remarks>
public sealed class BookmarksMaintenanceTask : KitMaintenanceTaskBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarksMaintenanceTask"/> class.
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    public BookmarksMaintenanceTask(IServiceProvider services)
        : base(services, "Bookmarks", new TimeSpan(3, 10, 0))
    {
    }
}
