using System;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Query.Evaluation;

/// <summary>
/// A field's in-memory semantics: compiles one validated
/// rule into a test over rows, once per query, so the rule's values are read once and not per candidate.
/// </summary>
/// <remarks>
/// Every evaluator follows the same null semantics: a positive comparison on an empty value is false, and every negative
/// operator is exactly <c>NOT(positive)</c>, so it matches empty values. A group's <c>Negate</c> and the three-valued
/// logic are the engine's, not the evaluator's.
/// </remarks>
internal interface IRowEvaluator
{
    /// <summary>Compiles a rule the validator accepted.</summary>
    /// <param name="rule">The rule.</param>
    /// <param name="environment">The evaluation environment.</param>
    /// <returns>The test.</returns>
    /// <exception cref="InvalidOperationException">The rule isn't valid for the field (it wasn't validated).</exception>
    Func<EvaluationRow, bool> Compile(SmartSearchQueryNode rule, EvaluationEnvironment environment);
}
