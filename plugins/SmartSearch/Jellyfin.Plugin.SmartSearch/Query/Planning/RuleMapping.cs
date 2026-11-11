namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>A rule's pushdown: the constraints its SQL filter puts on the query, and how they relate to the rule.</summary>
/// <param name="Set">The constraints; <see cref="PushdownSet.None"/> when declined.</param>
/// <param name="Exactness">Exact, Superset or Declined.</param>
/// <param name="Reason">Why it is Superset or Declined, or <c>null</c>.</param>
internal sealed record RuleMapping(PushdownSet Set, PushdownExactness Exactness, string? Reason)
{
    /// <summary>A declined mapping.</summary>
    /// <param name="reason">Why.</param>
    /// <returns>The mapping.</returns>
    public static RuleMapping Declined(string reason) => new(PushdownSet.None, PushdownExactness.Declined, reason);

    /// <summary>An exact mapping.</summary>
    /// <param name="set">The constraints.</param>
    /// <returns>The mapping.</returns>
    public static RuleMapping Exact(PushdownSet set) => new(set, PushdownExactness.Exact, null);

    /// <summary>A superset mapping.</summary>
    /// <param name="set">The constraints.</param>
    /// <param name="reason">Why it isn't exact.</param>
    /// <returns>The mapping.</returns>
    public static RuleMapping Superset(PushdownSet set, string reason) => new(set, PushdownExactness.Superset, reason);

    /// <summary>Weakens an exact mapping to a superset (the query's kinds reach beyond the field's, for example).</summary>
    /// <param name="reason">Why.</param>
    /// <returns>The mapping.</returns>
    public RuleMapping AtMostSuperset(string reason) => Exactness == PushdownExactness.Exact ? this with { Exactness = PushdownExactness.Superset, Reason = reason } : this;
}
