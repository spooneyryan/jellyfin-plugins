using System;
using System.Collections.Frozen;
using System.Linq;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartSearch.Query.Evaluation;

/// <summary>
/// Choice fields over one text an item has (<c>SeriesStatus</c>, <c>Resolution</c>,
/// <c>Container</c>, <c>VideoType</c>, <c>OfficialRating</c>): <c>AnyOf NoneOf</c>; <c>NoneOf</c> is <c>NOT AnyOf</c>, so
/// it matches an item with no value. For <c>OfficialRating</c>, <c>Le</c> and <c>Ge</c> compare parental
/// rating scores (<c>GetRatingScore</c>, score then sub-score); an item or rule value with no score never matches them.
/// A comma-separated value (<c>Container</c>: probing stores <c>"mov,mp4,m4a,3gp,3g2,mj2"</c> for an MP4) is a list of
/// tokens, and <c>AnyOf</c> matches when any token is one of the values.
/// </summary>
internal sealed class ChoiceEvaluator : IRowEvaluator
{
    private readonly Func<EvaluationRow, string?> _select;
    private readonly StringComparer _comparer;
    private readonly bool _byRatingScore;
    private readonly bool _commaSeparated;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChoiceEvaluator"/> class.
    /// </summary>
    /// <param name="select">The item's value, or <c>null</c> for none.</param>
    /// <param name="comparer">How values compare (<c>OfficialRating</c> is exact, as its SQL <c>IN</c> is).</param>
    /// <param name="byRatingScore">Whether <c>Le</c> and <c>Ge</c> compare parental rating scores.</param>
    /// <param name="commaSeparated">Whether the item's value is a comma-separated list of tokens.</param>
    public ChoiceEvaluator(Func<EvaluationRow, string?> select, StringComparer comparer, bool byRatingScore = false, bool commaSeparated = false)
    {
        _select = select;
        _comparer = comparer;
        _byRatingScore = byRatingScore;
        _commaSeparated = commaSeparated;
    }

    /// <summary>Splits a comma-separated value into its tokens, trimmed, without empty ones.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The tokens.</returns>
    public static string[] Tokens(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <inheritdoc />
    public Func<EvaluationRow, bool> Compile(SmartSearchQueryNode rule, EvaluationEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(environment);
        var select = _select;
        var op = RuleValues.Op(rule);
        if (_byRatingScore && op is SmartSearchQueryOperator.Le or SmartSearchQueryOperator.Ge)
        {
            var scoreOf = environment.RatingScore;
            if (scoreOf(RuleValues.Text(rule)) is not { } limit)
            {
                return _ => false;
            }

            int? Compare(EvaluationRow row) => select(row) is { Length: > 0 } rating && scoreOf(rating) is { } score ? CompareScores(score, limit) : null;
            return op == SmartSearchQueryOperator.Le ? row => Compare(row) is <= 0 : row => Compare(row) is >= 0;
        }

        var set = RuleValues.Strings(rule).ToFrozenSet(_comparer);
        Func<EvaluationRow, bool> anyOf = _commaSeparated
            ? row => select(row) is { } value && Tokens(value).Any(set.Contains)
            : row => select(row) is { } value && !string.IsNullOrWhiteSpace(value) && set.Contains(value);
        return op switch
        {
            SmartSearchQueryOperator.AnyOf => anyOf,
            SmartSearchQueryOperator.NoneOf => row => !anyOf(row),
            _ => throw RuleValues.Invalid(rule, "has an operator choice fields don't take")
        };
    }

    private static int CompareScores(ParentalRatingScore a, ParentalRatingScore b)
    {
        var byScore = a.Score.CompareTo(b.Score);
        return byScore != 0 ? Math.Sign(byScore) : Math.Sign((a.SubScore ?? 0).CompareTo(b.SubScore ?? 0));
    }
}
