using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// A gate the daily maintenance passes before its steps (<c>AddKitMaintenanceGate</c>): the plugin's
/// hold over its own work that mustn't run beside the maintenance (User Ratings: a restore or
/// a data job, which writes the database and then the archive, while the maintenance's Archive step writes both). The hold
/// is disposed when the run ends; a null hold skips the run (Diagnostics <c>Maintenance</c>: <c>LastRun.Skipped</c>); a gate
/// that throws is logged, and the run goes ahead without it.
/// </summary>
internal sealed class KitMaintenanceGate
{
    /// <summary>
    /// Initializes a new instance of the <see cref="KitMaintenanceGate"/> class.
    /// </summary>
    /// <param name="name">Its name for logs and Diagnostics.</param>
    /// <param name="hold">Takes the hold, waiting as long as the plugin chooses; null to skip the run.</param>
    public KitMaintenanceGate(string name, Func<IServiceProvider, CancellationToken, Task<IDisposable?>> hold)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(hold);
        Name = name;
        Hold = hold;
    }

    /// <summary>Gets the gate's name.</summary>
    public string Name { get; }

    /// <summary>Gets what takes the hold.</summary>
    public Func<IServiceProvider, CancellationToken, Task<IDisposable?>> Hold { get; }
}
