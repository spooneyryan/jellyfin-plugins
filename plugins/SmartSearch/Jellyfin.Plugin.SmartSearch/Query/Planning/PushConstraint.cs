namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// What a pushed filter demands of one <c>InternalItemsQuery</c> property, in core's SQL semantics, with the merges of
/// the planner's algebra: under <c>All</c> the result is implied by both sides, so it may only be wider
/// than their conjunction; under <c>Any</c> it is implied by each branch.
/// </summary>
internal abstract class PushConstraint
{
    /// <summary>Gets a value indicating whether no item can satisfy it (an empty set); a query with it isn't run.</summary>
    public virtual bool Impossible => false;

    /// <summary>Merges it with another constraint on the same property under <c>All</c>.</summary>
    /// <param name="other">The other constraint.</param>
    /// <returns>A constraint every item satisfying both satisfies (it may be just this one).</returns>
    public abstract PushConstraint And(PushConstraint other);

    /// <summary>Merges it with another constraint on the same property under <c>Any</c>.</summary>
    /// <param name="other">The other constraint.</param>
    /// <returns>A constraint every item satisfying either satisfies, or <c>null</c> for none.</returns>
    public abstract PushConstraint? Or(PushConstraint other);

    /// <summary>Tells whether every item satisfying this constraint satisfies the other one.</summary>
    /// <param name="other">The other constraint, on the same property.</param>
    /// <returns>Whether this implies it.</returns>
    public abstract bool Implies(PushConstraint other);
}
