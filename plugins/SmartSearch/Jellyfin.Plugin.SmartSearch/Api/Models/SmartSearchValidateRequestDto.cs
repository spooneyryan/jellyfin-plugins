using System.Text.Json.Serialization;
using Jellyfin.Plugin.SmartSearch.Query;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// The body of <c>POST /SmartSearch/Validate</c>: the query to check, read as strictly as a search's.
/// </summary>
public sealed class SmartSearchValidateRequestDto
{
    /// <summary>Gets or sets the query.</summary>
    public SmartSearchQueryDefinition? Query { get; set; }

    /// <summary>Gets or sets what the reader made of the query (set by the body's reader, never by the client).</summary>
    [JsonIgnore]
    internal QueryReadResult? Read { get; set; }
}
