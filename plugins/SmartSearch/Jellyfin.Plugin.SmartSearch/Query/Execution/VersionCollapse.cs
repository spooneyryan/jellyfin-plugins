using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>
/// One representative per version set, or, for a search core groups by presentation key, per version set and
/// presentation key: the unit <c>/Items</c> effectively shows.
/// </summary>
/// <remarks>
/// <para>Merged versions don't always share one key. Core's "Merge versions" leaves a chain (an item pointing at a
/// secondary keeps that secondary's id as its key), and a standalone item a primary lists keeps its own key; core's
/// <c>/Items</c> shows each key as its own row. Collapsing the whole set would show one result where <c>/Items</c> shows
/// two (a false negative), and a primary the caller can't see could merge two items they can see.</para>
/// <para>Within a unit, a match represents itself when it is the set's root; otherwise the root represents the unit when it
/// is eligible and carries the unit's key (it matched, or <c>eligible</c> confirms it), else the first matching member by
/// sort name (ordinal, ignoring case), then id. Without keys this is the kit's <see cref="VersionSets.Collapse"/>.</para>
/// </remarks>
internal static class VersionCollapse
{
    /// <summary>Collapses the matches.</summary>
    /// <param name="matches">The matching items (each eligible itself), in the caller's order, with their presentation keys.</param>
    /// <param name="byKey">Whether a set is split by presentation key (a search core groups by key).</param>
    /// <param name="eligible">Tells which of the given roots are eligible, with their presentation keys: called once, with
    /// every root that isn't a match, never with an empty list.</param>
    /// <param name="lookup">Finds items outside the matches (<see cref="VersionSets.LibraryLookup"/>).</param>
    /// <param name="findByPath">Finds the item at a link's path (<see cref="VersionSets.LibraryPaths"/>).</param>
    /// <param name="cancellationToken">The evaluation's cancellation token.</param>
    /// <returns>One representative per unit, in the order of each unit's first match.</returns>
    public static List<Representative> Collapse(
        IReadOnlyList<(VersionMember Member, string? Key)> matches,
        bool byKey,
        Func<IReadOnlyCollection<Guid>, IReadOnlyDictionary<Guid, string?>> eligible,
        Func<Guid, VersionMember?>? lookup,
        Func<VersionMember, string, Guid?>? findByPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(eligible);
        var order = new Dictionary<Guid, int>();
        var byId = new Dictionary<Guid, (VersionMember Member, string? Key)>();
        foreach (var match in matches)
        {
            if (match.Member.Id != Guid.Empty && byId.TryAdd(match.Member.Id, match))
            {
                order[match.Member.Id] = order.Count;
            }
        }

        string? KeyOf(Guid id) => byKey ? byId[id].Key : null;

        var units = VersionSets.Group(matches.Select(m => m.Member), lookup, findByPath, cancellationToken)
            .SelectMany(set => set.MemberIds
                .Where(order.ContainsKey)
                .OrderBy(id => order[id])
                .GroupBy(KeyOf, StringComparer.Ordinal)
                .Select(unit => (Set: set, Key: unit.Key, Matched: unit.ToList())))
            .OrderBy(u => order[u.Matched[0]])
            .ToList();

        cancellationToken.ThrowIfCancellationRequested();
        var rootsToCheck = units.Where(u => !order.ContainsKey(u.Set.RootId)).Select(u => u.Set.RootId).Distinct().ToList();
        var eligibleRoots = rootsToCheck.Count == 0 ? new Dictionary<Guid, string?>() : eligible(rootsToCheck);

        return units.Select(u =>
        {
            var root = u.Set.RootId;
            var rootRepresents = order.ContainsKey(root)
                ? u.Matched.Contains(root)
                : eligibleRoots.TryGetValue(root, out var rootKey) && (!byKey || string.Equals(rootKey, u.Key, StringComparison.Ordinal));
            var id = rootRepresents
                ? root
                : u.Matched.OrderBy(m => byId[m].Member.SortName ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(m => m).First();
            return new Representative(id, root, u.Key);
        }).ToList();
    }

    /// <summary>A unit's representative.</summary>
    /// <param name="Id">The item shown.</param>
    /// <param name="RootId">The version set's root.</param>
    /// <param name="Key">The unit's presentation key, or <c>null</c> when sets aren't split by key.</param>
    internal sealed record Representative(Guid Id, Guid RootId, string? Key);
}
