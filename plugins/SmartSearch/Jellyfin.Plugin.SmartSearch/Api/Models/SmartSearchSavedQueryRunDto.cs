namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// The body of <c>POST /SmartSearch/Queries/{id}/Search</c>: the page of the saved query's results, as
/// <see cref="SmartSearchRequestDto"/> without the query. It may be left out for the first page.
/// </summary>
public sealed class SmartSearchSavedQueryRunDto
{
    /// <summary>Gets or sets the index of the page's first result.</summary>
    public int StartIndex { get; set; }

    /// <summary>Gets or sets the page size, 1 to 200.</summary>
    public int Limit { get; set; } = SmartSearchRequestLimits.DefaultLimit;

    /// <summary>Gets or sets the token of an earlier page of the same search, or <c>null</c> for a first page.</summary>
    public string? ResultToken { get; set; }

    /// <summary>Gets or sets a value indicating whether the total is wanted.</summary>
    public bool IncludeTotal { get; set; } = true;
}
