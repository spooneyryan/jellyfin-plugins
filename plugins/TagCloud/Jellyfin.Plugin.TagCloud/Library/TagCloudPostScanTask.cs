using System;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.TagCloud.Library;

/// <summary>
/// Tag Cloud's post-scan task: inside every library scan it flushes the kit's library-change monitor
/// as a full pass to the index's consumer, which patches the titles the scan changed or, past
/// <c>FullRebuildAfterChanges</c>, asks for a full rebuild.
/// </summary>
/// <remarks>
/// Jellyfin creates it once at startup and runs the same instance for every scan, so it takes only the service provider and
/// keeps no state (the kit's <see cref="KitPostScanTaskBase"/>, which also makes it return at once on a Jellyfin line this
/// build isn't for).
/// </remarks>
public sealed class TagCloudPostScanTask : KitPostScanTaskBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TagCloudPostScanTask"/> class.
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    public TagCloudPostScanTask(IServiceProvider services)
        : base(services)
    {
    }
}
