using System;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// Smart Search's post-scan task: flushes the kit's library-change monitor as a full pass inside every scan,
/// which re-runs the clean-form probe (<see cref="CleanFormProbe"/>) and queues the auto-collections' pass (which starts
/// once the scan has ended). Jellyfin creates it once, with its root service provider.
/// </summary>
public sealed class SmartSearchPostScanTask : KitPostScanTaskBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SmartSearchPostScanTask"/> class.
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    public SmartSearchPostScanTask(IServiceProvider services)
        : base(services)
    {
    }
}
