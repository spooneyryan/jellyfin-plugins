using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Query;

/// <summary>
/// The validation limits. The validator, the reader and the tests read them from here.
/// </summary>
internal static class QueryLimits
{
    /// <summary>The largest serialized query, in UTF-8 bytes.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The deepest group nesting; the root group is depth 1.</summary>
    public const int MaxDepth = 4;

    /// <summary>The most rules (leaves) in a query.</summary>
    public const int MaxRules = 50;

    /// <summary>The most children of one group.</summary>
    public const int MaxChildren = 50;

    /// <summary>The most <c>Values</c> of one rule.</summary>
    public const int MaxValues = 100;

    /// <summary>The longest text value (and <c>Matches</c> pattern), in characters.</summary>
    public const int MaxTextLength = 200;

    /// <summary>The first year a rule may name (years and dates).</summary>
    public const int MinYear = 1800;

    /// <summary>The last year a rule may name (years and dates).</summary>
    public const int MaxYear = 2100;

    /// <summary>The widest year <c>Between</c>.</summary>
    public const int MaxYearSpan = 200;

    /// <summary>The most sort keys.</summary>
    public const int MaxSortKeys = 3;

    /// <summary>The largest <c>Limit</c>.</summary>
    public const int MaxLimit = 10_000;

    /// <summary>The most errors one validation reports; the rest are dropped.</summary>
    public const int MaxErrors = 50;

    /// <summary>The longest a <c>Matches</c> pattern may take on one value.</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Gets the item kinds a query may search, in the rule builder's order (M S E V H B).</summary>
    public static IReadOnlyList<BaseItemKind> ItemKindOrder { get; } =
    [
        BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video, BaseItemKind.BoxSet
    ];

    /// <summary>Gets the item kinds a query may search (M S E V H B), before the admin's <c>AllowedItemKinds</c>.</summary>
    public static FrozenSet<BaseItemKind> ItemKinds { get; } = ItemKindOrder.ToFrozenSet();

    /// <summary>Gets the most <c>Amount</c> of a relative date rule per unit: 3650 days, or the same span in larger units.</summary>
    /// <param name="unit">The unit.</param>
    /// <returns>The most units.</returns>
    public static int MaxRelativeAmount(SmartSearchRelativeUnit unit) => unit switch
    {
        SmartSearchRelativeUnit.Days => 3650,
        SmartSearchRelativeUnit.Weeks => 521,
        SmartSearchRelativeUnit.Months => 120,
        _ => 10
    };
}
