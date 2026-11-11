using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>A pinned or excluded item, with what is needed to find it again after it moved.</summary>
internal sealed record AutoCollectionItemEntry
{
    /// <summary>Gets the item's id.</summary>
    public Guid ItemId { get; init; }

    /// <summary>Gets what the item was when the entry was made.</summary>
    public IdentitySnapshot? Identity { get; init; }

    /// <summary>Gets when the entry was made.</summary>
    public DateTime AddedUtc { get; init; }

    /// <summary>Gets when the item stopped resolving, or <c>null</c> while it does.</summary>
    public DateTime? DetachedUtc { get; init; }

    /// <summary>Gets fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
