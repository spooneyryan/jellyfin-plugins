using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// One per-rule option of a field: a property of the rule, such as <c>IncludeOriginalTitle</c> or
/// <c>Provider</c>.
/// </summary>
public sealed class SmartSearchFieldOptionDto
{
    /// <summary>Gets or sets the option's name, which is the rule's property.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether every rule on the field must set it.</summary>
    public bool Required { get; set; }

    /// <summary>Gets or sets its values, for a text option from a set; <c>null</c> for a true/false option.</summary>
    public IReadOnlyList<string>? Choices { get; set; }
}
