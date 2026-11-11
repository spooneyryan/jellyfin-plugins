using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// Decides, from the clean-form probe's rows, whether a pushed text or value filter can miss an item the evaluator
/// matches (core's stored clean forms can be stale). SQL compares a stored clean form; the evaluator folds what the item
/// carries. Where they differ for a row, the rule's value is checked against that row: if memory would match the row and
/// SQL wouldn't, the rule isn't pushed; if SQL would and memory wouldn't, it is pushed as Superset (re-checked).
/// </summary>
internal static class CleanFormRisk
{
    /// <summary>The value kinds the probe covers.</summary>
    public static readonly IReadOnlyList<ItemValueType> ValueTypes = [ItemValueType.Genre, ItemValueType.Studios, ItemValueType.Tags];

    /// <summary>
    /// The keys the evaluator compares for an <c>ItemValues</c> row: items load a list by splitting a <c>|</c>-joined
    /// column, and blank entries are ignored; each part is folded.
    /// </summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The folded parts.</returns>
    public static IReadOnlySet<string> MemoryKeys(string value)
        => value.Split('|').Where(p => !string.IsNullOrWhiteSpace(p)).Select(RuleValues.Fold).ToHashSet(StringComparer.Ordinal);

    /// <summary>Tells whether SQL and memory see an <c>ItemValues</c> row differently.</summary>
    /// <param name="value">The stored value.</param>
    /// <param name="cleanValue">The stored clean value.</param>
    /// <returns>Whether the row differs.</returns>
    public static bool Differs(string value, string? cleanValue)
    {
        var keys = MemoryKeys(value);
        return cleanValue is null || keys.Count != 1 || !keys.Contains(cleanValue);
    }

    /// <summary>Tells whether an item's stored clean name differs from what the evaluator compares.</summary>
    /// <param name="name">The name.</param>
    /// <param name="cleanName">The stored clean name.</param>
    /// <returns>Whether it differs.</returns>
    public static bool NameDiffers(string? name, string? cleanName)
        => !string.IsNullOrWhiteSpace(name) && !string.Equals(RuleValues.Fold(name), cleanName, StringComparison.Ordinal);

    /// <summary>
    /// Rates <c>Genres</c>/<c>Tags</c>/<c>StudioIds</c> (<paramref name="exclude"/> false: any of the values) or
    /// <c>ExcludeTags</c> (true: none of them).
    /// </summary>
    /// <param name="rows">The rows that differ (the probe's and the per-plan <c>|</c> rows).</param>
    /// <param name="type">The value kind.</param>
    /// <param name="values">The rule's values (core cleans them, as the evaluator folds them).</param>
    /// <param name="exclude">Whether the filter excludes.</param>
    /// <returns><see cref="PushdownExactness.Declined"/> when SQL could miss an item memory matches; else whether SQL could
    /// return an item memory doesn't match (<see cref="PushdownExactness.Superset"/>) or not (<see cref="PushdownExactness.Exact"/>).</returns>
    public static PushdownExactness ForValues(IEnumerable<ItemValueRow> rows, ItemValueType type, IReadOnlyCollection<string> values, bool exclude)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(values);
        var wanted = values.Select(RuleValues.Fold).ToHashSet(StringComparer.Ordinal);
        var result = PushdownExactness.Exact;
        foreach (var row in rows.Where(r => r.Type == type && Differs(r.Value, r.CleanValue)))
        {
            var memory = MemoryKeys(row.Value).Overlaps(wanted);
            var sql = row.CleanValue is { } clean && wanted.Contains(clean);

            // Any of the values: SQL misses an item memory matches through this row. None of them: SQL drops an item
            // memory keeps.
            if (exclude ? sql && !memory : memory && !sql)
            {
                return PushdownExactness.Declined;
            }

            if (memory != sql)
            {
                result = PushdownExactness.Superset;
            }
        }

        return result;
    }

    /// <summary>Rates <c>Name</c> (<c>CleanName</c> equals the clean value).</summary>
    /// <param name="names">The items whose clean name differs.</param>
    /// <param name="value">The rule's value.</param>
    /// <returns>How the filter relates to <c>Title Eq</c>.</returns>
    public static PushdownExactness ForName(IEnumerable<StaleName> names, string value)
    {
        ArgumentNullException.ThrowIfNull(names);
        var folded = RuleValues.Fold(value);
        var result = PushdownExactness.Exact;
        foreach (var name in names)
        {
            var memory = !string.IsNullOrWhiteSpace(name.Name) && string.Equals(RuleValues.Fold(name.Name), folded, StringComparison.Ordinal);
            var sql = string.Equals(name.CleanName, folded, StringComparison.Ordinal);
            if (memory && !sql)
            {
                return PushdownExactness.Declined;
            }

            if (memory != sql)
            {
                result = PushdownExactness.Superset;
            }
        }

        return result;
    }

    /// <summary>
    /// Rates <c>NameContains</c> (already a superset): declined when an item's stored clean name wouldn't hold the term
    /// though its name does. SQL is taken at its narrowest (the plain <c>instr</c> on <c>CleanName</c>; the original title
    /// and <c>LIKE</c> only add matches).
    /// </summary>
    /// <param name="names">The items whose clean name differs.</param>
    /// <param name="term">The folded term.</param>
    /// <returns><see cref="PushdownExactness.Declined"/> or <see cref="PushdownExactness.Superset"/>.</returns>
    public static PushdownExactness ForNameContains(IEnumerable<StaleName> names, string term)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (var name in names)
        {
            if (!string.IsNullOrWhiteSpace(name.Name)
                && RuleValues.Fold(name.Name).Contains(term, StringComparison.Ordinal)
                && !(name.CleanName ?? string.Empty).Contains(term, StringComparison.Ordinal))
            {
                return PushdownExactness.Declined;
            }
        }

        return PushdownExactness.Superset;
    }
}
