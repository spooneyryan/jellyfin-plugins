using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// The base of a plugin's post-scan task: inside every library scan, once the library folders are
/// validated, it flushes the plugin's <see cref="LibraryChangeMonitor"/> as a full pass. A plugin declares <c>public sealed
/// class &lt;Plugin&gt;PostScanTask(IServiceProvider services) : KitPostScanTaskBase(services)</c>; the kit has no concrete
/// post-scan task, since Jellyfin creates every public concrete one it finds.
/// </summary>
/// <remarks>
/// <para>Jellyfin creates each <c>ILibraryPostScanTask</c> once at startup through <c>GetExports</c>
/// (<c>v10.11.5:Emby.Server.Implementations/ApplicationHost.cs:679</c>), not as the DI singleton, and runs the same instance
/// for every scan (<c>LibraryManager.cs:1208-1221</c>), inside the scan: <c>IsScanRunning</c> is still true. So the task
/// takes only the <see cref="IServiceProvider"/>, resolves the monitor on every run and keeps no state of its own.</para>
/// <para>It returns at once when the ABI guard made the build inert or the plugin registered no monitor.
/// Otherwise it awaits <see cref="LibraryChangeMonitor.FlushAsync"/> with <c>fullPass</c> and <c>duringPostScan</c> true,
/// which bypasses the scan guard and returns when every consumer is done, reporting progress to the scan's bar. A cancelled
/// scan cancels it; Jellyfin logs anything else it throws and carries on with the next post-scan task.</para>
/// <para><b>It runs before Jellyfin's own post-scan tasks, not at the very end of the scan.</b> Jellyfin 10.11.5 finds the
/// plugins' types first (<c>ApplicationHost.GetComposablePartAssemblies</c>, <c>ApplicationHost.cs:822-826</c>; the order is
/// kept by <c>GetExports</c> and <c>LibraryManager.cs:263</c>) and runs the post-scan tasks in that order, then
/// <c>UpdateInheritedValues()</c> (<c>LibraryManager.cs:1220-1265</c>). So what core's tasks do after the full pass
/// (by-name music artists, genres, music genres and studios created or deleted, box sets filled, items whose parent is gone
/// deleted) reaches the consumers as the next batch, <see cref="LibraryChangeBatch.AfterFullPass"/>, as soon as the scan
/// has ended. (<c>UpdateInheritedValues()</c> only clears stored inherited tags on 10.11.5, which works them out when asked,
/// <c>BaseItemRepository.cs:161-171</c>.)</para>
/// </remarks>
public abstract class KitPostScanTaskBase : ILibraryPostScanTask
{
    private readonly IServiceProvider _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="KitPostScanTaskBase"/> class.
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    protected KitPostScanTaskBase(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!KitAbiGuard.IsActive(_services) || _services.GetService<LibraryChangeMonitor>() is not { } monitor)
        {
            progress?.Report(100);
            return Task.CompletedTask;
        }

        return monitor.FlushAsync(fullPass: true, duringPostScan: true, progress, cancellationToken);
    }
}
