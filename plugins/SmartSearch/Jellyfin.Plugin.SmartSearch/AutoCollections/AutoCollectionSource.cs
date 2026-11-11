using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>Where a definition's query comes from.</summary>
internal sealed record AutoCollectionSource
{
    /// <summary>Gets the mode.</summary>
    public StoredEnum<SmartSearchAutoCollectionSourceMode> Mode { get; init; } = SmartSearchAutoCollectionSourceMode.Reference;

    /// <summary>Gets the saved query's id (<see cref="SmartSearchAutoCollectionSourceMode.Reference"/>); it must belong to the definition's creator.</summary>
    public Guid? SavedQueryId { get; init; }

    /// <summary>Gets the copied query's JSON (<see cref="SmartSearchAutoCollectionSourceMode.Snapshot"/>), kept as it is like a saved query's.</summary>
    public JsonElement? Snapshot { get; init; }

    /// <summary>Gets when the copy was taken.</summary>
    public DateTime? SnapshotTakenUtc { get; init; }

    /// <summary>Gets fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
