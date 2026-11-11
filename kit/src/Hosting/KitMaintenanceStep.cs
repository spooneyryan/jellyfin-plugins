using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// A maintenance step made of a delegate, for the kit's own steps and small plugin steps.
/// </summary>
internal sealed class KitMaintenanceStep : IKitMaintenanceStep
{
    private readonly Func<CancellationToken, Task<IDictionary<string, object?>?>> _run;

    /// <summary>
    /// Initializes a new instance of the <see cref="KitMaintenanceStep"/> class.
    /// </summary>
    /// <param name="name">The step's name.</param>
    /// <param name="order">Its place (<see cref="KitMaintenanceOrder"/>).</param>
    /// <param name="run">What it does.</param>
    public KitMaintenanceStep(string name, int order, Func<CancellationToken, Task<IDictionary<string, object?>?>> run)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(run);
        Name = name;
        Order = order;
        _run = run;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public int Order { get; }

    /// <inheritdoc />
    public Task<IDictionary<string, object?>?> RunAsync(CancellationToken cancellationToken) => _run(cancellationToken);
}
