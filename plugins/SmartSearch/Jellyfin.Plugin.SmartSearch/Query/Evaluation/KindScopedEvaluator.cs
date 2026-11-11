using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Query.Evaluation;

/// <summary>
/// Makes a rule false on every row whose kind its field doesn't apply to: <c>SeriesStatus</c>
/// on a movie, <c>Played</c> on a series. Negative operators are no exception (<c>SeriesStatus NoneOf [Ended]</c> is
/// false on a movie); only a group's <c>Negate</c> turns it true. The validator only refuses a rule whose field applies
/// to none of the scope's kinds, so a query over several kinds needs this per row.
/// </summary>
internal sealed class KindScopedEvaluator : IRowEvaluator
{
    private readonly FrozenSet<BaseItemKind> _kinds;
    private readonly IRowEvaluator _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="KindScopedEvaluator"/> class.
    /// </summary>
    /// <param name="kinds">The kinds the field applies to.</param>
    /// <param name="inner">The field's evaluator.</param>
    public KindScopedEvaluator(IEnumerable<BaseItemKind> kinds, IRowEvaluator inner)
    {
        _kinds = kinds.ToFrozenSet();
        _inner = inner;
    }

    /// <inheritdoc />
    public Func<EvaluationRow, bool> Compile(SmartSearchQueryNode rule, EvaluationEnvironment environment)
    {
        var test = _inner.Compile(rule, environment);
        var kinds = _kinds;
        return row => kinds.Contains(row.Item.GetBaseItemKind()) && test(row);
    }
}
