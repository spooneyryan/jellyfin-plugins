using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.TagCloud.Clouds;
using Jellyfin.Plugin.TagCloud.Configuration;
using Jellyfin.Plugin.TagCloud.Index;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TagCloud.Tasks;

/// <summary>
/// Tag Cloud's own steps of the daily maintenance: the snapshot of the saved clouds, and a full rebuild of the
/// index, which catches anything a missed library event left behind.
/// </summary>
internal static class TagCloudMaintenanceSteps
{
    /// <summary>The snapshot step's name.</summary>
    public const string SnapshotsStep = "Snapshots";

    /// <summary>The index step's name.</summary>
    public const string IndexStep = "Index";

    /// <summary>
    /// Adds the steps.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddTagCloudMaintenanceSteps(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddKitMaintenanceStep(SnapshotsStep, KitMaintenanceOrder.Snapshots, SnapshotAsync);
        services.AddKitMaintenanceStep(IndexStep, KitMaintenanceOrder.Plugin, RebuildIndexAsync);
        return services;
    }

    /// <summary>
    /// Writes today's snapshot of <c>clouds.json</c>, keeping <c>SnapshotsToKeep</c>.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="cancellationToken">Stops the step.</param>
    /// <returns>What it did.</returns>
    public static async Task<IDictionary<string, object?>?> SnapshotAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var keep = Math.Max(0, services.GetRequiredService<IPluginConfigurationProvider>().GetConfiguration().SnapshotsToKeep);
        var owner = services.GetRequiredService<SavedCloudOwner>();
        var path = await owner.Store.SnapshotAsync(owner.SnapshotDirectory, keep, cancellationToken).ConfigureAwait(false);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Keep"] = keep,
            ["Snapshot"] = path is null ? null : System.IO.Path.GetFileName(path),
            ["Skipped"] = path is not null ? null : owner.IsReadOnly ? "ReadOnly" : keep == 0 ? "SnapshotsToKeep is 0" : "NotWritten"
        };
    }

    /// <summary>
    /// Rebuilds the index in full.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="cancellationToken">Stops the step; the last snapshot stays.</param>
    /// <returns>What it did.</returns>
    public static async Task<IDictionary<string, object?>?> RebuildIndexAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var index = services.GetRequiredService<TagCloudIndex>();
        await index.BuildAsync(cancellationToken).ConfigureAwait(false);
        var status = index.Describe();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Titles"] = status.Titles,
            ["Milliseconds"] = status.BuildMilliseconds,
            ["PeoplePath"] = status.PeoplePath
        };
    }
}
