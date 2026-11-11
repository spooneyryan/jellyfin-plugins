using System;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.UserRatings.Lifecycle;

/// <summary>
/// User Ratings' post-scan task: inside every library scan it flushes the kit's
/// library-change monitor as a full pass, synchronously, to <see cref="RatingsLibraryConsumer"/>, so a movie folder renamed
/// on disk (the old item removed and the new one created in the same scan) detaches and relinks in that one batch. It runs
/// before Jellyfin's own post-scan tasks, whose changes (items whose parent is gone, artists by name, genres, studios) reach
/// the consumer in the batch right after the scan (<c>AfterFullPass</c>), in the same Removed → sweep → Added/Updated order.
/// </summary>
/// <remarks>
/// Jellyfin creates it once at startup and runs the same instance for every scan, so it takes only the service provider and
/// keeps no state (the kit's <see cref="KitPostScanTaskBase"/>, which also makes it return at once on a Jellyfin line this
/// build isn't for).
/// </remarks>
public sealed class RatingsPostScanTask : KitPostScanTaskBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RatingsPostScanTask"/> class.
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    public RatingsPostScanTask(IServiceProvider services)
        : base(services)
    {
    }
}
