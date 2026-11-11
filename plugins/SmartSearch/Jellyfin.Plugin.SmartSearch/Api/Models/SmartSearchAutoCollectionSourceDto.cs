using System;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.SmartSearch.Query;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>What an auto-collection's members follow.</summary>
public sealed class SmartSearchAutoCollectionSourceDto
{
    /// <summary>
    /// Gets or sets the mode: <c>Reference</c> (a saved query of the creator's, followed as it changes) or
    /// <c>Snapshot</c> (a copy kept with the auto-collection). In a request it may be left out: <c>SavedQueryId</c> means
    /// <c>Reference</c>, <c>Query</c> means <c>Snapshot</c>.
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>Gets or sets the referenced saved query.</summary>
    public Guid? SavedQueryId { get; set; }

    /// <summary>
    /// Gets or sets the query: in a request, the copy to follow; in an answer, the query followed, only when the caller
    /// may read it (a saved query: its owner, a shared one, administrators; a copy: the creator and administrators).
    /// </summary>
    public SmartSearchQueryDefinition? Query { get; set; }

    /// <summary>Gets or sets the referenced query's name (answers only; only when the caller may read it).</summary>
    public string? QueryName { get; set; }

    /// <summary>Gets or sets a value indicating whether the caller may read the query (answers only).</summary>
    public bool? QueryVisibleToCaller { get; set; }

    /// <summary>Gets or sets when the copy was taken (answers only).</summary>
    public DateTime? SnapshotTakenUtc { get; set; }

    /// <summary>Gets or sets what the query reader made of <see cref="Query"/>.</summary>
    [JsonIgnore]
    internal QueryReadResult? Read { get; set; }
}
