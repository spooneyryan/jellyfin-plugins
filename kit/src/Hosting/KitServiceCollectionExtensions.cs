using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// Registers the kit's shared services.
/// </summary>
internal static class KitServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="KitOptions"/> and <see cref="KitPaths"/>. Call it right after <see cref="KitAbiGuard.Register"/>
    /// returned <c>true</c>, in the plugin's <c>RegisterServices</c>. It only adds descriptors, so it never throws there.
    /// </summary>
    /// <param name="services">The server's service collection.</param>
    /// <param name="options">The plugin's kit options.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKit(this IServiceCollection services, KitOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<KitPaths>();
        return services;
    }
}
