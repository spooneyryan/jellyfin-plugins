using System;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>The body of <c>POST /SmartSearch/AutoCollections/CheckName</c>.</summary>
public sealed class SmartSearchAutoCollectionCheckNameRequestDto
{
    /// <summary>Gets or sets the name to check; it is trimmed.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the auto-collection the name is for, when it exists: its own collection isn't in the way.</summary>
    public Guid? DefinitionId { get; set; }
}
