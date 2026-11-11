using System;
using System.Linq;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// Registers the kit's Jellyfin Enhanced interop.
/// </summary>
internal static class KitJellyfinEnhancedServiceCollectionExtensions
{
    /// <summary>The key of the Diagnostics entry.</summary>
    public const string DiagnosticsKey = "JellyfinEnhanced";

    /// <summary>
    /// Registers <see cref="JellyfinEnhancedReader"/>, <see cref="ShortcutPolicy"/> and the Diagnostics entry
    /// <c>JellyfinEnhanced</c> (<see cref="JellyfinEnhancedDiagnostics"/>), for every plugin with a shortcut setting or a
    /// JE feature to replace (<c>KitOptions.JellyfinEnhanced</c>). Call it after <c>AddKit</c> and <c>AddKitDiagnostics</c>,
    /// only when <c>KitAbiGuard.Register</c> returned <c>true</c>. It only adds descriptors, so it never throws in
    /// <c>RegisterServices</c>.
    /// </summary>
    /// <param name="services">The server's service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKitJellyfinEnhanced(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<KitOptions>();
            return new JellyfinEnhancedReader(
                provider.GetRequiredService<IApplicationPaths>(),
                provider.GetService<IPluginManager>(),
                provider,
                options.CreateLogger(provider.GetRequiredService<ILoggerFactory>(), "JellyfinEnhanced"),
                options.Clock(provider));
        });
        services.TryAddSingleton<ShortcutPolicy>();
        if (!services.Any(d => d.ServiceType == typeof(JellyfinEnhancedDiagnostics)))
        {
            services.AddSingleton<JellyfinEnhancedDiagnostics>();
            services.AddKitDiagnosticsSource(DiagnosticsKey, provider => provider.GetRequiredService<JellyfinEnhancedDiagnostics>().Describe());
        }

        return services;
    }
}
