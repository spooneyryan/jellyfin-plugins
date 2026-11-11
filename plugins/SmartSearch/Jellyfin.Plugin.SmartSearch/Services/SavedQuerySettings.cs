using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Configuration;
using Jellyfin.Plugin.SmartSearch.Query.Execution;

namespace Jellyfin.Plugin.SmartSearch.Services;

/// <summary>The settings the saved queries read, on each request.</summary>
/// <param name="Enabled">The setting <c>EnableSearchPage</c>.</param>
/// <param name="AllowSharing">The setting <c>AllowQuerySharing</c>.</param>
/// <param name="MaxPerUser">The setting <c>MaxSavedQueriesPerUser</c>, kept in its range.</param>
/// <param name="AllowedKinds">The setting <c>AllowedItemKinds</c>.</param>
internal sealed record SavedQuerySettings(bool Enabled, bool AllowSharing, int MaxPerUser, IReadOnlyCollection<BaseItemKind> AllowedKinds)
{
    /// <summary>The default of <c>MaxSavedQueriesPerUser</c>.</summary>
    public const int DefaultMaxPerUser = 200;

    /// <summary>The largest <c>MaxSavedQueriesPerUser</c>.</summary>
    public const int MaxMaxPerUser = 10_000;

    /// <summary>Reads the settings.</summary>
    /// <param name="configuration">The plugin configuration.</param>
    /// <returns>The settings.</returns>
    public static SavedQuerySettings From(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new SavedQuerySettings(
            configuration.EnableSearchPage,
            configuration.AllowQuerySharing,
            Math.Clamp(configuration.MaxSavedQueriesPerUser, 1, MaxMaxPerUser),
            EngineSettings.AllowedItemKinds(configuration));
    }
}
