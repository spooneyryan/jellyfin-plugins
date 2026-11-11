using System.Collections.Generic;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// One field of <see cref="SmartSearchFieldCatalogDto"/>: what a rule on it may say and how the rule
/// builder edits its values.
/// </summary>
public sealed class SmartSearchFieldDto
{
    /// <summary>Gets or sets the field id, as rules name it.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the group the rule builder lists it under.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the value type: <c>Text</c>, <c>Number</c>, <c>Date</c>, <c>List</c>, <c>Choice</c> or <c>Bool</c>.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the item kinds the field applies to.</summary>
    public IReadOnlyList<BaseItemKind> Kinds { get; set; } = [];

    /// <summary>Gets or sets the operators (<c>Contains</c>, <c>AnyOf</c>, <c>Is</c>, …).</summary>
    public IReadOnlyList<string> Operators { get; set; } = [];

    /// <summary>Gets or sets the per-rule options (<c>IncludeOriginalTitle</c>, <c>Provider</c>).</summary>
    public IReadOnlyList<SmartSearchFieldOptionDto> Options { get; set; } = [];

    /// <summary>
    /// Gets or sets what each entry of a rule's <c>Values</c> is: <c>None</c>, <c>String</c>, <c>Integer</c>, <c>Guid</c>,
    /// <c>Reference</c> (<c>{Id, Name}</c>) or <c>ItemKind</c>.
    /// </summary>
    public string ElementKind { get; set; } = string.Empty;

    /// <summary>Gets or sets the fixed values of a choice field, or <c>null</c> when they come from <see cref="ValueSource"/> or are free.</summary>
    public IReadOnlyList<string>? Choices { get; set; }

    /// <summary>Gets or sets the smallest number a rule may name, or <c>null</c>.</summary>
    public double? Min { get; set; }

    /// <summary>Gets or sets the largest number a rule may name, or <c>null</c>.</summary>
    public double? Max { get; set; }

    /// <summary>Gets or sets a value indicating whether numbers must be whole.</summary>
    public bool Integer { get; set; }

    /// <summary>Gets or sets the widest <c>Between</c> a rule may name (<c>To - From</c>), or <c>null</c>.</summary>
    public double? MaxSpan { get; set; }

    /// <summary>Gets or sets the lookup route suggestions come from, or <c>null</c>.</summary>
    public string? ValueSource { get; set; }

    /// <summary>Gets or sets the cost class: <c>Sql</c>, <c>Row</c>, <c>Set</c>, <c>Facts</c> or <c>User</c>.</summary>
    public string Cost { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the field reads the user's data (watch state).</summary>
    public bool PerUser { get; set; }

    /// <summary>Gets or sets a value indicating whether only administrators may use the field.</summary>
    public bool AdminOnly { get; set; }

    /// <summary>Gets or sets a value indicating whether the field can be evaluated on this server now.</summary>
    public bool Available { get; set; }

    /// <summary>Gets or sets a short note for the rule builder, or <c>null</c>.</summary>
    public string? Note { get; set; }
}
