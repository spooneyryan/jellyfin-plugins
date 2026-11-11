using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Query.Evaluation;

/// <summary>
/// List fields (genres, tags, studios): <c>AnyOf AllOf NoneOf IsEmpty IsNotEmpty</c>, values
/// compared folded, as core's <c>ItemValues.CleanValue</c> (so the <c>Genres</c>/<c>Tags</c> pushdown is Exact). Blank
/// entries don't count. <c>NoneOf</c> is <c>NOT AnyOf</c>, so it matches an item with no values.
/// </summary>
internal sealed class ListEvaluator : IRowEvaluator
{
    private readonly Func<EvaluationRow, IReadOnlyList<string>?> _select;

    /// <summary>
    /// Initializes a new instance of the <see cref="ListEvaluator"/> class.
    /// </summary>
    /// <param name="select">The item's values, or <c>null</c> for none.</param>
    public ListEvaluator(Func<EvaluationRow, IReadOnlyList<string>?> select)
    {
        _select = select;
    }

    /// <inheritdoc />
    public Func<EvaluationRow, bool> Compile(SmartSearchQueryNode rule, EvaluationEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var select = _select;
        bool HasAny(EvaluationRow row) => select(row) is { } values && values.Any(v => !string.IsNullOrWhiteSpace(v));
        switch (RuleValues.Op(rule))
        {
            case SmartSearchQueryOperator.IsEmpty:
                return row => !HasAny(row);
            case SmartSearchQueryOperator.IsNotEmpty:
                return HasAny;
        }

        var wanted = RuleValues.Strings(rule).Select(RuleValues.Fold).ToFrozenSet(StringComparer.Ordinal);
        HashSet<string> Folded(EvaluationRow row)
            => select(row) is { } values ? values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(RuleValues.Fold).ToHashSet(StringComparer.Ordinal) : [];
        bool AnyOf(EvaluationRow row) => select(row) is { } values && values.Any(v => !string.IsNullOrWhiteSpace(v) && wanted.Contains(RuleValues.Fold(v)));

        return RuleValues.Op(rule) switch
        {
            SmartSearchQueryOperator.AnyOf => AnyOf,
            SmartSearchQueryOperator.NoneOf => row => !AnyOf(row),
            SmartSearchQueryOperator.AllOf => row => wanted.IsSubsetOf(Folded(row)),
            _ => throw RuleValues.Invalid(rule, "has an operator list fields don't take")
        };
    }
}
