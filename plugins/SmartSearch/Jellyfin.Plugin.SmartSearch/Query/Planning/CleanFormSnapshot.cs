using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// The clean-form probe's answer: the item names and <c>ItemValues</c> rows whose
/// stored clean form differs from what the evaluator compares (today's <c>RemoveDiacritics().ToLowerInvariant()</c> of the
/// value, or of each <c>|</c>-separated part of it, which is how items load their lists). On a database created by 10.9 or
/// later both lists are normally empty.
/// </summary>
/// <param name="TakenUtc">When the probe read the database.</param>
/// <param name="ItemsRead">How many item rows it read.</param>
/// <param name="ValuesRead">How many <c>ItemValues</c> rows it read.</param>
/// <param name="Names">The items whose <c>CleanName</c> differs.</param>
/// <param name="Values">The <c>ItemValues</c> rows whose <c>CleanValue</c> differs.</param>
internal sealed record CleanFormSnapshot(DateTime TakenUtc, int ItemsRead, int ValuesRead, IReadOnlyList<StaleName> Names, IReadOnlyList<ItemValueRow> Values)
{
    /// <summary>Gets an empty snapshot (nothing stale), as a database created by 10.9 or later gives.</summary>
    public static CleanFormSnapshot Clean { get; } = new(DateTime.UnixEpoch, 0, 0, [], []);
}
