using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Validation;

namespace Jellyfin.Plugin.SmartSearch.Query.Evaluation;

/// <summary>
/// Reads a validated rule's values for an evaluator, and folds text the way core's <c>CleanName</c> does.
/// A value of the wrong shape means the rule wasn't validated, which is a programming error.
/// </summary>
internal static class RuleValues
{
    /// <summary>
    /// Folds text for comparison: <c>RemoveDiacritics().ToLowerInvariant()</c>, the same as <c>CleanName</c>
    /// (<c>BaseItemRepository.cs:1391-1399</c>).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The folded text.</returns>
    public static string Fold(string text) => text.RemoveDiacritics().ToLowerInvariant();

    /// <summary>Gets the rule's operator.</summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The operator.</returns>
    public static SmartSearchQueryOperator Op(SmartSearchQueryNode rule) => rule.Op ?? throw Invalid(rule, "has no operator");

    /// <summary>Reads <c>Value</c> as text, trimmed.</summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The text.</returns>
    public static string Text(SmartSearchQueryNode rule) => RawText(rule).Trim();

    /// <summary>Reads <c>Value</c> as text as given (a <c>Matches</c> pattern isn't trimmed).</summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The text.</returns>
    public static string RawText(SmartSearchQueryNode rule)
        => rule.Value is { ValueKind: JsonValueKind.String } value ? value.GetString()! : throw Invalid(rule, "has no text Value");

    /// <summary>Reads <c>Value</c> as a bool.</summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The bool.</returns>
    public static bool Bool(SmartSearchQueryNode rule) => rule.Value switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => throw Invalid(rule, "has no true or false Value")
    };

    /// <summary>Reads a number (<c>Value</c>, <c>From</c> or <c>To</c>).</summary>
    /// <param name="rule">The rule.</param>
    /// <param name="value">The value.</param>
    /// <returns>The number.</returns>
    public static double Number(SmartSearchQueryNode rule, JsonElement? value)
        => value is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out var d) ? d : throw Invalid(rule, "has no number where one is needed");

    /// <summary>Reads a date (<c>Value</c>, <c>From</c> or <c>To</c>): a calendar day, or an instant in UTC.</summary>
    /// <param name="rule">The rule.</param>
    /// <param name="value">The value.</param>
    /// <returns>The date.</returns>
    public static QueryDate Date(SmartSearchQueryNode rule, JsonElement? value)
        => value is { ValueKind: JsonValueKind.String } text && QueryDates.TryParse(text.GetString()!, out var date) ? date : throw Invalid(rule, "has no date where one is needed");

    /// <summary>Reads <c>Values</c> as trimmed strings.</summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The strings.</returns>
    public static IReadOnlyList<string> Strings(SmartSearchQueryNode rule)
        => ValuesOf(rule).Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : throw Invalid(rule, "has a Values entry that isn't text")).ToList();

    /// <summary>Reads <c>Values</c> as numbers.</summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The numbers.</returns>
    public static IReadOnlyList<double> Numbers(SmartSearchQueryNode rule) => ValuesOf(rule).Select(v => Number(rule, v)).ToList();

    /// <summary>Reads <c>Values</c> as ids (GUID strings in any format).</summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The ids.</returns>
    public static IReadOnlyList<Guid> Ids(SmartSearchQueryNode rule)
        => ValuesOf(rule).Select(v => v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var id) ? id : throw Invalid(rule, "has a Values entry that isn't an id")).ToList();

    /// <summary>Reads <c>Values</c> as references <c>{Id, Name}</c> and returns their ids.</summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The ids.</returns>
    public static IReadOnlyList<Guid> ReferenceIds(SmartSearchQueryNode rule) => ValuesOf(rule).Select(v => ReferenceId(rule, v)).ToList();

    /// <summary>Reads <c>Values</c> as item kinds.</summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The kinds.</returns>
    public static IReadOnlyList<BaseItemKind> ItemKinds(SmartSearchQueryNode rule)
        => ValuesOf(rule).Select(v => QueryValidator.ItemKindOf(v) ?? throw Invalid(rule, "has a Values entry that isn't an item kind")).ToList();

    /// <summary>Gets the earliest time an <c>InLast</c>/<c>NotInLast</c> rule's span reaches back to.</summary>
    /// <param name="rule">The rule.</param>
    /// <param name="nowUtc">Now.</param>
    /// <returns>The start of the span.</returns>
    public static DateTime SpanStart(SmartSearchQueryNode rule, DateTime nowUtc)
    {
        var amount = rule.Amount ?? throw Invalid(rule, "has no Amount");
        return rule.Unit switch
        {
            SmartSearchRelativeUnit.Days => nowUtc.AddDays(-amount),
            SmartSearchRelativeUnit.Weeks => nowUtc.AddDays(-7.0 * amount),
            SmartSearchRelativeUnit.Months => nowUtc.AddMonths(-amount),
            SmartSearchRelativeUnit.Years => nowUtc.AddYears(-amount),
            _ => throw Invalid(rule, "has no Unit")
        };
    }

    /// <summary>The error for a rule an evaluator can't read.</summary>
    /// <param name="rule">The rule.</param>
    /// <param name="what">What is wrong.</param>
    /// <returns>The exception.</returns>
    public static InvalidOperationException Invalid(SmartSearchQueryNode rule, string what)
        => new("The " + (rule.Field ?? "?") + " " + (rule.Op?.ToString() ?? "?") + " rule " + what + "; rules are validated before they are evaluated.");

    private static List<JsonElement> ValuesOf(SmartSearchQueryNode rule) => rule.Values ?? throw Invalid(rule, "has no Values");

    private static Guid ReferenceId(SmartSearchQueryNode rule, JsonElement value)
        => QueryReference.Read(value) is { Valid: true } reference ? reference.Id : throw Invalid(rule, "has a Values entry that isn't a reference {Id, Name}");
}
