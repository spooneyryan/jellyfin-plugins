using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// An item someone added to or removed from an auto-collection outside the plugin (the server's own collection editor,
/// another plugin, the server's automatic adds after a scan), kept until the definition's next run sorts it into a pin,
/// an exclude or nothing.
/// </summary>
internal sealed record AutoCollectionManualEdit
{
    /// <summary>Gets the item's id.</summary>
    public Guid ItemId { get; init; }

    /// <summary>Gets a value indicating whether the item was added (else removed).</summary>
    public bool Added { get; init; }

    /// <summary>Gets when the edit was seen.</summary>
    public DateTime Utc { get; init; }

    /// <summary>Gets fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
