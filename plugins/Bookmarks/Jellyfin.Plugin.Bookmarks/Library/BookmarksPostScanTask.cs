using System;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.Bookmarks.Library;

/// <summary>
/// Bookmarks' post-scan task: inside every library scan it flushes the kit's library-change monitor as a full pass,
/// synchronously, to Bookmarks' consumers (the identity service, and the developer library change demo). It runs before
/// Jellyfin's own post-scan tasks, whose changes (items whose parent is gone, by-name artists, genres, studios, box sets)
/// reach the consumers as the batch right after the scan (<c>AfterFullPass</c>).
/// </summary>
/// <remarks>
/// Jellyfin creates it once at startup and runs the same instance for every scan, so it takes only the service provider and
/// keeps no state (the kit's <see cref="KitPostScanTaskBase"/>, which also makes it return at once on a Jellyfin line this
/// build isn't for).
/// </remarks>
public sealed class BookmarksPostScanTask : KitPostScanTaskBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarksPostScanTask"/> class.
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    public BookmarksPostScanTask(IServiceProvider services)
        : base(services)
    {
    }
}
