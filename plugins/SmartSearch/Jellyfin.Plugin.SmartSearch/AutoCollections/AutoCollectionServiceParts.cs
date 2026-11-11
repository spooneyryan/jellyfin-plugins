using System;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using Jellyfin.Plugin.SmartSearch.Services;
using Jellyfin.Plugin.SmartSearch.Storage;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>What <see cref="AutoCollectionService"/> works with.</summary>
internal sealed record AutoCollectionServiceParts
{
    /// <summary>Gets the name checker.</summary>
    public required CollectionNameChecker Names { get; init; }

    /// <summary>Gets the definitions.</summary>
    public required AutoCollectionStore Store { get; init; }

    /// <summary>Gets the run state.</summary>
    public required AutoCollectionStateStore State { get; init; }

    /// <summary>Gets the definition checks.</summary>
    public required AutoCollectionChecks Checks { get; init; }

    /// <summary>Gets the check context and settings.</summary>
    public required AutoCollectionCheckContextSource Context { get; init; }

    /// <summary>Gets the membership planner.</summary>
    public required MembershipPlanner Planner { get; init; }

    /// <summary>Gets the mixed-audience check.</summary>
    public required MixedAudienceCheck MixedAudience { get; init; }

    /// <summary>Gets the page hydrator (preview samples).</summary>
    public required PageHydrator Hydrator { get; init; }

    /// <summary>Gets the kit's library access.</summary>
    public required LibraryAccess Access { get; init; }

    /// <summary>Gets the runner (deletes).</summary>
    public required DefinitionRunner Runner { get; init; }

    /// <summary>Gets the queue the runs are asked of.</summary>
    public required IAutoCollectionQueue Queue { get; init; }

    /// <summary>Gets the collection locator.</summary>
    public required CollectionLocator Locator { get; init; }

    /// <summary>Gets the link resolver.</summary>
    public required LinkResolver Links { get; init; }

    /// <summary>Gets the saved queries.</summary>
    public required SavedQueryStore SavedQueries { get; init; }

    /// <summary>Gets the saved-query service (copies of someone else's shared query), resolved when needed.</summary>
    public required Func<ISmartSearchSavedQueries> SavedQueryService { get; init; }

    /// <summary>Gets the recent previews.</summary>
    public required PreviewLedger Ledger { get; init; }

    /// <summary>Gets the clock.</summary>
    public required TimeProvider Time { get; init; }

    /// <summary>Gets the mass-removal guard (what a preview would be held for).</summary>
    public required MassRemovalGuard Guard { get; init; }

    /// <summary>Gets whether a library scan is running.</summary>
    public required Func<bool> ScanRunning { get; init; }

    /// <summary>Gets the most saved queries a user may have (<c>MaxSavedQueriesPerUser</c>), read when a reassignment moves one; no limit by default.</summary>
    public Func<int> MaxSavedQueriesPerUser { get; init; } = () => int.MaxValue;

    /// <summary>Gets the pins and excludes keeper (what a new entry's item is); without, entries get it at the next run.</summary>
    public ManualEditKeeper? Keeper { get; init; }
}
