using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Query.Evaluation;

/// <summary>
/// Choice fields over ids: <c>Series</c> (<c>episode.SeriesId ∈ ids</c>, values are references
/// <c>{Id, Name}</c>), <c>Library</c> (membership from <see cref="EvaluationEnvironment.LibraryMembers"/>, the
/// <c>LibraryMembershipResolver</c>) and <c>ItemType</c>. <c>AnyOf NoneOf</c>; <c>NoneOf</c> is <c>NOT AnyOf</c>.
/// </summary>
internal sealed class IdChoiceEvaluator : IRowEvaluator
{
    private readonly Func<SmartSearchQueryNode, EvaluationEnvironment, Func<EvaluationRow, bool>> _anyOf;

    private IdChoiceEvaluator(Func<SmartSearchQueryNode, EvaluationEnvironment, Func<EvaluationRow, bool>> anyOf)
    {
        _anyOf = anyOf;
    }

    /// <summary>Creates the evaluator of a field whose values are references to items whose id the row carries.</summary>
    /// <param name="select">The id the row carries, <see cref="Guid.Empty"/> for none.</param>
    /// <returns>The evaluator.</returns>
    public static IdChoiceEvaluator OfReferences(Func<EvaluationRow, Guid> select) => new((rule, _) =>
    {
        var ids = RuleValues.ReferenceIds(rule).ToFrozenSet();
        return row => select(row) is var id && !id.Equals(Guid.Empty) && ids.Contains(id);
    });

    /// <summary>Creates the <c>Library</c> evaluator: an item matches when it is a member of one of the libraries.</summary>
    /// <returns>The evaluator.</returns>
    public static IdChoiceEvaluator OfLibraries() => new((rule, environment) =>
    {
        var resolve = environment.LibraryMembers ?? throw RuleValues.Invalid(rule, "needs the library resolver, which the environment doesn't have");
        IReadOnlySet<Guid> members = resolve(RuleValues.Ids(rule));
        return row => members.Contains(row.Item.Id);
    });

    /// <summary>Creates the <c>ItemType</c> evaluator.</summary>
    /// <returns>The evaluator.</returns>
    public static IdChoiceEvaluator OfItemKinds() => new((rule, _) =>
    {
        var kinds = RuleValues.ItemKinds(rule).ToFrozenSet();
        return row => kinds.Contains(row.Item.GetBaseItemKind());
    });

    /// <inheritdoc />
    public Func<EvaluationRow, bool> Compile(SmartSearchQueryNode rule, EvaluationEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(environment);
        var op = RuleValues.Op(rule);
        if (op is not (SmartSearchQueryOperator.AnyOf or SmartSearchQueryOperator.NoneOf))
        {
            throw RuleValues.Invalid(rule, "has an operator choice fields don't take");
        }

        var anyOf = _anyOf(rule, environment);
        return op == SmartSearchQueryOperator.AnyOf ? anyOf : row => !anyOf(row);
    }
}
