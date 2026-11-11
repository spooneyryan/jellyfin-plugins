using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// Registers the kit's daily maintenance and its steps.
/// </summary>
internal static class KitMaintenanceServiceCollectionExtensions
{
    /// <summary>The Diagnostics key of the maintenance.</summary>
    public const string DiagnosticsKey = "Maintenance";

    /// <summary>
    /// Registers <see cref="KitMaintenance"/> (every step added with <c>AddKitMaintenanceStep</c>, in order, each
    /// created on its own: one that can't be created is left out of the run, logged, and tried again at the next run;
    /// logging under <c>Jellyfin.Plugin.&lt;X&gt;.Kit.Maintenance</c>) and its Diagnostics entry <c>Maintenance</c> (with
    /// <c>Unavailable</c>, the steps that can't be created). The plugin declares a public sealed task deriving from
    /// <see cref="KitMaintenanceTaskBase"/> that runs it daily. Call it after <c>AddKit</c> (and <c>AddKitDiagnostics</c>),
    /// only when <c>KitAbiGuard.Register</c> returned <c>true</c>. It only adds descriptors, so it never throws in
    /// <c>RegisterServices</c>.
    /// </summary>
    /// <param name="services">The server's service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKitMaintenance(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(d => d.ServiceType == typeof(KitMaintenance)))
        {
            return services;
        }

        services.AddSingleton(static provider =>
        {
            var options = provider.GetRequiredService<KitOptions>();
            var logger = options.CreateLogger(provider.GetRequiredService<ILoggerFactory>(), KitMaintenance.LoggerComponent);
            var gates = provider.GetServices<KitMaintenanceGate>()
                .Select(g => (g.Name, (Func<CancellationToken, Task<IDisposable?>>)(cancellationToken => g.Hold(provider, cancellationToken))))
                .ToList();
            return new KitMaintenance(() => KitComponents.CreateAll<IKitMaintenanceStep>(provider, logger), logger, options.Clock(provider), gates);
        });
        services.AddKitDiagnosticsSource(DiagnosticsKey, static provider =>
        {
            var described = provider.GetRequiredService<KitMaintenance>().Describe();
            described["Unavailable"] = KitComponents.Problems<IKitMaintenanceStep>(provider);
            return described;
        });
        return services;
    }

    /// <summary>
    /// Adds a maintenance step made of a delegate (the kit's own steps, and small plugin steps), run in its
    /// <paramref name="order"/> (<see cref="KitMaintenanceOrder"/>) by <see cref="AddKitMaintenance"/>'s maintenance. The
    /// delegate gets the service provider and resolves what it needs when it runs.
    /// </summary>
    /// <param name="services">The server's service collection.</param>
    /// <param name="name">The step's name for logs and Diagnostics.</param>
    /// <param name="order">Its place.</param>
    /// <param name="run">What it does; its result is shown in Diagnostics (plain values), or null.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKitMaintenanceStep(
        this IServiceCollection services,
        string name,
        int order,
        Func<IServiceProvider, CancellationToken, Task<IDictionary<string, object?>?>> run)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(run);
        services.AddSingleton(new KitComponent<IKitMaintenanceStep>(name, provider => new KitMaintenanceStep(name, order, cancellationToken => run(provider, cancellationToken))));
        return services;
    }

    /// <summary>
    /// Adds a gate the maintenance passes before its steps (<see cref="KitMaintenanceGate"/>): the delegate
    /// takes the plugin's hold over its own work that mustn't run beside the maintenance,
    /// waiting as long as it chooses, and returns it (disposed when the run ends), or null to skip the run (logged, and
    /// Diagnostics <c>Maintenance</c> shows <c>LastRun.Skipped</c>). A gate that throws is logged as an Error, and the run
    /// goes ahead without it. Gates are passed in the order they were added. Optional: without one the maintenance runs as
    /// before.
    /// </summary>
    /// <param name="services">The server's service collection.</param>
    /// <param name="name">The gate's name for logs and Diagnostics.</param>
    /// <param name="hold">Takes the hold, given the service provider and the run's cancellation.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKitMaintenanceGate(
        this IServiceCollection services,
        string name,
        Func<IServiceProvider, CancellationToken, Task<IDisposable?>> hold)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(hold);
        services.AddSingleton(new KitMaintenanceGate(name, hold));
        return services;
    }

    /// <summary>
    /// Adds a maintenance step of the plugin's own type, which the plugin registers as a singleton itself. The kit creates
    /// each step on its own (<see cref="KitComponent{T}"/>): one whose creation throws costs only itself. Steps registered as
    /// <see cref="IKitMaintenanceStep"/> directly are not run (Repo.Tests <c>KitAdoptionTests</c> refuses them).
    /// </summary>
    /// <typeparam name="TStep">The step's type.</typeparam>
    /// <param name="services">The server's service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKitMaintenanceStep<TStep>(this IServiceCollection services)
        where TStep : class, IKitMaintenanceStep
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new KitComponent<IKitMaintenanceStep>(typeof(TStep).FullName ?? typeof(TStep).Name, static provider => provider.GetRequiredService<TStep>()));
        return services;
    }
}
