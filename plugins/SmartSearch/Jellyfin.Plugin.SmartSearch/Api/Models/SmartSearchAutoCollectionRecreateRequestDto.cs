namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>The body of <c>POST …/{id}/Recreate</c>; an empty body keeps the name.</summary>
public sealed class SmartSearchAutoCollectionRecreateRequestDto
{
    /// <summary>Gets or sets a new name for the auto-collection and its new collection.</summary>
    public string? Name { get; set; }
}
