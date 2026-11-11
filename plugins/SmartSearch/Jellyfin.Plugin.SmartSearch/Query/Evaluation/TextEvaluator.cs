using System;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Validation;

namespace Jellyfin.Plugin.SmartSearch.Query.Evaluation;

/// <summary>How a text field compares.</summary>
internal enum TextComparison
{
    /// <summary>Case- and diacritics-insensitive: both sides folded as <c>CleanName</c> is.</summary>
    Folded,

    /// <summary>Ordinal, ignoring case, nothing folded: paths (<c>OrdinalIgnoreCase</c> on Windows).</summary>
    OrdinalIgnoreCase
}

/// <summary>
/// Text fields: <c>Contains NotContains Eq Ne StartsWith EndsWith Matches IsEmpty
/// IsNotEmpty</c>. A field may read a second text for a rule (<c>Title</c> with <c>IncludeOriginalTitle</c> also reads the
/// original title); a positive operator then matches when either text does.
/// </summary>
/// <remarks>
/// Empty means null, empty or white space. <c>Matches</c> runs the pattern (NonBacktracking, ignoring case, 250 ms
/// timeout) on the text as stored, not folded; a timeout is not swallowed, so the engine can report it.
/// </remarks>
internal sealed class TextEvaluator : IRowEvaluator
{
    private readonly Func<SmartSearchQueryNode, Func<EvaluationRow, string?>> _primary;
    private readonly Func<SmartSearchQueryNode, Func<EvaluationRow, string?>?>? _secondary;
    private readonly TextComparison _comparison;

    /// <summary>
    /// Initializes a new instance of the <see cref="TextEvaluator"/> class.
    /// </summary>
    /// <param name="primary">The text a rule reads, chosen per rule (a <c>ProviderId</c> rule reads its provider's id).</param>
    /// <param name="comparison">How the texts compare.</param>
    /// <param name="secondary">A second text a rule also reads, or <c>null</c> for none.</param>
    public TextEvaluator(Func<SmartSearchQueryNode, Func<EvaluationRow, string?>> primary, TextComparison comparison = TextComparison.Folded, Func<SmartSearchQueryNode, Func<EvaluationRow, string?>?>? secondary = null)
    {
        _primary = primary;
        _secondary = secondary;
        _comparison = comparison;
    }

    /// <summary>Creates an evaluator over one text of the row, the same for every rule.</summary>
    /// <param name="select">The text.</param>
    /// <param name="comparison">How the texts compare.</param>
    /// <returns>The evaluator.</returns>
    public static TextEvaluator Of(Func<EvaluationRow, string?> select, TextComparison comparison = TextComparison.Folded) => new(_ => select, comparison);

    /// <inheritdoc />
    public Func<EvaluationRow, bool> Compile(SmartSearchQueryNode rule, EvaluationEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var op = RuleValues.Op(rule);
        var test = PositiveTest(rule, op);
        var primary = _primary(rule);
        var secondary = _secondary?.Invoke(rule);
        bool Positive(EvaluationRow row) => Check(primary(row), test) || (secondary is not null && Check(secondary(row), test));

        return op switch
        {
            SmartSearchQueryOperator.NotContains or SmartSearchQueryOperator.Ne or SmartSearchQueryOperator.IsEmpty => row => !Positive(row),
            _ => Positive
        };
    }

    private static bool Check(string? text, Func<string, bool> test) => !string.IsNullOrWhiteSpace(text) && test(text);

    // The positive form of the operator, over a non-empty text: NotContains is NOT Contains, Ne is NOT Eq, IsEmpty is
    // NOT IsNotEmpty.
    private Func<string, bool> PositiveTest(SmartSearchQueryNode rule, SmartSearchQueryOperator op)
    {
        if (op is SmartSearchQueryOperator.IsEmpty or SmartSearchQueryOperator.IsNotEmpty)
        {
            return _ => true;
        }

        if (op == SmartSearchQueryOperator.Matches)
        {
            var pattern = QueryValidator.CompilePattern(RuleValues.RawText(rule));
            return pattern.IsMatch;
        }

        var value = RuleValues.Text(rule);
        if (_comparison == TextComparison.Folded)
        {
            var folded = RuleValues.Fold(value);
            return op switch
            {
                SmartSearchQueryOperator.Contains or SmartSearchQueryOperator.NotContains => text => RuleValues.Fold(text).Contains(folded, StringComparison.Ordinal),
                SmartSearchQueryOperator.Eq or SmartSearchQueryOperator.Ne => text => string.Equals(RuleValues.Fold(text), folded, StringComparison.Ordinal),
                SmartSearchQueryOperator.StartsWith => text => RuleValues.Fold(text).StartsWith(folded, StringComparison.Ordinal),
                SmartSearchQueryOperator.EndsWith => text => RuleValues.Fold(text).EndsWith(folded, StringComparison.Ordinal),
                _ => throw RuleValues.Invalid(rule, "has an operator text fields don't take")
            };
        }

        return op switch
        {
            SmartSearchQueryOperator.Contains or SmartSearchQueryOperator.NotContains => text => text.Contains(value, StringComparison.OrdinalIgnoreCase),
            SmartSearchQueryOperator.Eq or SmartSearchQueryOperator.Ne => text => string.Equals(text, value, StringComparison.OrdinalIgnoreCase),
            SmartSearchQueryOperator.StartsWith => text => text.StartsWith(value, StringComparison.OrdinalIgnoreCase),
            SmartSearchQueryOperator.EndsWith => text => text.EndsWith(value, StringComparison.OrdinalIgnoreCase),
            _ => throw RuleValues.Invalid(rule, "has an operator text fields don't take")
        };
    }
}
