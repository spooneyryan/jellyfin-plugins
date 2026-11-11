using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// A query (schema version 1): what to search (<see cref="Scope"/>), the rule tree (<see cref="Where"/>), the
/// order and an optional cap. PascalCase JSON, like the Jellyfin API and the kit's serializer.
/// </summary>
/// <remarks>
/// <c>/Search</c> and <c>/Validate</c> read it strictly (<c>QueryJson</c>): a property it doesn't have, or a name given twice,
/// is refused, and ids are read in any GUID format.
/// </remarks>
public sealed class SmartSearchQueryDefinition
{
    /// <summary>Gets or sets the query schema version (<see cref="Query.Migration.QueryMigrator.CurrentVersion"/>).</summary>
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets what is searched: item kinds, libraries, missing episodes.</summary>
    public SmartSearchQueryScope Scope { get; set; } = new();

    /// <summary>Gets or sets the root of the rule tree, always a group; an empty one matches everything in scope.</summary>
    public SmartSearchQueryNode Where { get; set; } = SmartSearchQueryNode.EmptyAll();

    /// <summary>Gets or sets the sort keys, at most three; none means by title (<c>SortName</c>, ascending).</summary>
    [SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "A query the JSON reader fills and the engine reads.")]
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "A query the JSON reader fills.")]
    public List<SmartSearchQuerySort> Sort { get; set; } = [];

    /// <summary>Gets or sets the most results, or <c>null</c> for all of them.</summary>
    public int? Limit { get; set; }

    /// <summary>Gets or sets the properties this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "A query the JSON reader fills.")]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
