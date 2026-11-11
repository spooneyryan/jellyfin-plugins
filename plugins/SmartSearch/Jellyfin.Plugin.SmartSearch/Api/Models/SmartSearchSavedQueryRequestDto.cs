using System;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.SmartSearch.Query;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// The body that saves a query: <c>POST /SmartSearch/Queries</c> (a new one) and <c>PUT /SmartSearch/Queries/{id}</c> (a
/// change, which also sends <see cref="IfUpdatedAt"/>).
/// </summary>
/// <remarks>
/// Read strictly: a property this body doesn't have (on <c>POST</c>, <see cref="IfUpdatedAt"/> and
/// <see cref="AcknowledgeAutoCollectionWarnings"/> too), or a name given twice, is refused, and the query is read as
/// <c>/Search</c> reads it.
/// </remarks>
public sealed class SmartSearchSavedQueryRequestDto
{
    /// <summary>Gets or sets the name: 1 to 100 characters once trimmed, unique among the owner's queries ignoring case.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the description: plain text of at most 500 characters; none when left out.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets a value indicating whether every user may open and run it (when the server allows sharing).</summary>
    public bool Shared { get; set; }

    /// <summary>Gets or sets the query.</summary>
    public SmartSearchQueryDefinition? Query { get; set; }

    /// <summary>Gets or sets, on a change (required there), the <c>UpdatedUtc</c> the caller last read: a query changed since is refused (409).</summary>
    public DateTime? IfUpdatedAt { get; set; }

    /// <summary>Gets or sets a value indicating whether the caller confirms, on a change, the warnings of the auto-collections that use the query.</summary>
    public bool AcknowledgeAutoCollectionWarnings { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether, on a change, the auto-collections that use the query are detached first:
    /// each keeps a copy of the query as it is stored now, and the change then touches none of them.
    /// </summary>
    public bool DetachAutoCollections { get; set; }

    /// <summary>Gets or sets what the reader made of the query (set by the body's reader, never by the client).</summary>
    [JsonIgnore]
    internal QueryReadResult? Read { get; set; }
}
