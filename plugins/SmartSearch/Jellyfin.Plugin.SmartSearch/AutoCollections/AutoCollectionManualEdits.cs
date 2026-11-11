using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>The manual adds and removes the definition keeps.</summary>
internal sealed record AutoCollectionManualEdits
{
    /// <summary>The most pins, and the most excludes, of one definition.</summary>
    public const int MaxEntries = 1000;

    /// <summary>Gets the mode.</summary>
    public StoredEnum<SmartSearchManualEditsMode> Mode { get; init; } = SmartSearchManualEditsMode.Respect;

    /// <summary>Gets the items kept in the collection whatever the query says (after the query's own, in this order).</summary>
    public IReadOnlyList<AutoCollectionItemEntry> Pinned { get; init; } = [];

    /// <summary>Gets the items kept out of the collection whatever the query and the pins say.</summary>
    public IReadOnlyList<AutoCollectionItemEntry> Excluded { get; init; } = [];

    /// <summary>Gets fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
