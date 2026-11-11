using System;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Query.Evaluation;

/// <summary>
/// Bool fields: watch state, <c>HasProviderId</c>, <c>Is3D</c>, <c>Locked</c>. The flag a rule reads
/// may depend on the rule (<c>HasProviderId</c> reads its <c>Provider</c>).
/// </summary>
internal sealed class BoolEvaluator : IRowEvaluator
{
    private readonly Func<SmartSearchQueryNode, Func<EvaluationRow, bool>> _select;

    /// <summary>
    /// Initializes a new instance of the <see cref="BoolEvaluator"/> class.
    /// </summary>
    /// <param name="select">The flag a rule reads.</param>
    public BoolEvaluator(Func<SmartSearchQueryNode, Func<EvaluationRow, bool>> select)
    {
        _select = select;
    }

    /// <summary>Creates an evaluator over one flag of the row, the same for every rule.</summary>
    /// <param name="select">The flag.</param>
    /// <returns>The evaluator.</returns>
    public static BoolEvaluator Of(Func<EvaluationRow, bool> select) => new(_ => select);

    /// <inheritdoc />
    public Func<EvaluationRow, bool> Compile(SmartSearchQueryNode rule, EvaluationEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (RuleValues.Op(rule) != SmartSearchQueryOperator.Is)
        {
            throw RuleValues.Invalid(rule, "has an operator bool fields don't take");
        }

        var select = _select(rule);
        return RuleValues.Bool(rule) ? select : row => !select(row);
    }
}
