using System.Collections.Generic;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Fields;

namespace Jellyfin.Plugin.SmartSearch.Query;

/// <summary>
/// What each operator reads from a rule, and the operators of each value type.
/// </summary>
internal static class QueryOperators
{
    /// <summary>
    /// Gets the operators that are built but not offered yet: <c>Matches</c> waits for a later version, which adds a query-level
    /// cancellation (the 250 ms timeout is per row, so a pattern over many rows could run long). <c>GET /Fields</c>
    /// leaves them out, and the validator refuses them while <c>EnableRegex</c> is off, which it is by default.
    /// </summary>
    public static IReadOnlySet<SmartSearchQueryOperator> Dormant { get; } = new HashSet<SmartSearchQueryOperator> { SmartSearchQueryOperator.Matches };

    /// <summary>Gets the text operators.</summary>
    public static IReadOnlyList<SmartSearchQueryOperator> Text { get; } =
    [
        SmartSearchQueryOperator.Contains, SmartSearchQueryOperator.NotContains, SmartSearchQueryOperator.Eq, SmartSearchQueryOperator.Ne,
        SmartSearchQueryOperator.StartsWith, SmartSearchQueryOperator.EndsWith, SmartSearchQueryOperator.Matches,
        SmartSearchQueryOperator.IsEmpty, SmartSearchQueryOperator.IsNotEmpty
    ];

    /// <summary>Gets the number operators.</summary>
    public static IReadOnlyList<SmartSearchQueryOperator> Number { get; } =
    [
        SmartSearchQueryOperator.Eq, SmartSearchQueryOperator.Ne, SmartSearchQueryOperator.Lt, SmartSearchQueryOperator.Le,
        SmartSearchQueryOperator.Gt, SmartSearchQueryOperator.Ge, SmartSearchQueryOperator.Between,
        SmartSearchQueryOperator.IsEmpty, SmartSearchQueryOperator.IsNotEmpty
    ];

    /// <summary>Gets the date operators.</summary>
    public static IReadOnlyList<SmartSearchQueryOperator> Date { get; } =
    [
        SmartSearchQueryOperator.Before, SmartSearchQueryOperator.After, SmartSearchQueryOperator.Between,
        SmartSearchQueryOperator.InLast, SmartSearchQueryOperator.NotInLast,
        SmartSearchQueryOperator.IsEmpty, SmartSearchQueryOperator.IsNotEmpty
    ];

    /// <summary>Gets the list operators.</summary>
    public static IReadOnlyList<SmartSearchQueryOperator> List { get; } =
    [
        SmartSearchQueryOperator.AnyOf, SmartSearchQueryOperator.AllOf, SmartSearchQueryOperator.NoneOf,
        SmartSearchQueryOperator.IsEmpty, SmartSearchQueryOperator.IsNotEmpty
    ];

    /// <summary>Gets the choice operators.</summary>
    public static IReadOnlyList<SmartSearchQueryOperator> Choice { get; } = [SmartSearchQueryOperator.AnyOf, SmartSearchQueryOperator.NoneOf];

    /// <summary>Gets the bool operators.</summary>
    public static IReadOnlyList<SmartSearchQueryOperator> Bool { get; } = [SmartSearchQueryOperator.Is];

    /// <summary>Gets what an operator reads from a rule.</summary>
    /// <param name="op">The operator.</param>
    /// <returns>Its shape.</returns>
    public static OperatorShape ShapeOf(SmartSearchQueryOperator op) => op switch
    {
        SmartSearchQueryOperator.IsEmpty or SmartSearchQueryOperator.IsNotEmpty => OperatorShape.None,
        SmartSearchQueryOperator.Between => OperatorShape.Range,
        SmartSearchQueryOperator.InLast or SmartSearchQueryOperator.NotInLast => OperatorShape.Relative,
        SmartSearchQueryOperator.AnyOf or SmartSearchQueryOperator.AllOf or SmartSearchQueryOperator.NoneOf => OperatorShape.Values,
        _ => OperatorShape.Value
    };

    /// <summary>Gets the default operators of a value type.</summary>
    /// <param name="type">The value type.</param>
    /// <returns>The operators.</returns>
    public static IReadOnlyList<SmartSearchQueryOperator> For(FieldValueType type) => type switch
    {
        FieldValueType.Text => Text,
        FieldValueType.Number => Number,
        FieldValueType.Date => Date,
        FieldValueType.List => List,
        FieldValueType.Choice => Choice,
        _ => Bool
    };
}
