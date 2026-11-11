using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Kit.Web.Pages;

/// <summary>
/// Registers the kit's page hosting.
/// </summary>
internal static class KitPagesServiceCollectionExtensions
{
    /// <summary>
    /// Registers the page shells (<see cref="IKitPages"/>), the host registry (<see cref="KitPageHosts"/>) and
    /// <see cref="PluginPagesRegistrationService"/>, which registers the enabled pages of <c>KitOptions.Pages</c> with
    /// Plugin Pages at startup in <c>Auto</c>/<c>PluginPagesOnly</c> mode. Call it after <c>AddKit</c>, only when
    /// <c>KitAbiGuard.Register</c> returned <c>true</c>. It only adds descriptors, so it never throws in
    /// <c>RegisterServices</c>, and it never writes Plugin Pages' <c>config.json</c>.
    /// </summary>
    /// <param name="services">The server's service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKitPages(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<KitPages>();
        services.TryAddSingleton<IKitPages>(static provider => provider.GetRequiredService<KitPages>());
        services.TryAddSingleton<KitPageHosts>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PluginPagesRegistrationService>());
        return services;
    }
}
