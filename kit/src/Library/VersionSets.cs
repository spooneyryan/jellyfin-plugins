using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// Merged versions ("Group versions" / "Merge versions") as version sets: <see cref="Group"/> builds the sets with
/// their broken states, and <see cref="Collapse"/> gives one representative per set. Duplicates, search and
/// auto-collections share it, so a broken set neither vanishes nor counts twice.
/// </summary>
/// <remarks>
/// <para>Jellyfin 10.11.5 stores a set as a <c>PrimaryVersionId</c> on each secondary (the primary's id, "N" format) and the
/// primary's <c>LinkedAlternateVersions</c> (<c>LinkedChild { Path, ItemId }</c>, <c>Video.cs:43-49</c>). Nothing keeps the
/// two sides in step: deleting a primary leaves orphaned secondaries, and native "Group versions" folds a secondary's own
/// linked items into the new primary's list but leaves their pointer on the old primary, a chain
/// (<c>VideosController.cs:220-247</c>).</para>
/// <para>Pure: the library is reached only through the delegates, so an index can pass its own lookup
/// and fall back to <see cref="LibraryLookup"/> for items outside it.</para>
/// </remarks>
internal static class VersionSets
{
    /// <summary>
    /// Builds version sets:
    /// <list type="bullet">
    /// <item><description>The <b>root</b> of an item is found by following <c>PrimaryVersionId</c> until it is empty or names
    /// an item that doesn't exist (the root is then an orphaned secondary, <see cref="VersionSet.IsOrphaned"/>). A repeat
    /// stops the walk (<see cref="VersionSet.HasCycle"/>); the smallest id of the circle is then the root, the same from
    /// whichever member the walk started.</description></item>
    /// <item><description><b>Members</b> are every item whose pointer chain reaches the root, plus every live item the root
    /// lists that is standalone by its own pointers. A listed item that belongs to another set by its pointers stays there
    /// and is named in both sets' <see cref="VersionSet.ListedNotPointingIds"/>; a standalone item two roots list goes to
    /// the first set.</description></item>
    /// <item><description>A root's links resolve as Jellyfin resolves them: a cached id (none at all for the empty GUID, which
    /// Jellyfin writes after a failed lookup), else the path through <paramref name="findByPath"/>, else the library item
    /// id; a link that resolves to nothing is stale.</description></item>
    /// </list>
    /// Only sets with at least one input member are returned, in the order of their first input member; within a set the
    /// root comes first, then the other members in input order, then members found through the lookup by id.
    /// </summary>
    /// <param name="members">The videos to group (for Duplicates, the index). Duplicate ids and the empty GUID
    /// are skipped.</param>
    /// <param name="lookup">Finds an item that isn't among <paramref name="members"/> (a primary or listed item in another
    /// library): its member, or null when no such item exists. Null: nothing outside the members exists.
    /// <see cref="LibraryLookup"/> asks the library.</param>
    /// <param name="findByPath">Finds the item at a link's path, given the root that lists it (a relative path is relative
    /// to the root's folder). Null: paths find nothing. <see cref="LibraryPaths"/> asks the library.</param>
    /// <param name="cancellationToken">Checked for each input member and each item whose root is looked for (the lookups
    /// can reach the library many times).</param>
    /// <returns>The sets.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static IReadOnlyList<VersionSet> Group(
        IEnumerable<VersionMember> members,
        Func<Guid, VersionMember?>? lookup = null,
        Func<VersionMember, string, Guid?>? findByPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(members);
        var grouping = new Grouping(lookup, findByPath, cancellationToken);
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            grouping.AddInput(member);
        }

        return grouping.Build();
    }

    /// <summary>
    /// Gives one representative per version set of the matches: a set matches when any member
    /// matches, and it is represented by its root (primary) when the root is <b>eligible</b>, otherwise by the first
    /// matching member (by sort name, ordinal and ignoring case, then id). Built on <see cref="Group"/>.
    /// </summary>
    /// <param name="matches">The items that matched. They must be eligible themselves: they come from a query scoped to the
    /// caller's libraries and user (<see cref="LibraryAccess.CreateQuery"/>).</param>
    /// <param name="eligible">Tells which of the given roots are eligible: inside the caller's allowed libraries and, with a
    /// user, visible to that user. Called once, with every root that isn't a match, never with an empty list;
    /// <see cref="LibraryAccess.EligibleIds"/> answers it with one scoped <c>ItemIds</c> + <c>TopParentIds</c> query. Never
    /// <c>GetItemById&lt;T&gt;(id, null)</c>, which checks nothing without a user (<c>LibraryManager.cs:3361-3372</c>).</param>
    /// <param name="lookup">Finds items outside the matches, as for <see cref="Group"/>.</param>
    /// <param name="findByPath">Finds the item at a link's path, as for <see cref="Group"/>.</param>
    /// <returns>One representative per set, in the order of each set's first match.</returns>
    public static IReadOnlyList<VersionRepresentative> Collapse(
        IReadOnlyList<VersionMember> matches,
        Func<IReadOnlyCollection<Guid>, IReadOnlySet<Guid>> eligible,
        Func<Guid, VersionMember?>? lookup = null,
        Func<VersionMember, string, Guid?>? findByPath = null)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(eligible);

        var order = new Dictionary<Guid, int>();
        foreach (var match in matches)
        {
            if (match is not null && match.Id != Guid.Empty)
            {
                order.TryAdd(match.Id, order.Count);
            }
        }

        var byId = matches.Where(m => m is not null).GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        var sets = Group(matches, lookup, findByPath)
            .Select(set => (Set: set, Matched: set.MemberIds.Where(order.ContainsKey).OrderBy(id => order[id]).ToList()))
            .Where(s => s.Matched.Count > 0)
            .OrderBy(s => order[s.Matched[0]])
            .ToList();

        var rootsToCheck = sets.Where(s => !order.ContainsKey(s.Set.RootId)).Select(s => s.Set.RootId).Distinct().ToList();
        var eligibleRoots = rootsToCheck.Count == 0 ? new HashSet<Guid>() : eligible(rootsToCheck) ?? new HashSet<Guid>();

        return sets.Select(s => new VersionRepresentative
        {
            Id = order.ContainsKey(s.Set.RootId) || eligibleRoots.Contains(s.Set.RootId)
                ? s.Set.RootId
                : s.Matched.OrderBy(id => byId[id].SortName ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(id => id).First(),
            Set = s.Set,
            MatchedIds = s.Matched
        }).ToList();
    }

    /// <summary>
    /// A lookup for <see cref="Group"/> over the library: <c>GetItemById</c> (the cache-backed lookup without a user; the
    /// empty GUID is never passed, since 10.11.5 throws for it), projected with <see cref="VersionMember.From"/>.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <returns>The lookup.</returns>
    public static Func<Guid, VersionMember?> LibraryLookup(ILibraryManager library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return id => id == Guid.Empty ? null : library.GetItemById(id) is { } item ? VersionMember.From(item) : null;
    }

    /// <summary>
    /// A path lookup for <see cref="Group"/> over the library, as <c>BaseItem.FindLinkedChild</c> does it: a relative path
    /// is made absolute against the root's folder, then <c>FindByPath</c>; a blank path finds nothing (10.11.5's
    /// <c>FindByPath</c> has no path filter for one, <c>BaseItemRepository.cs:1788</c>).
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <returns>The path lookup.</returns>
    public static Func<VersionMember, string, Guid?> LibraryPaths(ILibraryManager library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return (root, path) =>
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var full = path;
            if (!Path.IsPathFullyQualified(path) && Path.GetDirectoryName(root.Path) is { Length: > 0 } folder)
            {
                full = Path.GetFullPath(Path.Combine(folder, path));
            }

            return library.FindByPath(full, null)?.Id;
        };
    }

    private sealed class Grouping
    {
        private readonly Func<Guid, VersionMember?>? _lookup;
        private readonly Func<VersionMember, string, Guid?>? _findByPath;
        private readonly Dictionary<Guid, int> _inputOrder = [];
        private readonly Dictionary<Guid, VersionMember?> _known = [];
        private readonly Dictionary<Guid, Guid> _rootOf = [];
        private readonly HashSet<Guid> _cycleRoots = [];
        private readonly CancellationToken _cancellationToken;

        public Grouping(Func<Guid, VersionMember?>? lookup, Func<VersionMember, string, Guid?>? findByPath, CancellationToken cancellationToken)
        {
            _lookup = lookup;
            _findByPath = findByPath;
            _cancellationToken = cancellationToken;
        }

        public void AddInput(VersionMember? member)
        {
            if (member is null || member.Id == Guid.Empty || _inputOrder.ContainsKey(member.Id))
            {
                return;
            }

            _inputOrder[member.Id] = _inputOrder.Count;
            _known[member.Id] = member;
        }

        public IReadOnlyList<VersionSet> Build()
        {
            foreach (var id in _inputOrder.Keys.ToList())
            {
                RootOf(_known[id]!);
            }

            // What the roots of the input's sets list, and where each listed item's own pointers lead (a listed item
            // outside the input that points at the root is a member like any other).
            var roots = _rootOf.Values.Distinct().OrderBy(FirstInput).ThenBy(r => r).ToList();
            var listed = new Dictionary<Guid, List<Guid>>();
            var stale = new Dictionary<Guid, List<VersionLink>>();
            foreach (var root in roots)
            {
                var (live, dead) = ResolveLinks(Find(root)!);
                listed[root] = live;
                stale[root] = dead;
                foreach (var id in live)
                {
                    RootOf(Find(id)!);
                }
            }

            var pointerSets = _rootOf.GroupBy(e => e.Value).ToDictionary(g => g.Key, g => g.Select(e => e.Key).ToList());

            // Listed items whose pointers don't lead to the root. A standalone one (its own root, nothing pointing at it)
            // joins the first set that lists it, unless that set's root joined another set itself or the item already
            // took others in; one that belongs to another set by its pointers stays there.
            var notPointing = roots.ToDictionary(r => r, _ => new List<Guid>());
            var absorbed = new Dictionary<Guid, Guid>();
            foreach (var root in roots)
            {
                foreach (var id in listed[root])
                {
                    var itsRoot = _rootOf[id];
                    if (itsRoot == root)
                    {
                        continue;
                    }

                    notPointing[root].Add(id);
                    var standalone = itsRoot == id && pointerSets[id].Count == 1;
                    if (standalone && !absorbed.ContainsKey(id) && !absorbed.ContainsKey(root) && !absorbed.ContainsValue(id))
                    {
                        absorbed[id] = root;
                    }
                }
            }

            var sets = new List<VersionSet>();
            foreach (var root in roots)
            {
                if (absorbed.ContainsKey(root))
                {
                    continue;
                }

                var pointerMembers = pointerSets[root];
                var members = pointerMembers.Concat(absorbed.Where(a => a.Value == root).Select(a => a.Key)).Distinct().ToList();
                if (!members.Any(_inputOrder.ContainsKey))
                {
                    continue;
                }

                var rootMember = Find(root)!;
                var isOrphaned = rootMember.PointsAt is not null && !_cycleRoots.Contains(root);
                var listedSet = listed[root].ToHashSet();
                var others = pointerMembers.Where(m => m != root).ToList();
                sets.Add(new VersionSet
                {
                    RootId = root,
                    MemberIds = members.OrderBy(m => m == root ? -1 : 0).ThenBy(FirstInput).ThenBy(m => m).ToList(),
                    ExternalIds = members.Where(m => !_inputOrder.ContainsKey(m)).OrderBy(m => m).ToList(),
                    IsOrphaned = isOrphaned,
                    MissingPrimaryVersionId = isOrphaned ? rootMember.PrimaryVersionId : null,
                    StaleLinks = stale[root],
                    StaleLinkIds = stale[root].Where(l => l.ItemId is { } id && id != Guid.Empty).Select(l => l.ItemId!.Value).Distinct().ToList(),
                    ChainedIds = others.Where(m => Find(m)!.PointsAt != root).OrderBy(FirstInput).ThenBy(m => m).ToList(),
                    UnlistedIds = others.Where(m => !listedSet.Contains(m)).OrderBy(FirstInput).ThenBy(m => m).ToList(),
                    ListedNotPointingIds = notPointing[root].Distinct().ToList(),
                    HasCycle = _cycleRoots.Contains(root)
                });
            }

            return sets.OrderBy(s => s.MemberIds.Where(_inputOrder.ContainsKey).Min(FirstInput)).ToList();
        }

        private int FirstInput(Guid id) => _inputOrder.TryGetValue(id, out var index) ? index : int.MaxValue;

        private VersionMember? Find(Guid id)
        {
            if (id == Guid.Empty)
            {
                return null;
            }

            if (_known.TryGetValue(id, out var member))
            {
                return member;
            }

            member = _lookup?.Invoke(id);
            if (member is not null && member.Id != id)
            {
                member = member with { Id = id };
            }

            _known[id] = member;
            return member;
        }

        // Follows PrimaryVersionId from the member to its root and remembers the root of every item on the way.
        private Guid RootOf(VersionMember member)
        {
            if (_rootOf.TryGetValue(member.Id, out var known))
            {
                return known;
            }

            _cancellationToken.ThrowIfCancellationRequested();
            var path = new List<Guid> { member.Id };
            var positions = new Dictionary<Guid, int> { [member.Id] = 0 };
            var current = member;
            Guid root;
            while (true)
            {
                if (current.PointsAt is not { } target)
                {
                    root = current.Id;
                    break;
                }

                if (positions.TryGetValue(target, out var at))
                {
                    root = path.Skip(at).Min();
                    _cycleRoots.Add(root);
                    break;
                }

                if (_rootOf.TryGetValue(target, out var reached))
                {
                    root = reached;
                    break;
                }

                if (Find(target) is not { } next)
                {
                    // The pointer names nothing (a deleted primary, or text that isn't an id): an orphan is its own root.
                    root = current.Id;
                    break;
                }

                positions[target] = path.Count;
                path.Add(target);
                current = next;
            }

            foreach (var id in path)
            {
                _rootOf[id] = root;
            }

            return root;
        }

        // A root's links as Jellyfin resolves them (BaseItem.GetLinkedChild, FindLinkedChild): the cached id (the empty
        // GUID means "not found before", with no second look), else the path, else the library item id.
        private (List<Guid> Live, List<VersionLink> Stale) ResolveLinks(VersionMember root)
        {
            var live = new List<Guid>();
            var stale = new List<VersionLink>();
            foreach (var link in root.LinkedAlternateVersions)
            {
                if (link is null)
                {
                    continue;
                }

                var id = ResolveLink(root, link);
                if (id is null)
                {
                    stale.Add(link);
                }
                else if (id.Value != root.Id && !live.Contains(id.Value))
                {
                    live.Add(id.Value);
                }
            }

            return (live, stale);
        }

        private Guid? ResolveLink(VersionMember root, VersionLink link)
        {
            if (link.ItemId is { } cached)
            {
                if (cached == Guid.Empty)
                {
                    return null;
                }

                if (Find(cached) is not null)
                {
                    return cached;
                }
            }

            if (!string.IsNullOrEmpty(link.Path))
            {
                return _findByPath?.Invoke(root, link.Path) is { } byPath && byPath != Guid.Empty && Find(byPath) is not null ? byPath : null;
            }

            return Guid.TryParse(link.LibraryItemId, out var libraryItemId) && Find(libraryItemId) is not null ? libraryItemId : null;
        }
    }
}
