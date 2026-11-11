using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>An auto-collection's settings, as a request gives them. Every part is optional on a change.</summary>
public sealed class SmartSearchAutoCollectionDefinitionDto
{
    /// <summary>Gets or sets the collection's name: 1 to 100 characters, trimmed.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets a value indicating whether the collection is kept up to date.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Gets or sets what the members follow: one of the creator's saved queries, or a copy of a query.</summary>
    public SmartSearchAutoCollectionSourceDto? Source { get; set; }

    /// <summary>
    /// Gets or sets whose watch state a query that reads watch state uses: the caller (administrators may choose anyone).
    /// <c>null</c> clears it; left out keeps it.
    /// </summary>
    public Guid? EvaluationUserId { get; set; }

    /// <summary>Gets or sets how the collection looks: its sort, lock and overview.</summary>
    public SmartSearchAutoCollectionPresentationDto? Presentation { get; set; }

    /// <summary>Gets or sets the manual changes: pinned and excluded items, and whether they are kept.</summary>
    public SmartSearchAutoCollectionManualEditsDto? ManualEdits { get; set; }

    /// <summary>Gets or sets the safeguards against removing many members at once.</summary>
    public SmartSearchAutoCollectionSafetyDto? Safety { get; set; }

    /// <summary>Gets or sets a value indicating whether the body gave <see cref="EvaluationUserId"/> (even as <c>null</c>).</summary>
    [JsonIgnore]
    internal bool HasEvaluationUserId { get; set; }
}
