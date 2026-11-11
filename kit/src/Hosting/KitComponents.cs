using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// Creates the registered <see cref="KitComponent{T}"/>s of an extension point.
/// </summary>
internal static class KitComponents
{
    /// <summary>
    /// Creates every registered component of an extension point that can be created, each on its own, in the order they
    /// were registered; one that can't be created is logged and left out (<see cref="KitComponent{T}.TryCreate"/>).
    /// </summary>
    /// <typeparam name="T">The extension point.</typeparam>
    /// <param name="provider">The service provider.</param>
    /// <param name="logger">The logger of the kit service that asks.</param>
    /// <returns>The components.</returns>
    public static IReadOnlyList<T> CreateAll<T>(IServiceProvider provider, ILogger logger)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(logger);
        return provider.GetServices<KitComponent<T>>().Select(c => c.TryCreate(provider, logger)).OfType<T>().ToList();
    }

    /// <summary>
    /// Lists the registered components of an extension point that can't be created now, with why, for Diagnostics.
    /// </summary>
    /// <typeparam name="T">The extension point.</typeparam>
    /// <param name="provider">The service provider.</param>
    /// <returns>Each failing component's name and problem.</returns>
    public static IReadOnlyList<IDictionary<string, object?>> Problems<T>(IServiceProvider provider)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.GetServices<KitComponent<T>>()
            .Select(c => (c.Name, c.Problem))
            .Where(c => c.Problem is not null)
            .Select(c => (IDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal) { ["Name"] = c.Name, ["Problem"] = c.Problem })
            .ToList();
    }
}
