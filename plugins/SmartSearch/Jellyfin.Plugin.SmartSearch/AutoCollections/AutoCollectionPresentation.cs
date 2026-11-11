using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>The collection's presentation.</summary>
internal sealed record AutoCollectionPresentation
{
    /// <summary>Gets the sort; an unlocked collection is always sorted by release date (core resets any other at each refresh).</summary>
    public StoredEnum<SmartSearchSortMode> SortMode { get; init; } = SmartSearchSortMode.PremiereDate;

    /// <summary>Gets a value indicating whether the collection's metadata is locked (stable name and sort, no online metadata).</summary>
    public bool Lock { get; init; } = true;

    /// <summary>Gets whether the overview is written.</summary>
    public StoredEnum<SmartSearchOverviewMode> OverviewMode { get; init; } = SmartSearchOverviewMode.LeaveAsIs;

    /// <summary>Gets the overview written in <see cref="SmartSearchOverviewMode.Managed"/> mode.</summary>
    public string? Overview { get; init; }

    /// <summary>Gets fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>Gets the sort the collection really gets: release date when it isn't locked.</summary>
    [JsonIgnore]
    public SmartSearchSortMode EffectiveSortMode => Lock ? SortMode.Value : SmartSearchSortMode.PremiereDate;
}
