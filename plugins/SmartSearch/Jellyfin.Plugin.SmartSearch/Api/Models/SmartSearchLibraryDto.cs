using System;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>A library the signed-in user can open, for the scope bar and the <c>Library</c> rule.</summary>
public sealed class SmartSearchLibraryDto
{
    /// <summary>Gets or sets the library's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the library's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the library's content type (<c>movies</c>, <c>tvshows</c>, …), or <c>null</c> for mixed content.</summary>
    public string? CollectionType { get; set; }
}
