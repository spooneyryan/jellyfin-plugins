using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Query.Fields;

/// <summary>
/// What the query layer knows about one field: its id and aliases, value type,
/// kinds, operators, options and value constraints, and the flags the validator and <c>/Fields</c> report. The
/// evaluators and pushdown mappers are the registry's, not this record's.
/// </summary>
internal sealed class FieldDescriptor
{
    /// <summary>Gets the field id, as rules name it.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the label the rule builder shows.</summary>
    public required string Label { get; init; }

    /// <summary>Gets the group the rule builder lists it under.</summary>
    public required string Group { get; init; }

    /// <summary>Gets the value type, which decides what each operator reads.</summary>
    public required FieldValueType Type { get; init; }

    /// <summary>Gets the item kinds the field applies to.</summary>
    public required IReadOnlyList<BaseItemKind> Kinds { get; init; }

    /// <summary>Gets the operators the field takes.</summary>
    public required IReadOnlyList<SmartSearchQueryOperator> Operators { get; init; }

    /// <summary>Gets the per-rule options the field takes.</summary>
    public IReadOnlyList<FieldOptionSpec> Options { get; init; } = [];

    /// <summary>Gets what each entry of <c>Values</c> is (<see cref="FieldElementKind.None"/> when no operator reads it).</summary>
    public FieldElementKind ElementKind { get; init; }

    /// <summary>Gets the fixed values of a choice field (or of its <c>Values</c> entries), in their canonical spelling.</summary>
    public IReadOnlyList<string>? Choices { get; init; }

    /// <summary>Gets the smallest number a rule may name.</summary>
    public double? Min { get; init; }

    /// <summary>Gets the largest number a rule may name.</summary>
    public double? Max { get; init; }

    /// <summary>Gets a value indicating whether numbers must be whole.</summary>
    public bool Integer { get; init; }

    /// <summary>Gets the widest <c>Between</c> a rule may name (<c>To - From</c>), or <c>null</c> for no limit.</summary>
    public double? MaxSpan { get; init; }

    /// <summary>Gets where the rule builder gets suggestions from (a lookup route), or <c>null</c>.</summary>
    public string? ValueSource { get; init; }

    /// <summary>Gets the cost class.</summary>
    public FieldCost Cost { get; init; } = FieldCost.Row;

    /// <summary>Gets a value indicating whether the field reads the user's data.</summary>
    public bool PerUser { get; init; }

    /// <summary>Gets a value indicating whether only administrators may use the field.</summary>
    public bool AdminOnly { get; init; }

    /// <summary>
    /// Gets a value indicating whether the evaluator reads <c>BaseItem.ProviderIds</c>, which core loads only when the
    /// query's <c>DtoOptions</c> ask for <c>ItemFields.ProviderIds</c> (<c>BaseItemRepository</c> includes the
    /// <c>Provider</c> table only then); without it every item has none.
    /// </summary>
    public bool NeedsProviderIds { get; init; }

    /// <summary>Gets a value indicating whether the field can be evaluated on this server now.</summary>
    public bool Available { get; init; } = true;

    /// <summary>Gets a short note the rule builder shows, or <c>null</c>.</summary>
    public string? Note { get; init; }

    /// <summary>Gets the field's earlier ids, still accepted.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];
}
