using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Query.Evaluation;

/// <summary>
/// The <c>People</c> field: the rule's people (references <c>{Id, Name}</c>) in its role (none is any role), each resolved
/// once per compile to the items they are credited on (<see cref="EvaluationEnvironment.PeopleMembers"/>, the
/// <c>PeopleRoleResolver</c>). <c>AnyOf</c>: the item is credited to at least one of them; <c>AllOf</c>: to every one;
/// <c>NoneOf</c> is <c>NOT AnyOf</c> and <c>NotAllOf</c> is <c>NOT AllOf</c>, so both match an item with no credits. A
/// person the server doesn't know is credited on nothing. A reference id listed twice counts once, under its smallest
/// name (ordinal), as the canonical form keeps it (<c>RuleValues.DistinctReferences</c>), so a rule and its canonical
/// form, which share a hash, also share their results.
/// </summary>
internal sealed class PeopleEvaluator : IRowEvaluator
{
    /// <inheritdoc />
    public Func<EvaluationRow, bool> Compile(SmartSearchQueryNode rule, EvaluationEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(environment);
        var op = RuleValues.Op(rule);
        if (op is not (SmartSearchQueryOperator.AnyOf or SmartSearchQueryOperator.AllOf or SmartSearchQueryOperator.NoneOf or SmartSearchQueryOperator.NotAllOf))
        {
            throw RuleValues.Invalid(rule, "has an operator the People field doesn't take");
        }

        var resolve = environment.PeopleMembers ?? throw RuleValues.Invalid(rule, "needs the people resolver, which the environment doesn't have");
        var role = RuleValues.Role(rule);
        Func<Guid, bool>[] credited = [.. RuleValues.DistinctReferences(rule).Select(r => resolve(r.Id, r.Name, role))];
        bool AnyOf(EvaluationRow row) => credited.Any(c => c(row.Item.Id));
        bool AllOf(EvaluationRow row) => credited.All(c => c(row.Item.Id));
        return op switch
        {
            SmartSearchQueryOperator.AnyOf => AnyOf,
            SmartSearchQueryOperator.AllOf => AllOf,
            SmartSearchQueryOperator.NoneOf => row => !AnyOf(row),
            _ => row => !AllOf(row)
        };
    }
}
