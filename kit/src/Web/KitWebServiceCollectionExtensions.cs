using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.Kit.Web;

/// <summary>
/// Registers the kit's web services.
/// </summary>
internal static class KitWebServiceCollectionExtensions
{
    /// <summary>
    /// Registers the plugin's <see cref="IndexHtmlInjectionStartupFilter"/>, once. Call it after
    /// <c>AddKit</c> in <c>RegisterServices</c>, and only when <c>KitAbiGuard.Register</c> returned <c>true</c>: an inert
    /// build adds nothing to <c>index.html</c>. It only adds a descriptor, so it never throws there.
    /// </summary>
    /// <param name="services">The server's service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddIndexHtmlInjection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, IndexHtmlInjectionStartupFilter>());
        return services;
    }

    /// <summary>
    /// Registers the plugin's <see cref="EmbeddedScript"/> as <see cref="IEmbeddedScript"/>, for the
    /// controller's anonymous <c>ClientScript</c> and <c>AdminScript</c> actions. Call it after <c>AddKit</c>, only when
    /// <c>KitAbiGuard.Register</c> returned <c>true</c>. The scripts are built on the first request, so this only adds
    /// descriptors and never throws in <c>RegisterServices</c>.
    /// </summary>
    /// <param name="services">The server's service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddEmbeddedScript(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<EmbeddedScript>();
        services.TryAddSingleton<IEmbeddedScript>(static provider => provider.GetRequiredService<EmbeddedScript>());
        return services;
    }
}
