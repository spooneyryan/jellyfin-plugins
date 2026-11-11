using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;
using Jellyfin.Plugin.SmartSearch.Query.Fields;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// Compiles a validated rule tree into one test over rows: an <c>All</c> group holds when every child
/// does (an empty one always holds), an <c>Any</c> group when one does, and <c>Negate</c> inverts a group. Each rule is
/// compiled once by its field's evaluator. <see cref="Compile"/> is the two-valued form (the planner's re-check: no
/// field needs facts); <see cref="CompileTriState"/> the engine's three-valued one.
/// </summary>
internal static class RuleTree
{
    /// <summary>Compiles a node.</summary>
    /// <param name="node">The node.</param>
    /// <param name="registry">The field registry.</param>
    /// <param name="environment">The evaluation environment.</param>
    /// <returns>The test.</returns>
    /// <exception cref="InvalidOperationException">A rule names a field the registry doesn't know (it wasn't validated).</exception>
    public static Func<EvaluationRow, bool> Compile(SmartSearchQueryNode node, FieldRegistry registry, EvaluationEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(environment);
        if (node.IsGroup)
        {
            var children = node.Children!.Select(c => Compile(c, registry, environment)).ToArray();
            Func<EvaluationRow, bool> group = node.Match == SmartSearchMatchMode.Any
                ? row => children.Any(c => c(row))
                : row => children.All(c => c(row));
            return node.Negate ? row => !group(row) : group;
        }

        return registry.EvaluatorOf(FieldOf(node, registry)).Compile(node, environment);
    }

    /// <summary>
    /// Compiles a node into the engine's three-valued test (phase A): a rule on a
    /// <see cref="FieldCost.Facts"/> field is <see cref="TriState.Unknown"/> while the row has no facts; <c>All(false, …)</c>
    /// is false and otherwise unknown when a child is; <c>Any(true, …)</c> is true and otherwise unknown when a child is;
    /// <c>Not(Unknown)</c> is unknown. On rows that have their facts (or a tree without facts fields) it equals
    /// <see cref="Compile"/>.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <param name="registry">The field registry.</param>
    /// <param name="environment">The evaluation environment.</param>
    /// <returns>The test.</returns>
    /// <exception cref="InvalidOperationException">A rule names a field the registry doesn't know (it wasn't validated).</exception>
    public static Func<EvaluationRow, TriState> CompileTriState(SmartSearchQueryNode node, FieldRegistry registry, EvaluationEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(environment);
        if (node.IsGroup)
        {
            var children = node.Children!.Select(c => CompileTriState(c, registry, environment)).ToArray();
            Func<EvaluationRow, TriState> group = node.Match == SmartSearchMatchMode.Any
                ? row => Any(children, row)
                : row => All(children, row);
            return node.Negate ? row => Not(group(row)) : group;
        }

        var field = FieldOf(node, registry);
        var test = registry.EvaluatorOf(field).Compile(node, environment);
        if (field.Cost == FieldCost.Facts)
        {
            return row => row.Facts is null ? TriState.Unknown : test(row) ? TriState.True : TriState.False;
        }

        return row => test(row) ? TriState.True : TriState.False;
    }

    /// <summary>Gets the facts fields a tree's rules use (what phase B must fetch).</summary>
    /// <param name="node">The node.</param>
    /// <param name="catalog">The field catalog.</param>
    /// <returns>The fields' ids, in the order they first appear.</returns>
    public static IReadOnlyList<string> FactsFields(SmartSearchQueryNode node, IFieldCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(catalog);
        var found = new List<string>();
        void Walk(SmartSearchQueryNode n)
        {
            if (n.IsGroup)
            {
                foreach (var child in n.Children!)
                {
                    Walk(child);
                }
            }
            else if (FieldOf(n, catalog) is { Cost: FieldCost.Facts } field && !found.Contains(field.Id))
            {
                found.Add(field.Id);
            }
        }

        Walk(node);
        return found;
    }

    /// <summary>Kleene's negation.</summary>
    /// <param name="value">The value.</param>
    /// <returns>True for false, false for true, unknown for unknown.</returns>
    public static TriState Not(TriState value) => value switch
    {
        TriState.True => TriState.False,
        TriState.False => TriState.True,
        _ => TriState.Unknown
    };

    private static TriState All(Func<EvaluationRow, TriState>[] children, EvaluationRow row)
    {
        var unknown = false;
        foreach (var child in children)
        {
            switch (child(row))
            {
                case TriState.False:
                    return TriState.False;
                case TriState.Unknown:
                    unknown = true;
                    break;
            }
        }

        return unknown ? TriState.Unknown : TriState.True;
    }

    private static TriState Any(Func<EvaluationRow, TriState>[] children, EvaluationRow row)
    {
        var unknown = false;
        foreach (var child in children)
        {
            switch (child(row))
            {
                case TriState.True:
                    return TriState.True;
                case TriState.Unknown:
                    unknown = true;
                    break;
            }
        }

        return unknown ? TriState.Unknown : TriState.False;
    }

    /// <summary>Finds a rule's field (its id or an alias, in any case, as the validator accepted it).</summary>
    /// <param name="rule">The rule.</param>
    /// <param name="catalog">The field catalog.</param>
    /// <returns>The field.</returns>
    /// <exception cref="InvalidOperationException">The field isn't known.</exception>
    public static FieldDescriptor FieldOf(SmartSearchQueryNode rule, IFieldCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(rule.Field?.Trim() ?? string.Empty)
            ?? throw new InvalidOperationException("The rule's field " + (rule.Field ?? "?") + " isn't known; rules are validated before they are planned.");
    }
}
