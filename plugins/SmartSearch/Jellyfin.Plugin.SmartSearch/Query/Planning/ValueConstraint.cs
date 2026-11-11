using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// "The property is exactly this" (the bool filters, <c>Name</c>, <c>NameContains</c>): under <c>All</c> the first one is
/// kept (a different second value stays in the residual), under <c>Any</c> only an equal value survives.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
internal sealed class ValueConstraint<T> : PushConstraint
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ValueConstraint{T}"/> class.
    /// </summary>
    /// <param name="value">The value.</param>
    public ValueConstraint(T value)
    {
        Value = value;
    }

    /// <summary>Gets the value.</summary>
    public T Value { get; }

    /// <inheritdoc />
    public override PushConstraint And(PushConstraint other) => this;

    /// <inheritdoc />
    public override PushConstraint? Or(PushConstraint other) => Implies(other) ? this : null;

    /// <inheritdoc />
    public override bool Implies(PushConstraint other) => EqualityComparer<T>.Default.Equals(Value, ((ValueConstraint<T>)other).Value);
}
