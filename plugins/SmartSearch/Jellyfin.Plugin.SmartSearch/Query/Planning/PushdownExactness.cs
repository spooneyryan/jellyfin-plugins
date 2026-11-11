namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// How a rule's SQL filter relates to its in-memory meaning. The planner's one rule: a pushed filter is
/// never narrower than the rule, so pushdown never loses an item.
/// </summary>
internal enum PushdownExactness
{
    /// <summary>SQL and memory agree on every item of the query's kinds; the rule leaves the residual.</summary>
    Exact,

    /// <summary>SQL returns at least every item the rule matches; the rule stays in the residual and is re-checked.</summary>
    Superset,

    /// <summary>Nothing is pushed: no mapper, or no SQL filter is provably a superset here; the rule is evaluated in memory.</summary>
    Declined
}
