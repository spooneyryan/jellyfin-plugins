using System;
using Jellyfin.Plugin.Kit.Hosting;

namespace Jellyfin.Plugin.UserRatings.Lifecycle;

/// <summary>
/// User Ratings' daily maintenance task, "User Ratings: Maintenance" at 03:20: the kit's steps in their order. The
/// SQLite database's, <c>PRAGMA quick_check</c> first, then the
/// daily snapshot (<c>SnapshotsToKeep</c>); the library's: the verify sweep of the attached ratings (which
/// detaches those whose item is gone, or keeps them attached while their library folder is unavailable), the
/// re-resolution of the detached ones, and the archive's two steps: the ratings detached longer than
/// <see cref="RatingsLibraryConsumer.DetachedRetentionDays"/> (the setting, 90 days by default; 0 keeps them live) move into
/// the archive, and archived ones whose item is found again come back; the user reconciliation, merged versions and
/// archive purges; the moderation log's retention (<see cref="ModerationLogRetention"/>: the rows older than
/// <c>ModerationLogDays</c>, 365 by default, deleted, then a checkpoint); and last the kit's checkpoint.
/// </summary>
/// <remarks>
/// Jellyfin creates it once through <c>GetExports</c>, so it takes only the service provider and resolves the kit's
/// maintenance when it runs (the kit's <see cref="KitMaintenanceTaskBase"/>, which also makes it return at once on a
/// Jellyfin line this build isn't for).
/// </remarks>
public sealed class RatingsMaintenanceTask : KitMaintenanceTaskBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RatingsMaintenanceTask"/> class.
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    public RatingsMaintenanceTask(IServiceProvider services)
        : base(services, "User Ratings", new TimeSpan(3, 20, 0))
    {
    }
}
