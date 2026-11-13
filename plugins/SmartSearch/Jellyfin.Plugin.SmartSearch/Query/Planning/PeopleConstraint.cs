using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// The constraint of <see cref="PushProperty.PersonIds"/>: the item is credited to one of the people (<c>PersonIds</c>),
/// and on Jellyfin 12 in the role (<c>PersonTypes</c>, which core checks on the same credit, so the two are one
/// constraint; <c>null</c> is any role). An item has many credits and the query one <c>PersonIds</c>, so under <c>All</c>
/// the first constraint is kept (a later rule stays in the residual unless the first implies it).
/// </summary>
internal sealed class PeopleConstraint : PushConstraint
{
    private readonly HashSet<Guid> _set;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeopleConstraint"/> class.
    /// </summary>
    /// <param name="ids">The people's item ids.</param>
    /// <param name="role">The role, or <c>null</c> for any.</param>
    public PeopleConstraint(IEnumerable<Guid> ids, string? role)
    {
        _set = [];
        Ids = ids.Where(_set.Add).ToList();
        Role = role;
    }

    /// <summary>Gets the people's ids, once each.</summary>
    public IReadOnlyList<Guid> Ids { get; }

    /// <summary>Gets the role, or <c>null</c> for any.</summary>
    public string? Role { get; }

    /// <inheritdoc />
    public override bool Impossible => Ids.Count == 0;

    /// <inheritdoc />
    public override PushConstraint And(PushConstraint other) => Impossible || !other.Impossible ? this : other;

    /// <inheritdoc />
    public override PushConstraint? Or(PushConstraint other)
    {
        // Credited to one of either's people: in the role both name, else in any role (wider, so still a superset).
        var that = (PeopleConstraint)other;
        return new PeopleConstraint(Ids.Concat(that.Ids), string.Equals(Role, that.Role, StringComparison.Ordinal) ? Role : null);
    }

    /// <inheritdoc />
    public override bool Implies(PushConstraint other)
    {
        // Credited to one of fewer people is credited to one of more; in a role, also in any role.
        var that = (PeopleConstraint)other;
        return (that.Role is null || string.Equals(that.Role, Role, StringComparison.Ordinal)) && Ids.All(that._set.Contains);
    }
}
