using System;
using System.Collections.Frozen;
using System.Linq;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Query.Evaluation;

/// <summary>
/// Number fields: <c>Eq Ne Lt Le Gt Ge Between IsEmpty IsNotEmpty</c>, and <c>AnyOf</c>
/// for <c>Year</c>. <c>Between</c> includes both ends. A missing number (<c>null</c>) is empty: it fails every positive
/// comparison and passes <c>Ne</c>. A field may round the rule's numbers first, as ratings do
/// (<see cref="ItemFacts.SinglePrecision"/>).
/// </summary>
internal sealed class NumberEvaluator : IRowEvaluator
{
    private readonly Func<EvaluationRow, double?> _select;
    private readonly Func<double, double> _ruleNumber;

    /// <summary>
    /// Initializes a new instance of the <see cref="NumberEvaluator"/> class.
    /// </summary>
    /// <param name="select">The number, or <c>null</c> when the item has none.</param>
    /// <param name="ruleNumber">How the rule's numbers are read, or <c>null</c> to take them as they are.</param>
    public NumberEvaluator(Func<EvaluationRow, double?> select, Func<double, double>? ruleNumber = null)
    {
        _select = select;
        _ruleNumber = ruleNumber ?? (v => v);
    }

    /// <inheritdoc />
    public Func<EvaluationRow, bool> Compile(SmartSearchQueryNode rule, EvaluationEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var select = _select;
        var number = _ruleNumber;
        switch (RuleValues.Op(rule))
        {
            case SmartSearchQueryOperator.IsEmpty:
                return row => select(row) is null;
            case SmartSearchQueryOperator.IsNotEmpty:
                return row => select(row) is not null;
            case SmartSearchQueryOperator.Between:
                var from = number(RuleValues.Number(rule, rule.From));
                var to = number(RuleValues.Number(rule, rule.To));
                return row => select(row) is { } v && v >= from && v <= to;
            case SmartSearchQueryOperator.AnyOf:
                var set = RuleValues.Numbers(rule).Select(number).ToFrozenSet();
                return row => select(row) is { } v && set.Contains(v);
        }

        var value = number(RuleValues.Number(rule, rule.Value));
        return RuleValues.Op(rule) switch
        {
            SmartSearchQueryOperator.Eq => row => select(row) is { } v && v == value,
            SmartSearchQueryOperator.Ne => row => !(select(row) is { } v && v == value),
            SmartSearchQueryOperator.Lt => row => select(row) is { } v && v < value,
            SmartSearchQueryOperator.Le => row => select(row) is { } v && v <= value,
            SmartSearchQueryOperator.Gt => row => select(row) is { } v && v > value,
            SmartSearchQueryOperator.Ge => row => select(row) is { } v && v >= value,
            _ => throw RuleValues.Invalid(rule, "has an operator number fields don't take")
        };
    }
}
