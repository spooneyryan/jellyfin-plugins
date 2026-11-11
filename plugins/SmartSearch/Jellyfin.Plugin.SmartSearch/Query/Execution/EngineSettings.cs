using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Configuration;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>
/// The engine's settings, read from the plugin's configuration on each evaluation (<c>MaxConcurrentSearches</c>
/// is read once, at startup, by <see cref="EngineSlots"/>).
/// </summary>
/// <param name="MaxCandidates">The cap on the ids one evaluation loads (the candidate id list and the item sets of
/// <c>Library</c> rules); past it the search is refused with <c>QueryTooBroad</c>.</param>
/// <param name="SearchTimeoutSeconds">The longest an evaluation runs before it is refused with <c>Timeout</c>.</param>
/// <param name="ResultCacheSeconds">The result cache's sliding lifetime.</param>
/// <param name="MaxCachedResultIds">The most ids the result cache holds over all its entries (the least recently used
/// entries go first).</param>
internal sealed record EngineSettings(
    int MaxCandidates = EngineSettings.DefaultMaxCandidates,
    int SearchTimeoutSeconds = EngineSettings.DefaultSearchTimeoutSeconds,
    int ResultCacheSeconds = EngineSettings.DefaultResultCacheSeconds,
    int MaxCachedResultIds = EngineSettings.DefaultMaxCachedResultIds)
{
    /// <summary>The default <c>MaxCandidates</c>.</summary>
    public const int DefaultMaxCandidates = 100_000;

    /// <summary>The default <c>SearchTimeoutSeconds</c>.</summary>
    public const int DefaultSearchTimeoutSeconds = 20;

    /// <summary>The default <c>ResultCacheSeconds</c>.</summary>
    public const int DefaultResultCacheSeconds = 120;

    /// <summary>The shortest <c>ResultCacheSeconds</c>: a shorter lifetime would end a result before its next page.</summary>
    public const int MinResultCacheSeconds = 10;

    /// <summary>The default <c>MaxCachedResultIds</c> (about 32 MB of ids).</summary>
    public const int DefaultMaxCachedResultIds = 2_000_000;

    /// <summary>The smallest <c>MaxCachedResultIds</c>: the largest result one search can have (the most
    /// <c>MaxCandidates</c>), so a result always fits.</summary>
    public const int MinMaxCachedResultIds = 1_000_000;

    /// <summary>The largest <c>MaxCachedResultIds</c> (about 320 MB of ids).</summary>
    public const int MaxMaxCachedResultIds = 20_000_000;

    /// <summary>The default <c>MaxConcurrentSearches</c>.</summary>
    public const int DefaultMaxConcurrentSearches = 2;

    /// <summary>Reads the settings, each kept within its range (the settings page enforces the same ranges).</summary>
    /// <param name="configuration">The plugin's configuration.</param>
    /// <returns>The settings.</returns>
    public static EngineSettings From(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new EngineSettings(
            Math.Clamp(configuration.MaxCandidates, 1_000, 1_000_000),
            Math.Clamp(configuration.SearchTimeoutSeconds, 1, 300),
            Math.Clamp(configuration.ResultCacheSeconds, MinResultCacheSeconds, 3_600),
            Math.Clamp(configuration.MaxCachedResultIds, MinMaxCachedResultIds, MaxMaxCachedResultIds));
    }

    /// <summary>Reads <c>AllowedItemKinds</c>: the searchable kinds it names, in their order; all of them when it names none.</summary>
    /// <param name="configuration">The plugin's configuration.</param>
    /// <returns>The kinds a query may name.</returns>
    public static IReadOnlyList<BaseItemKind> AllowedItemKinds(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var named = configuration.AllowedItemKinds ?? [];
        var allowed = QueryLimits.ItemKindOrder.Where(named.Contains).ToList();
        return allowed.Count == 0 ? QueryLimits.ItemKindOrder : allowed;
    }

    /// <summary>Reads <c>MaxConcurrentSearches</c>, kept within 1 to 16.</summary>
    /// <param name="configuration">The plugin's configuration.</param>
    /// <returns>The number of search slots.</returns>
    public static int SearchSlots(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Math.Clamp(configuration.MaxConcurrentSearches, 1, 16);
    }
}
