using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>An auto-collection that uses a saved query, with what a change of the query means for it.</summary>
public sealed class SmartSearchAutoCollectionRefDto
{
    /// <summary>Gets or sets the auto-collection's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets its name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the reasons or warnings, in plain English.</summary>
    public IReadOnlyList<string> Messages { get; set; } = [];
}
