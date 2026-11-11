using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>A page of saved queries, ordered by name.</summary>
public sealed class SmartSearchSavedQueryListDto
{
    /// <summary>Gets or sets the page's queries.</summary>
    public IReadOnlyList<SmartSearchSavedQuerySummaryDto> Items { get; set; } = [];

    /// <summary>Gets or sets how many queries the list has in all.</summary>
    public int TotalRecordCount { get; set; }

    /// <summary>Gets or sets the index of the page's first query.</summary>
    public int StartIndex { get; set; }
}
