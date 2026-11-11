using System;
using Jellyfin.Plugin.Kit.Hosting;

namespace Jellyfin.Plugin.AccessRoles.Tasks;

/// <summary>
/// Access Roles' daily maintenance task, "Access Roles: Maintenance" at 03:50: the daily
/// snapshot of <c>accessroles.json</c> (<c>SnapshotsToKeep</c>), then the kit's user reconciliation and the audit retention
/// (both since 0.9.2).
/// </summary>
/// <remarks>
/// Jellyfin creates it once through <c>GetExports</c>, so it takes only the service provider and resolves the kit's
/// maintenance when it runs (the kit's <see cref="KitMaintenanceTaskBase"/>, which also makes it return at once on a
/// Jellyfin line this build isn't for).
/// </remarks>
public sealed class AccessRolesMaintenanceTask : KitMaintenanceTaskBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AccessRolesMaintenanceTask"/> class.
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    public AccessRolesMaintenanceTask(IServiceProvider services)
        : base(services, "Access Roles", new TimeSpan(3, 50, 0))
    {
    }
}
