using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// A conjunction of pushed constraints, one per <c>InternalItemsQuery</c> property, and the algebra the
/// planner builds the query with: a rule maps to one, an <c>All</c> group is the <see cref="And"/> of its children, an
/// <c>Any</c> group the <see cref="Or"/> of its branches (only the properties every branch constrains survive), and a
/// negated group constrains nothing. Every step only widens, so the final set, applied to the base query, returns at
/// least every item the whole tree matches; <see cref="Implies"/> then tells which rules SQL fully enforces.
/// </summary>
internal sealed class PushdownSet
{
    private readonly Dictionary<PushProperty, PushConstraint> _constraints;

    private PushdownSet(Dictionary<PushProperty, PushConstraint> constraints)
    {
        _constraints = constraints;
    }

    /// <summary>Gets the set that constrains nothing.</summary>
    public static PushdownSet None { get; } = new([]);

    /// <summary>Gets the constrained properties.</summary>
    public IReadOnlyCollection<PushProperty> Properties => _constraints.Keys;

    /// <summary>Gets a value indicating whether no item can satisfy it.</summary>
    public bool Impossible => _constraints.Values.Any(c => c.Impossible);

    /// <summary>Gets the constraint on a property, or <c>null</c>.</summary>
    /// <param name="property">The property.</param>
    /// <returns>The constraint.</returns>
    public PushConstraint? this[PushProperty property] => _constraints.GetValueOrDefault(property);

    /// <summary>Creates a set with one constraint.</summary>
    /// <param name="property">The property.</param>
    /// <param name="constraint">The constraint.</param>
    /// <returns>The set.</returns>
    public static PushdownSet Of(PushProperty property, PushConstraint constraint) => new(new() { [property] = constraint });

    /// <summary>Creates a set from several constraints.</summary>
    /// <param name="constraints">The constraints; a property given twice keeps the first.</param>
    /// <returns>The set.</returns>
    public static PushdownSet Of(params (PushProperty Property, PushConstraint Constraint)[] constraints)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        var map = new Dictionary<PushProperty, PushConstraint>();
        foreach (var (property, constraint) in constraints)
        {
            map.TryAdd(property, constraint);
        }

        return new(map);
    }

    /// <summary>Merges two sets under <c>All</c>.</summary>
    /// <param name="other">The later set.</param>
    /// <returns>The merged set.</returns>
    public PushdownSet And(PushdownSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var map = new Dictionary<PushProperty, PushConstraint>(_constraints);
        foreach (var (property, constraint) in other._constraints)
        {
            map[property] = map.TryGetValue(property, out var mine) ? mine.And(constraint) : constraint;
        }

        return new(map);
    }

    /// <summary>Merges two sets under <c>Any</c>: only the properties both constrain survive, each widened.</summary>
    /// <param name="other">The other branch's set.</param>
    /// <returns>The merged set.</returns>
    public PushdownSet Or(PushdownSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var map = new Dictionary<PushProperty, PushConstraint>();
        foreach (var (property, constraint) in _constraints)
        {
            if (other._constraints.TryGetValue(property, out var theirs) && constraint.Or(theirs) is { } merged)
            {
                map[property] = merged;
            }
        }

        return new(map);
    }

    /// <summary>Tells whether every item satisfying this set satisfies every constraint of the other.</summary>
    /// <param name="other">The other set (a rule's mapping).</param>
    /// <returns>Whether this implies it.</returns>
    public bool Implies(PushdownSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return other._constraints.All(pair => _constraints.TryGetValue(pair.Key, out var mine) && mine.Implies(pair.Value));
    }

    /// <summary>Sets the constrained properties on a query. Never sets an empty array (core reads it as "no filter"):
    /// callers check <see cref="Impossible"/> first.</summary>
    /// <param name="query">The query.</param>
    public void Apply(InternalItemsQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Impossible)
        {
            throw new InvalidOperationException("An impossible pushdown is never run; the plan is empty.");
        }

        foreach (var (property, constraint) in _constraints)
        {
            switch (property)
            {
                case PushProperty.IncludeItemTypes:
                    query.IncludeItemTypes = Set<BaseItemKind>(constraint).OrderBy(k => QueryLimitsOrder(k)).ToArray();
                    break;
                case PushProperty.TopParentIds:
                    query.TopParentIds = Set<Guid>(constraint).ToArray();
                    break;
                case PushProperty.Years:
                    query.Years = Set<int>(constraint).Order().ToArray();
                    break;
                case PushProperty.OfficialRatings:
                    query.OfficialRatings = Set<string>(constraint).ToArray();
                    break;
                case PushProperty.Genres:
                    query.Genres = Set<string>(constraint).ToArray();
                    break;
                case PushProperty.Tags:
                    query.Tags = Set<string>(constraint).ToArray();
                    break;
                case PushProperty.StudioIds:
                    query.StudioIds = Set<Guid>(constraint).ToArray();
                    break;
                case PushProperty.AncestorIds:
                    query.AncestorIds = Set<Guid>(constraint).ToArray();
                    break;
                case PushProperty.ExcludeTags:
                    query.ExcludeTags = ((ExcludeSetConstraint)constraint).Values.ToArray();
                    break;
                case PushProperty.MinCommunityRating:
                    query.MinCommunityRating = Bound<double>(constraint);
                    break;
                case PushProperty.MinPremiereDate:
                    query.MinPremiereDate = Bound<DateTime>(constraint);
                    break;
                case PushProperty.MaxPremiereDate:
                    query.MaxPremiereDate = Bound<DateTime>(constraint);
                    break;
                case PushProperty.MinDateCreated:
                    query.MinDateCreated = Bound<DateTime>(constraint);
                    break;
                case PushProperty.MinWidth:
                    query.MinWidth = Bound<int>(constraint);
                    break;
                case PushProperty.MaxWidth:
                    query.MaxWidth = Bound<int>(constraint);
                    break;
                case PushProperty.HasOverview:
                    query.HasOverview = Value<bool>(constraint);
                    break;
                case PushProperty.IsLocked:
                    query.IsLocked = Value<bool>(constraint);
                    break;
                case PushProperty.IsPlayed:
                    query.IsPlayed = Value<bool>(constraint);
                    break;
                case PushProperty.IsFavorite:
                    query.IsFavorite = Value<bool>(constraint);
                    break;
                case PushProperty.IsResumable:
                    query.IsResumable = Value<bool>(constraint);
                    break;
                case PushProperty.HasTmdbId:
                    query.HasTmdbId = Value<bool>(constraint);
                    break;
                case PushProperty.HasImdbId:
                    query.HasImdbId = Value<bool>(constraint);
                    break;
                case PushProperty.HasTvdbId:
                    query.HasTvdbId = Value<bool>(constraint);
                    break;
                case PushProperty.Name:
                    query.Name = Value<string>(constraint);
                    break;
                case PushProperty.NameContains:
                    query.NameContains = Value<string>(constraint);
                    break;
                case PushProperty.PersonIds:
                    var people = (PeopleConstraint)constraint;
                    query.PersonIds = [.. people.Ids];
                    if (people.Role is { } role)
                    {
#if JELLYFIN_12
                        query.PersonTypes = [role];
#else
                        throw new InvalidOperationException("Jellyfin 10.11 ignores PersonTypes in item queries; a role is never pushed there (" + role + ").");
#endif
                    }

                    break;
                default:
                    throw new InvalidOperationException("No query property for " + property + ".");
            }
        }
    }

    /// <summary>Describes the set for logs and tests: <c>Property=value</c> pairs in property order.</summary>
    /// <returns>The description.</returns>
    public override string ToString()
        => string.Join(", ", _constraints.OrderBy(p => p.Key).Select(p => p.Key + "=" + Describe(p.Value)));

    private static int QueryLimitsOrder(BaseItemKind kind)
    {
        for (var i = 0; i < QueryLimits.ItemKindOrder.Count; i++)
        {
            if (QueryLimits.ItemKindOrder[i] == kind)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static IReadOnlyList<T> Set<T>(PushConstraint constraint) => ((InSetConstraint<T>)constraint).Values;

    private static T Bound<T>(PushConstraint constraint)
        where T : struct, IComparable<T>
        => ((BoundConstraint<T>)constraint).Value;

    private static T Value<T>(PushConstraint constraint) => ((ValueConstraint<T>)constraint).Value;

    private static string Describe(PushConstraint constraint) => constraint switch
    {
        InSetConstraint<BaseItemKind> s => "[" + string.Join(",", s.Values) + "]",
        InSetConstraint<Guid> s => "[" + string.Join(",", s.Values.Select(g => g.ToString("N"))) + "]",
        InSetConstraint<int> s => s.Values.Count > 6 ? "[" + s.Values.Min() + ".." + s.Values.Max() + " (" + s.Values.Count + ")]" : "[" + string.Join(",", s.Values) + "]",
        InSetConstraint<string> s => "[" + string.Join(",", s.Values) + "]",
        ExcludeSetConstraint s => "[" + string.Join(",", s.Values) + "]",
        BoundConstraint<DateTime> b => b.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        BoundConstraint<double> b => b.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        BoundConstraint<int> b => b.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ValueConstraint<bool> v => v.Value ? "true" : "false",
        ValueConstraint<string> v => v.Value,
        PeopleConstraint p => "[" + string.Join(",", p.Ids.Select(g => g.ToString("N"))) + "]" + (p.Role is null ? string.Empty : " as " + p.Role),
        _ => constraint.ToString() ?? string.Empty
    };
}
