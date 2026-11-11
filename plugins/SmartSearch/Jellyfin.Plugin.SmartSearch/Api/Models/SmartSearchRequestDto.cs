using System.Text.Json.Serialization;
using Jellyfin.Plugin.SmartSearch.Query;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// The body of <c>POST /SmartSearch/Search</c>: the query and the page asked for. The first page has no
/// <see cref="ResultToken"/>; later pages send back the one the first returned, with the same query.
/// </summary>
/// <remarks>
/// Read strictly, as a saved query is: a property neither this body nor the query has, or a name given twice, is refused
/// (<c>UnknownProperty</c>, <c>InvalidJson</c>), and the query's errors come back with paths inside the query
/// (<c>Where.Children[0].Value</c>).
/// </remarks>
public sealed class SmartSearchRequestDto
{
    /// <summary>Gets or sets the query.</summary>
    public SmartSearchQueryDefinition? Query { get; set; }

    /// <summary>Gets or sets the index of the page's first result.</summary>
    public int StartIndex { get; set; }

    /// <summary>Gets or sets the page size, 1 to 200.</summary>
    public int Limit { get; set; } = SmartSearchRequestLimits.DefaultLimit;

    /// <summary>Gets or sets the token of an earlier page of the same search, or <c>null</c> for a first page.</summary>
    public string? ResultToken { get; set; }

    /// <summary>Gets or sets a value indicating whether the total is wanted (a later page can leave it out).</summary>
    public bool IncludeTotal { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to push down the scope alone and check every rule in memory (the Residual
    /// path), for administrators checking the pushdown against the database; 403 for anyone else.
    /// </summary>
    public bool ForceResidual { get; set; }

    /// <summary>Gets or sets what the reader made of the query (set by the body's reader, never by the client).</summary>
    [JsonIgnore]
    internal QueryReadResult? Read { get; set; }
}
