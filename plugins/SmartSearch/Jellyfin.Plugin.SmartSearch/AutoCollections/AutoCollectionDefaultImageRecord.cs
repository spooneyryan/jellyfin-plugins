using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// What a definition's state remembers of the default poster (<see cref="AutoCollectionDefaultImage"/>): which collection
/// it was decided for, and how. It is decided once per collection: a poster the plugin gave is never given again, and a
/// collection that already had a poster never gets one, whatever happens to its images later.
/// </summary>
internal sealed record AutoCollectionDefaultImageRecord
{
    /// <summary>The plugin gave the collection its poster.</summary>
    public const string Applied = "Applied";

    /// <summary>The collection already had a poster; the plugin left it alone.</summary>
    public const string HadImage = "HadImage";

    /// <summary>Gets the collection it was decided for.</summary>
    public Guid CollectionId { get; init; }

    /// <summary>Gets how: <see cref="Applied"/> or <see cref="HadImage"/>.</summary>
    public string Outcome { get; init; } = string.Empty;

    /// <summary>Gets when.</summary>
    public DateTime Utc { get; init; }

    /// <summary>Gets fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
