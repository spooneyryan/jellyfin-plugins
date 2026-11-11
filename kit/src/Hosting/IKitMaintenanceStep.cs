using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// One step of a plugin's daily maintenance, run by <see cref="KitMaintenance"/> in
/// <see cref="Order"/>. The kit registers its own (the user reconciliation with <c>AddKitUsers</c>, the verify sweep with
/// <c>AddKitVerifier</c>, the detached records' steps with <c>AddKitDetachedRecords</c>); a plugin adds its own (snapshots,
/// clip-file retries) with <c>AddKitMaintenanceStep</c> (a delegate, or a singleton of its own type; never registered as
/// <see cref="IKitMaintenanceStep"/> directly, which the kit doesn't see), and asks its background-write gate in them.
/// </summary>
internal interface IKitMaintenanceStep
{
    /// <summary>
    /// Gets the step's name for logs and Diagnostics.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the step's place (<see cref="KitMaintenanceOrder"/>).
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Runs the step. An exception is logged and the next step runs.
    /// </summary>
    /// <param name="cancellationToken">Cancelled when the task is cancelled or the server stops.</param>
    /// <returns>What it did, for Diagnostics (plain values), or null.</returns>
    Task<IDictionary<string, object?>?> RunAsync(CancellationToken cancellationToken);
}
