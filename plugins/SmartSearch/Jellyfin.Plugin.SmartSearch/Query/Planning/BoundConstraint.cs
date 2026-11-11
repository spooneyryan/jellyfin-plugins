using System;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// An inclusive lower or upper bound (<c>MinCommunityRating</c>, <c>MinPremiereDate</c>, <c>MaxPremiereDate</c>,
/// <c>MinDateCreated</c>, <c>MinWidth</c>, <c>MaxWidth</c>): under <c>All</c> the tighter bound, under <c>Any</c> the
/// looser one.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
internal sealed class BoundConstraint<T> : PushConstraint
    where T : struct, IComparable<T>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BoundConstraint{T}"/> class.
    /// </summary>
    /// <param name="value">The bound.</param>
    /// <param name="lower">Whether it is a lower bound.</param>
    public BoundConstraint(T value, bool lower)
    {
        Value = value;
        Lower = lower;
    }

    /// <summary>Gets the bound.</summary>
    public T Value { get; }

    /// <summary>Gets a value indicating whether it is a lower bound.</summary>
    public bool Lower { get; }

    /// <inheritdoc />
    public override PushConstraint And(PushConstraint other) => AtLeastAsTight((BoundConstraint<T>)other) ? this : other;

    /// <inheritdoc />
    public override PushConstraint? Or(PushConstraint other) => AtLeastAsTight((BoundConstraint<T>)other) ? other : this;

    /// <inheritdoc />
    public override bool Implies(PushConstraint other) => AtLeastAsTight((BoundConstraint<T>)other);

    // Whether this bound admits no value the other one refuses.
    private bool AtLeastAsTight(BoundConstraint<T> other) => Lower ? Value.CompareTo(other.Value) >= 0 : Value.CompareTo(other.Value) <= 0;
}
