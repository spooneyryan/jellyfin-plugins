using System;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Query.Evaluation;

/// <summary>
/// Date fields: <c>Before After Between InLast NotInLast IsEmpty IsNotEmpty</c>. A date-only
/// value is a whole calendar day: <c>Before d</c> is before that day, <c>After d</c> from the next day on, and
/// <c>Between a b</c> from the start of <c>a</c> to the end of <c>b</c>; the item's day is the server's local day of an
/// instant, or the day a calendar date names (<see cref="ItemFacts.CalendarDay"/>). A value with a time and zone is an
/// exact instant, compared with the item's instant: <c>Before</c> and <c>After</c> exclude it, <c>Between</c> includes
/// both ends. <c>InLast</c> is the span of instants from <c>now - Amount Unit</c> to now, both included; months and
/// years are calendar months and years. <c>NotInLast</c> is <c>NOT InLast</c>, so it matches items with no date (never
/// played) and dates after now.
/// </summary>
internal sealed class DateEvaluator : IRowEvaluator
{
    private readonly Func<EvaluationRow, DateTime?> _select;
    private readonly bool _calendarDate;

    /// <summary>
    /// Initializes a new instance of the <see cref="DateEvaluator"/> class.
    /// </summary>
    /// <param name="select">The date, or <c>null</c> when the item has none; any kind (see <see cref="ItemFacts.Utc"/>).</param>
    /// <param name="calendarDate">Whether the field is a calendar date (<c>PremiereDate</c>) rather than an instant.</param>
    public DateEvaluator(Func<EvaluationRow, DateTime?> select, bool calendarDate = false)
    {
        _select = select;
        _calendarDate = calendarDate;
    }

    /// <inheritdoc />
    public Func<EvaluationRow, bool> Compile(SmartSearchQueryNode rule, EvaluationEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(environment);
        var raw = _select;
        var zone = environment.TimeZone;
        Func<EvaluationRow, DateTime?> select = row => ItemFacts.Utc(raw(row), zone);
        Func<DateTime, DateOnly> dayOf = _calendarDate ? utc => ItemFacts.CalendarDay(utc, zone) : utc => ItemFacts.LocalDay(utc, zone);

        // The sign of the item's date compared with a bound: by day for a day, by instant for an instant.
        int Compare(DateTime utc, QueryDate bound) => bound.IsDay ? dayOf(utc).CompareTo(bound.Day) : utc.CompareTo(bound.Utc);

        switch (RuleValues.Op(rule))
        {
            case SmartSearchQueryOperator.IsEmpty:
                return row => select(row) is null;
            case SmartSearchQueryOperator.IsNotEmpty:
                return row => select(row) is not null;
            case SmartSearchQueryOperator.Before:
                var before = RuleValues.Date(rule, rule.Value);
                return row => select(row) is { } d && Compare(d, before) < 0;
            case SmartSearchQueryOperator.After:
                var after = RuleValues.Date(rule, rule.Value);
                return row => select(row) is { } d && Compare(d, after) > 0;
            case SmartSearchQueryOperator.Between:
                var from = RuleValues.Date(rule, rule.From);
                var to = RuleValues.Date(rule, rule.To);
                return row => select(row) is { } d && Compare(d, from) >= 0 && Compare(d, to) <= 0;
            case SmartSearchQueryOperator.InLast or SmartSearchQueryOperator.NotInLast:
                var now = DateTime.SpecifyKind(environment.NowUtc, DateTimeKind.Utc);
                var start = RuleValues.SpanStart(rule, now);
                bool InLast(EvaluationRow row) => select(row) is { } d && d >= start && d <= now;
                return rule.Op == SmartSearchQueryOperator.InLast ? InLast : row => !InLast(row);
            default:
                throw RuleValues.Invalid(rule, "has an operator date fields don't take");
        }
    }
}
