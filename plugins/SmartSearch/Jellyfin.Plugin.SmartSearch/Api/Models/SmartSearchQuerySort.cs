using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Database.Implementations.Enums;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// One sort key of a query. <see cref="Order"/> is core's
/// <see cref="SortOrder"/>; no type of ours is named <c>SortOrder</c>, which would take the schema id of core's in /api-docs.
/// </summary>
public sealed class SmartSearchQuerySort
{
    /// <summary>Gets or sets the key: an <c>ItemSortBy</c> name or a plugin key.</summary>
    public string Key { get; set; } = "SortName";

    /// <summary>Gets or sets the direction.</summary>
    public SortOrder Order { get; set; }

    /// <summary>Gets or sets the properties this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "A query the JSON reader fills.")]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
