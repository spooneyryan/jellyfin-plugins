using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// "The item's value is one of these" (<c>IncludeItemTypes</c>, <c>TopParentIds</c>, <c>Years</c>,
/// <c>OfficialRatings</c>), or, for a property with many values per item, "one of the item's values is one of these"
/// (<c>Genres</c>, <c>Tags</c>, <c>StudioIds</c>, <c>AncestorIds</c>). Under <c>All</c> one value per item allows the
/// intersection; with many the sets can't be intersected ("has A or B" and "has B or C" both hold for an item with A and
/// C), so the first one is kept and the later rule stays in the residual ("property already set"). Under
/// <c>Any</c> the union.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
internal sealed class InSetConstraint<T> : PushConstraint
{
    private readonly HashSet<T> _set;

    /// <summary>
    /// Initializes a new instance of the <see cref="InSetConstraint{T}"/> class.
    /// </summary>
    /// <param name="values">The values, in order; repeated ones are dropped.</param>
    /// <param name="comparer">How values compare (core's way: exact, or by clean form).</param>
    /// <param name="singleValued">Whether an item has one value of the property.</param>
    public InSetConstraint(IEnumerable<T> values, IEqualityComparer<T> comparer, bool singleValued)
    {
        _set = new HashSet<T>(comparer);
        Values = values.Where(_set.Add).ToList();
        Comparer = comparer;
        SingleValued = singleValued;
    }

    /// <summary>Gets the values, in order.</summary>
    public IReadOnlyList<T> Values { get; }

    /// <summary>Gets how values compare.</summary>
    public IEqualityComparer<T> Comparer { get; }

    /// <summary>Gets a value indicating whether an item has one value of the property.</summary>
    public bool SingleValued { get; }

    /// <inheritdoc />
    public override bool Impossible => Values.Count == 0;

    /// <summary>Tells whether a value is in the set.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether it is.</returns>
    public bool Contains(T value) => _set.Contains(value);

    /// <inheritdoc />
    public override PushConstraint And(PushConstraint other)
    {
        var that = (InSetConstraint<T>)other;
        return SingleValued ? new InSetConstraint<T>(Values.Where(that.Contains), Comparer, true) : this;
    }

    /// <inheritdoc />
    public override PushConstraint? Or(PushConstraint other)
        => new InSetConstraint<T>(Values.Concat(((InSetConstraint<T>)other).Values), Comparer, SingleValued);

    /// <inheritdoc />
    public override bool Implies(PushConstraint other)
    {
        // Being in a smaller set is being in the larger one, and having one of a smaller set's values is having one of the larger's.
        var that = (InSetConstraint<T>)other;
        return Values.All(that.Contains);
    }
}
