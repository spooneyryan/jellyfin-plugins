namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>The body of <c>POST /SmartSearch/Queries/{id}/Duplicate</c>, which may be left out.</summary>
public sealed class SmartSearchSavedQueryDuplicateDto
{
    /// <summary>Gets or sets the copy's name; when left out, the original's, with a number if the caller already uses it.</summary>
    public string? Name { get; set; }
}
