using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.Kit.Diagnostics;

/// <summary>
/// Registers the kit's diagnostics.
/// </summary>
internal static class KitDiagnosticsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ClientHealthRegistry"/> and <see cref="KitDiagnostics"/> as <see cref="IKitDiagnostics"/>, for
    /// the plugin's <c>POST /&lt;Route&gt;/Diagnostics/ClientHealth</c> and <c>GET /&lt;Route&gt;/Admin/Diagnostics</c>.
    /// Call it after <c>AddKit</c>, only when <c>KitAbiGuard.Register</c> returned <c>true</c>. It only adds descriptors,
    /// so it never throws in <c>RegisterServices</c>.
    /// </summary>
    /// <param name="services">The server's service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKitDiagnostics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ClientHealthRegistry>();
        services.TryAddSingleton<KitDiagnostics>();
        services.TryAddSingleton<IKitDiagnostics>(static provider => provider.GetRequiredService<KitDiagnostics>());
        return services;
    }

    /// <summary>
    /// Adds an entry of the plugin's own to the Diagnostics body, for example its store:
    /// <c>services.AddKitDiagnosticsSource("Store", s =&gt; KitDiagnostics.DescribeStore(...))</c>. The delegate runs on
    /// every Diagnostics request.
    /// </summary>
    /// <param name="services">The server's service collection.</param>
    /// <param name="key">The key in the Diagnostics body.</param>
    /// <param name="describe">Returns the value: dictionaries, lists, strings, numbers and dates.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKitDiagnosticsSource(this IServiceCollection services, string key, Func<IServiceProvider, object?> describe)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(describe);

        services.AddSingleton<IKitDiagnosticsSource>(provider => new KitDiagnosticsSource(key, () => describe(provider)));
        return services;
    }
}
