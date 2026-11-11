using System;
using Jellyfin.Plugin.SmartSearch.Configuration;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// The auto-collections' settings and limits (the settings page's Auto-collections part), read from the plugin
/// configuration on each use.
/// </summary>
internal sealed record AutoCollectionSettings
{
    /// <summary>The default <see cref="MaxMembersPerCollection"/>.</summary>
    public const int DefaultMaxMembersPerCollection = 5_000;

    /// <summary>The default <see cref="MaxAutoCollections"/>.</summary>
    public const int DefaultMaxAutoCollections = 100;

    /// <summary>Gets the most members one collection may have (its links live in one database row and in <c>collection.xml</c>).</summary>
    public int MaxMembersPerCollection { get; init; } = DefaultMaxMembersPerCollection;

    /// <summary>Gets the most auto-collections on the server.</summary>
    public int MaxAutoCollections { get; init; } = DefaultMaxAutoCollections;

    /// <summary>Gets a value indicating whether collections are kept up to date (off: nothing runs or is created).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets how long library changes must be quiet before a batch is dispatched.</summary>
    public TimeSpan EventDebounce { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets the longest a batch of library changes waits.</summary>
    public TimeSpan EventMaxWait { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets the least time between two runs of a definition that library changes start.</summary>
    public TimeSpan DefinitionCooldown { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Gets how long after the server's start the startup pass runs; zero turns it off.</summary>
    public TimeSpan StartupPassDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets a value indicating whether large removals while something looks unhealthy are held for a review.</summary>
    public bool GuardEnabled { get; init; } = true;

    /// <summary>Gets the share of a collection's members, in percent, whose removal counts as large.</summary>
    public int GuardMaxRemovalPercent { get; init; } = 50;

    /// <summary>Gets the fewest removals that count as large.</summary>
    public int GuardMinRemovals { get; init; } = 5;

    /// <summary>Gets a value indicating whether emptying a collection is always held for a review (unless it allows it).</summary>
    public bool GuardBlockEmpty { get; init; } = true;

    /// <summary>Gets the most items a pass keeps loaded to share between definitions.</summary>
    public int PassCacheMaxItems { get; init; } = 15_000;

    /// <summary>Gets how long the daily task waits for a running library scan before it runs anyway.</summary>
    public TimeSpan DailyPassScanWait { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Gets a value indicating whether a change of an evaluation user's watch state runs their auto-collections.</summary>
    public bool UseUserDataTriggers { get; init; } = true;

    /// <summary>Gets a value indicating whether a collection without a poster gets the default one, once (<see cref="AutoCollectionDefaultImage"/>).</summary>
    public bool DefaultImage { get; init; } = true;

    /// <summary>Gets how collections are written (<c>Auto</c> resolved: the direct writer on this server version).</summary>
    public CollectionWriterKind CollectionWriter { get; init; } = CollectionWriterKind.DirectReplace;

    /// <summary>Gets the writer <c>Auto</c> means here: the server's collection calls from Jellyfin 12 on (its links moved), else the direct writer.</summary>
    public static CollectionWriterKind AutoWriter { get; } = typeof(MediaBrowser.Controller.Entities.BaseItem).Assembly.GetName().Version?.Major >= 12
        ? CollectionWriterKind.ManagerApi
        : CollectionWriterKind.DirectReplace;

    /// <summary>Reads the settings from the plugin configuration, each clamped to its range.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The settings.</returns>
    public static AutoCollectionSettings From(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new AutoCollectionSettings
        {
            MaxMembersPerCollection = Math.Clamp(configuration.MaxMembersPerCollection, 1, 20_000),
            MaxAutoCollections = Math.Clamp(configuration.MaxAutoCollections, 1, 1_000),
            Enabled = configuration.EnableAutoCollections,
            EventDebounce = TimeSpan.FromSeconds(Math.Clamp(configuration.EventDebounceSeconds, 5, 3_600)),
            EventMaxWait = TimeSpan.FromMinutes(Math.Clamp(configuration.EventMaxWaitMinutes, 1, 1_440)),
            DefinitionCooldown = TimeSpan.FromSeconds(Math.Clamp(configuration.DefinitionCooldownSeconds, 0, 86_400)),
            StartupPassDelay = TimeSpan.FromMinutes(Math.Clamp(configuration.StartupPassDelayMinutes, 0, 1_440)),
            GuardEnabled = configuration.GuardEnabled,
            GuardMaxRemovalPercent = Math.Clamp(configuration.GuardMaxRemovalPercent, 1, 100),
            GuardMinRemovals = Math.Clamp(configuration.GuardMinRemovals, 1, 100_000),
            GuardBlockEmpty = configuration.GuardBlockEmpty,
            PassCacheMaxItems = Math.Clamp(configuration.PassCacheMaxItems, 0, 200_000),
            DailyPassScanWait = TimeSpan.FromHours(Math.Clamp(configuration.DailyPassScanWaitHours, 0, 48)),
            UseUserDataTriggers = configuration.UseUserDataTriggers,
            DefaultImage = configuration.AutoCollectionDefaultImage,
            CollectionWriter = WriterOf(configuration.CollectionWriter)
        };
    }

    /// <summary>Reads the <c>CollectionWriter</c> setting: <c>DirectReplace</c>, <c>ManagerApi</c>, else <see cref="AutoWriter"/>.</summary>
    /// <param name="value">The setting.</param>
    /// <returns>The writer.</returns>
    public static CollectionWriterKind WriterOf(string? value)
        => string.Equals(value, nameof(CollectionWriterKind.DirectReplace), StringComparison.OrdinalIgnoreCase) ? CollectionWriterKind.DirectReplace
            : string.Equals(value, nameof(CollectionWriterKind.ManagerApi), StringComparison.OrdinalIgnoreCase) ? CollectionWriterKind.ManagerApi
            : AutoWriter;
}
