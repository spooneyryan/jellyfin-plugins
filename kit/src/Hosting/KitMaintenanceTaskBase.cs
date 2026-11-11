using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// The base of a plugin's daily maintenance task, <c>&lt;Plugin&gt;: Maintenance</c>: it runs the
/// plugin's <see cref="KitMaintenance"/> (snapshots, the retention archive step, user reconciliation, the verify sweep of
/// attached records, re-resolution of detached records, and the plugin's own steps). A plugin declares <c>public sealed
/// class &lt;Plugin&gt;MaintenanceTask(IServiceProvider services) : KitMaintenanceTaskBase(services, "&lt;Plugin&gt;",
/// &lt;time&gt;)</c>; the kit has no concrete task, since Jellyfin creates every public concrete one it finds.
/// </summary>
/// <remarks>
/// <para>Jellyfin creates scheduled tasks through <c>GetExports</c> with the service provider, so the task takes only the
/// <see cref="IServiceProvider"/> and resolves the maintenance when it runs. The default trigger is daily at the plugin's
/// staggered time (03:10 Bookmarks, 03:20 User Ratings, 03:30 Smart Search, 03:40 Library Maintenance, 03:50
/// Access Roles); an admin can change it on the Scheduled Tasks page or start it there.</para>
/// <para>It returns at once when the ABI guard made the build inert or the plugin registered no
/// maintenance.</para>
/// </remarks>
public abstract class KitMaintenanceTaskBase : IScheduledTask
{
    private readonly IServiceProvider _services;
    private readonly string _pluginName;
    private readonly TimeSpan _dailyAt;

    /// <summary>
    /// Initializes a new instance of the <see cref="KitMaintenanceTaskBase"/> class.
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    /// <param name="pluginName">The plugin's name (<c>build.yaml</c>'s), for the task's name, key and category.</param>
    /// <param name="dailyAt">The time of day of the default trigger (the server's local time).</param>
    protected KitMaintenanceTaskBase(IServiceProvider services, string pluginName, TimeSpan dailyAt)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        if (dailyAt < TimeSpan.Zero || dailyAt >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(dailyAt), "The time of day must be within one day.");
        }

        _services = services;
        _pluginName = pluginName;
        _dailyAt = dailyAt;
    }

    /// <inheritdoc />
    public string Name => _pluginName + ": Maintenance";

    /// <inheritdoc />
    public string Key => _pluginName.Replace(" ", string.Empty, StringComparison.Ordinal) + "Maintenance";

    /// <inheritdoc />
    public string Description => "Daily upkeep of the plugin's data: snapshots, records whose library item is gone (detached, archived, reattached), users no longer on the server.";

    /// <inheritdoc />
    public string Category => _pluginName;

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!KitAbiGuard.IsActive(_services) || _services.GetService<KitMaintenance>() is not { } maintenance)
        {
            progress?.Report(100);
            return;
        }

        await maintenance.RunAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => [new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = _dailyAt.Ticks }];
}
