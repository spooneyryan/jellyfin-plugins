using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// "Smart Search: Refresh auto-collections": a pass over every enabled auto-collection, daily at 04:00 by default and on
/// demand. It needs the daily run for rules over relative dates, and it is the safety net for changes nothing else
/// caught. The scheduled pass waits for a running library scan to end, at most <c>DailyPassScanWait</c>; the dashboard's
/// Run is a pass by hand, which starts at once (a run that starts during a scan holds large removals).
/// </summary>
/// <remarks>Jellyfin doesn't tell a task what started it: a pass that starts within <see cref="ScheduledWindow"/> after
/// the time of one of its daily or weekly triggers is taken as the scheduled one, any other as the dashboard's.</remarks>
/// <remarks>Jellyfin creates it with its own service provider. Cancelling the task stops waiting for the pass, not the
/// pass itself.</remarks>
public sealed class RefreshAutoCollectionsTask : IScheduledTask
{
    /// <summary>How long after a trigger's time a start counts as the trigger's.</summary>
    public static readonly TimeSpan ScheduledWindow = TimeSpan.FromMinutes(10);

    private readonly IServiceProvider _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshAutoCollectionsTask"/> class.
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    public RefreshAutoCollectionsTask(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <inheritdoc />
    public string Name => "Smart Search: Refresh auto-collections";

    /// <inheritdoc />
    public string Key => "SmartSearchRefreshAutoCollections";

    /// <inheritdoc />
    public string Description => "Brings every auto-collection in line with its search. On schedule it waits for a running library scan first; Run starts at once.";

    /// <inheritdoc />
    public string Category => "Smart Search";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!KitAbiGuard.IsActive(_services)
            || _services.GetService<IAutoCollectionQueue>() is not { } queue
            || _services.GetService<AutoCollectionCheckContextSource>() is not { } context
            || !context.Settings.Enabled)
        {
            progress?.Report(100);
            return;
        }

        var triggers = _services.GetService<ITaskManager>()?.ScheduledTasks.FirstOrDefault(w => string.Equals(w.ScheduledTask?.Key, Key, StringComparison.Ordinal))?.Triggers ?? [];
        var reason = IsScheduled(triggers, DateTime.Now) ? SmartSearchAutoCollectionRunReason.Daily : SmartSearchAutoCollectionRunReason.Manual;
        await queue.RequestPassAsync(reason, progress).WaitAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(100);
    }

    /// <summary>Tells whether a start is a daily or weekly trigger's: within <see cref="ScheduledWindow"/> after its time.</summary>
    /// <param name="triggers">The task's triggers.</param>
    /// <param name="localNow">The server's local time (the triggers' clock).</param>
    /// <returns>Whether it is.</returns>
    internal static bool IsScheduled(IEnumerable<TaskTriggerInfo> triggers, DateTime localNow)
        => triggers.Any(t =>
            t.TimeOfDayTicks is { } ticks
            && (t.Type == TaskTriggerInfoType.DailyTrigger || (t.Type == TaskTriggerInfoType.WeeklyTrigger && t.DayOfWeek == localNow.DayOfWeek))
            && TimeSpan.FromTicks((localNow.TimeOfDay.Ticks - ticks + TimeSpan.TicksPerDay) % TimeSpan.TicksPerDay) <= ScheduledWindow);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => [new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = new TimeSpan(4, 0, 0).Ticks }];
}
