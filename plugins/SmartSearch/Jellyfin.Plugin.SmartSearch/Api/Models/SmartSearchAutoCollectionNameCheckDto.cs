using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>Whether a new collection can take a name.</summary>
public sealed class SmartSearchAutoCollectionNameCheckDto
{
    /// <summary>Gets or sets the name as checked (trimmed).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the name is free (or already the auto-collection's own).</summary>
    public bool Available { get; set; }

    /// <summary>Gets or sets what is in the way; empty when the name is free.</summary>
    public IReadOnlyList<SmartSearchAutoCollectionNameConflictDto> Conflicts { get; set; } = [];

    /// <summary>Gets or sets what would go wrong with the name even though it is free, in plain English.</summary>
    public IReadOnlyList<string> Warnings { get; set; } = [];

    /// <summary>Gets or sets free names close to this one, when it isn't free.</summary>
    public IReadOnlyList<string> Suggestions { get; set; } = [];
}
