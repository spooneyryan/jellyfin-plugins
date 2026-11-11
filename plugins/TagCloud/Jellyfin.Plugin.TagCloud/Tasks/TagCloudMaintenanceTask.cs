using System;
using Jellyfin.Plugin.Kit.Hosting;

namespace Jellyfin.Plugin.TagCloud.Tasks;

/// <summary>
/// Tag Cloud's daily maintenance task, "Tag Cloud: Maintenance" at 03:40: the snapshot of
/// <c>clouds.json</c> (<c>SnapshotsToKeep</c>), the kit's user reconciliation, and a full rebuild of the index.
/// </summary>
/// <remarks>
/// Jellyfin creates it once through <c>GetExports</c>, so it takes only the service provider and resolves the kit's
/// maintenance when it runs (the kit's <see cref="KitMaintenanceTaskBase"/>, which also makes it return at once on a
/// Jellyfin line this build isn't for). Its time is the default trigger, which an admin can change under Scheduled Tasks.
/// </remarks>
public sealed class TagCloudMaintenanceTask : KitMaintenanceTaskBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TagCloudMaintenanceTask"/> class.
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    public TagCloudMaintenanceTask(IServiceProvider services)
        : base(services, "Tag Cloud", new TimeSpan(3, 40, 0))
    {
    }
}
