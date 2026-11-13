using System;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>A person credited on something the signed-in user can see, for the <c>People</c> rule's picker.</summary>
public sealed class SmartSearchPersonDto
{
    /// <summary>Gets or sets the person's id (their <c>Person</c> item).</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the person's name.</summary>
    public string Name { get; set; } = string.Empty;
}
