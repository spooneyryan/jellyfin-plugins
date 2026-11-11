using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>The removal safeguards of one definition.</summary>
internal sealed record AutoCollectionSafety
{
    /// <summary>Gets a value indicating whether a run may empty the collection.</summary>
    public bool AllowEmpty { get; init; }

    /// <summary>Gets the share of members a run may remove while the library looks unhealthy, or <c>null</c> for the server's setting.</summary>
    public int? MaxRemovalPercent { get; init; }

    /// <summary>Gets fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
