using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// Which libraries and items a user may reach: the visible libraries, per-item visibility,
/// library ids a caller sends (views included) resolved to libraries, and the <c>InternalItemsQuery(user)</c> factory
/// every scoped query starts from. Admins get no bypass; only a null user (an API key, or server-wide work)
/// sees every library.
/// </summary>
/// <remarks>
/// <para><b>The factory</b> (<see cref="CreateQuery"/>) builds <c>new InternalItemsQuery(user)</c>, whose constructor calls
/// <c>SetUser</c> (parental rating, unrated items and the tag filters in SQL, <c>InternalItemsQuery.cs:59-66, 366-390</c>),
/// with explicit <c>TopParentIds</c>: the requested libraries intersected with the user's. Setting them means
/// core's <c>AddUserToQuery</c> never runs (<c>LibraryManager.cs:1703-1709</c>), so access comes only from here, which also
/// makes an <c>ItemIds</c> filter safe. It sets <c>GroupByPresentationUniqueKey = false</c>: with a user, core otherwise
/// groups by <c>PresentationUniqueKey</c> and keeps one arbitrary member of each merged version set, even for an
/// <c>ItemIds</c> lookup; a caller that wants core's grouping sets it back. With no library left it returns null:
/// never query with empty <c>TopParentIds</c>, which means "every library" (core itself puts a random id there,
/// <c>LibraryManager.cs:1678-1685, 1720-1724</c>).</para>
/// <para><b>Never <c>HasOwnerId</c>.</b> On 10.11.5 <c>HasOwnerId = false</c> means <c>OwnerId IS NULL</c>, while every item
/// saved on 10.11 stores the zero-GUID string (<c>BaseItemRepository.cs:1010, 2235-2246</c>), so the filter drops every
/// current item. Owned items (extras, local alternate versions) have no top parent, so explicit <c>TopParentIds</c> leaves
/// them out; <see cref="IsOwned"/> is the in-memory safety net.</para>
/// <para><b>Owned items</b>. Extras, local trailers and local alternate versions (<c>OwnerId</c> set) have no top
/// parent, so a <c>TopParentIds</c> query never returns them and <see cref="EligibleIds"/> leaves them out by default
/// (<see cref="OwnedItems.Exclude"/>: they are never results of their own). Code that checks ids a client sent must
/// keep them when their owner is visible, as <c>GetItemById&lt;T&gt;(id, user)</c> does (its collection-folder check
/// walks <c>GetOwner()</c> to the owner's library, <c>LibraryManager.cs:2256-2289</c>):
/// <see cref="OwnedItems.FollowOwner"/>, or the per-item <see cref="Visibility"/>.</para>
/// <para><b>Parents</b>. On 10.11.5 <c>GetItemList</c> keeps an item when the item itself passes
/// <c>IsVisible(user)</c> (<c>LibraryManager.cs:1408-1431</c>), while <c>GetItemById&lt;T&gt;(id, user)</c> asks
/// <c>IsVisibleStandalone(user)</c>, which also refuses the item when any parent fails <c>IsVisible(user, true)</c>
/// (<c>BaseItem.cs:1330-1351</c>): a TV-PG episode of a TV-MA series, an episode of an unrated series that the user's
/// <c>BlockUnratedItems</c> blocks, a PG movie in a folder rated R. <see cref="EligibleIds"/> checks the parents itself, so
/// a list never shows what the details page refuses.</para>
/// </remarks>
internal sealed class LibraryAccess
{
    /// <summary>How many ids one <see cref="EligibleIds"/> query carries.</summary>
    public const int IdsPerQuery = 500;

    /// <summary>The most parents walked above one item; a longer chain (a cycle) counts as not visible.</summary>
    public const int MaxParentDepth = 64;

    /// <summary>The most views <see cref="Visibility"/> follows up from a view (a sub-view, its shadow view, the library).</summary>
    public const int MaxViewDepth = 4;

    private readonly ILibraryManager _library;
    private readonly Func<User?, IReadOnlyList<CollectionFolder>> _libraries;
    private readonly Func<BaseItem, User, bool> _isVisible;
    private readonly Func<BaseItem, User, bool> _isItemVisible;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryAccess"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="libraries">The libraries a user can open, or every library for null; by default the user root folder's
    /// <see cref="CollectionFolder"/> children the user can see (<c>GetChildren(user, true)</c>: the library access and
    /// blocked folders of the user's policy and the library's <c>Enabled</c> option, hidden-from-home libraries included,
    /// as <c>AddUserToQuery</c> asks for them with <c>IncludeHidden = true</c>).</param>
    /// <param name="isVisible">Whether one parent, on its own, lets a user see what is under it; by default core's
    /// <c>parent.IsVisible(user, true)</c>, the check <c>IsVisibleStandalone</c> makes of every parent (the parental rating,
    /// unrated items and blocked tags; not the allowed tags). Tests give their fake library's.</param>
    /// <param name="isItemVisible">Whether an owned item itself is visible to a user, the check core's
    /// <c>IsVisibleStandaloneInternal</c> makes of the item first (<c>IsVisible(user)</c>, the allowed tags included); used
    /// for the owned items <see cref="OwnedItems.FollowOwner"/> keeps, which no <c>SetUser</c> SQL filter has seen. Tests give
    /// their fake library's.</param>
    public LibraryAccess(ILibraryManager library, Func<User?, IReadOnlyList<CollectionFolder>>? libraries = null, Func<BaseItem, User, bool>? isVisible = null, Func<BaseItem, User, bool>? isItemVisible = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        _library = library;
        _libraries = libraries ?? (user => ServerLibraries(library, user));
        _isVisible = isVisible ?? (static (parent, user) => parent.IsVisible(user, true));
        _isItemVisible = isItemVisible ?? (static (item, user) => item.IsVisible(user));
    }

    /// <summary>
    /// Gets whether an item is owned by another (an extra or a local alternate version, <c>OwnerId</c> set). Such items
    /// have no top parent and are never results of their own; this is the in-memory check, never
    /// <c>HasOwnerId</c>.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>Whether it has an owner.</returns>
    public static bool IsOwned(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.OwnerId != Guid.Empty;
    }

    /// <summary>
    /// Gets the top-parent ids of libraries: their <c>PhysicalFolderIds</c>, what Jellyfin stores as an item's
    /// <c>TopParentId</c> and <c>InternalItemsQuery.TopParentIds</c> takes (one library can have several folders, and one
    /// folder can be in two libraries).
    /// </summary>
    /// <param name="libraries">The libraries.</param>
    /// <returns>The distinct ids, never the empty GUID.</returns>
    public static Guid[] TopParentIds(IEnumerable<CollectionFolder> libraries)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        return libraries.Where(l => l is not null).SelectMany(l => l.PhysicalFolderIds ?? []).Where(id => id != Guid.Empty).Distinct().ToArray();
    }

    /// <summary>
    /// Gets the libraries a user can open; every library for null (an API key, or work that belongs to no user).
    /// </summary>
    /// <param name="user">The user, or null.</param>
    /// <returns>The libraries, each once.</returns>
    public IReadOnlyList<CollectionFolder> VisibleLibraries(User? user)
        => (_libraries(user) ?? []).Where(l => l is not null).GroupBy(l => l.Id).Select(g => g.First()).ToList();

    /// <summary>
    /// Gets an item as the user sees it: <c>GetItemById&lt;T&gt;(id, user)</c>, so an item in a
    /// library the user can't open, above their parental limit or of the wrong type is the same as none. Never asks for
    /// the empty GUID, which throws on 10.11.5. An id whose row the server can't load is none as well: 10.11.5's
    /// <c>GetItemById</c> throws <see cref="InvalidOperationException"/> ("Cannot deserialize unknown type.") for its own
    /// placeholder item <c>00000000-0000-0000-0000-000000000001</c> (kept for detached user data) and for an item of a
    /// type no loaded assembly has, which made a route that looked such an id up answer 500. With a null user it checks
    /// nothing but existence and type, so only callers without a user (API keys, server-wide work) pass null.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="id">The item id.</param>
    /// <param name="user">The user, or null.</param>
    /// <returns>The item, or null.</returns>
    public T? GetItem<T>(Guid id, User? user)
        where T : BaseItem
    {
        if (id == Guid.Empty)
        {
            return null;
        }

        try
        {
            return _library.GetItemById<T>(id, user);
        }
        catch (InvalidOperationException)
        {
            // A row the server can't make an item of (above): the same as no item.
            return null;
        }
    }

    /// <summary>
    /// Tells what an id a client sent is to a user (Access Roles' checks): <see cref="ItemVisibility.Unknown"/> when it
    /// names no item at all (the empty GUID, a removed item, a grouped view's computed id), so a check can let it
    /// through and the core find nothing either; <see cref="ItemVisibility.Hidden"/> when it names an item that
    /// <c>GetItemById&lt;BaseItem&gt;(id, user)</c> refuses (a library the user can't open, an item under a hidden
    /// parent or above the user's parental limit), or a named view of a library the user can't open; else
    /// <see cref="ItemVisibility.Visible"/>. A <see cref="UserView"/> is judged by its <c>DisplayParentId</c>, else its
    /// <c>ParentId</c>: a library's sub-views live under <c>metadata\views</c> and match no collection folder, so
    /// <c>GetItemById&lt;BaseItem&gt;(view, user)</c> alone lets them through (measured on 10.11.5). Owned items (a
    /// movie's local trailer) follow their owner's library, as core's own check does.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="user">The user.</param>
    /// <returns>What the id is to the user.</returns>
    public ItemVisibility Visibility(Guid id, User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return VisibilityOf(id, user, 0);
    }

    /// <summary>
    /// Resolves the library ids a caller sent to libraries the user can open. An id may name a library, or a view: a
    /// named view resolves its <c>DisplayParentId</c> first, then its <c>ParentId</c>, because a view's own id matches
    /// no library (as core's <c>GetTopParentIdsForQuery</c> does, <c>LibraryManager.cs:1728-1787</c>); a grouped view
    /// ("Movies" over several libraries) gives the user's grouped libraries of its type. Ids that name nothing the user
    /// can open are dropped, so the result is always within <see cref="VisibleLibraries"/>.
    /// </summary>
    /// <param name="requested">The ids; null or empty means every library the user can open.</param>
    /// <param name="user">The user, or null for every library.</param>
    /// <returns>The libraries, each once, in the order asked for.</returns>
    public IReadOnlyList<CollectionFolder> ResolveLibraries(IEnumerable<Guid>? requested, User? user)
    {
        var visible = VisibleLibraries(user);
        var asked = requested?.ToList();
        if (asked is null || asked.Count == 0)
        {
            return visible;
        }

        var byId = visible.ToDictionary(l => l.Id);
        var result = new List<CollectionFolder>();
        foreach (var id in asked.Where(id => id != Guid.Empty).Distinct())
        {
            foreach (var libraryId in LibraryIdsOf(id, user, depth: 0))
            {
                if (byId.TryGetValue(libraryId, out var folder) && !result.Contains(folder))
                {
                    result.Add(folder);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Creates the query every scoped lookup starts from: <c>new InternalItemsQuery(user)</c>
    /// (or a plain query without a user), <c>TopParentIds</c> the requested libraries the user can open, and
    /// <c>GroupByPresentationUniqueKey = false</c>. It never sets <c>HasOwnerId</c>.
    /// </summary>
    /// <param name="user">The user, or null for a query without a user (an API key, server-wide work).</param>
    /// <param name="libraryIds">Library or view ids to limit it to; null or empty for every library the user can open.</param>
    /// <returns>The query, or null when no library is left: answer with nothing, without querying.</returns>
    public InternalItemsQuery? CreateQuery(User? user, IEnumerable<Guid>? libraryIds = null)
    {
        var topParents = TopParentIds(ResolveLibraries(libraryIds, user));
        if (topParents.Length == 0)
        {
            return null;
        }

        var query = user is null ? new InternalItemsQuery() : new InternalItemsQuery(user);
        query.TopParentIds = topParents;
        query.GroupByPresentationUniqueKey = false;
        return KitItemQueries.IncludeEveryVersion(query);
    }

    /// <summary>
    /// Tells which of the ids are <b>eligible</b> (the check behind <see cref="VersionSets.Collapse"/>):
    /// items inside the libraries and, with a user, visible to that user as <c>GetItemById&lt;T&gt;(id, user)</c> sees
    /// them, and not owned. One <c>GetItemList</c> with <c>ItemIds</c> and <c>TopParentIds</c> per <see cref="IdsPerQuery"/>
    /// ids, with <c>GroupByPresentationUniqueKey = false</c> (or the other members of a set would be dropped): the SQL
    /// filters of <c>SetUser</c> and the item's own <c>IsVisible(user)</c>. Then, with a user, every parent of each item must
    /// pass <c>IsVisible(user, true)</c>, as core's <c>IsVisibleStandaloneInternal</c> asks (the remarks' "Parents"); its
    /// last step, the item's collection folders among the user's, is the <c>TopParentIds</c> filter here. Each parent is
    /// looked up (<c>GetItemById</c>, core's cached item) and checked once per call, its answer kept for every item under
    /// it: the episodes of one season cost one check each of the season, the series and the library's folders. Never
    /// <c>GetItemById&lt;T&gt;(id, null)</c>, which checks nothing without a user.
    /// </summary>
    /// <remarks>
    /// The parents are walked here rather than by calling <c>IsVisibleStandalone(user)</c> on each item, which would check
    /// the item's own <c>IsVisible</c> a second time, walk the same parents again for every item, and for every item read
    /// its collection folders and the user root folder's children (<c>GetChildren(user, true)</c>), the libraries this
    /// list already holds.
    /// <para><b>Owned items</b> (<paramref name="owned"/>): left out by default (<see cref="OwnedItems.Exclude"/>, never a result
    /// of their own). With <see cref="OwnedItems.FollowOwner"/>, for ids a client sent: an owned item the query
    /// didn't return (it has no top parent) is eligible when, with a user, it passes its own <c>IsVisible(user)</c> and
    /// parent check, as do the owned items between it and its first owner that isn't owned, and that owner is eligible by
    /// the rules above (in the libraries, visible, its parents visible): one more query for the owners not among the ids.
    /// That is stricter than core's <c>GetItemById&lt;T&gt;(id, user)</c>, which checks only the owner's library, never its
    /// rating: a trailer of a movie the user may not see is never eligible. An owner chain longer than
    /// <see cref="MaxParentDepth"/>, or a cycle, is not eligible.</para>
    /// </remarks>
    /// <param name="user">The user, or null.</param>
    /// <param name="libraries">The caller's allowed libraries (already resolved with <see cref="ResolveLibraries"/>).</param>
    /// <param name="ids">The ids to check.</param>
    /// <param name="owned">Whether owned items count: <see cref="OwnedItems.Exclude"/> (the default) or
    /// <see cref="OwnedItems.FollowOwner"/>.</param>
    /// <returns>The eligible ids.</returns>
    public IReadOnlySet<Guid> EligibleIds(User? user, IReadOnlyCollection<CollectionFolder> libraries, IReadOnlyCollection<Guid> ids, OwnedItems owned = OwnedItems.Exclude)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(ids);
        var eligible = new HashSet<Guid>();
        var topParents = TopParentIds(libraries);
        var wanted = ids.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (topParents.Length == 0 || wanted.Length == 0)
        {
            return eligible;
        }

        var parents = new Dictionary<Guid, bool>();
        AddEligible(user, topParents, wanted, parents, eligible);
        if (owned == OwnedItems.FollowOwner)
        {
            AddOwnedFollowingTheirOwners(user, topParents, wanted, parents, eligible);
        }

        return eligible;
    }

    // The batch: one GetItemList with ItemIds + TopParentIds per IdsPerQuery ids; an item counts when it isn't owned and,
    // with a user, its parents pass (the remarks' "Parents").
    private void AddEligible(User? user, Guid[] topParents, Guid[] ids, Dictionary<Guid, bool> parents, HashSet<Guid> eligible)
    {
        foreach (var chunk in ids.Chunk(IdsPerQuery))
        {
            var query = user is null ? new InternalItemsQuery() : new InternalItemsQuery(user);
            query.ItemIds = chunk;
            query.TopParentIds = topParents;
            query.GroupByPresentationUniqueKey = false;
            query.DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false };
            KitItemQueries.IncludeEveryVersion(query);
            foreach (var item in _library.GetItemList(query))
            {
                if (item is not null && !IsOwned(item) && (user is null || ParentsVisible(item, user, parents)))
                {
                    eligible.Add(item.Id);
                }
            }
        }
    }

    // OwnedItems.FollowOwner: each id the batch didn't keep that names an owned item is eligible when the owned items on its
    // owner chain pass their own checks and the chain's first owner that isn't owned is eligible. The owners among the ids
    // are already decided; the others are asked in one more batch.
    private void AddOwnedFollowingTheirOwners(User? user, Guid[] topParents, Guid[] wanted, Dictionary<Guid, bool> parents, HashSet<Guid> eligible)
    {
        var asked = wanted.ToHashSet();
        var rootOf = new Dictionary<Guid, Guid>();
        foreach (var id in wanted)
        {
            if (!eligible.Contains(id) && _library.GetItemById(id) is { } item && IsOwned(item) && RootOwner(item, user, parents) is { } root)
            {
                rootOf[id] = root;
            }
        }

        if (rootOf.Count == 0)
        {
            return;
        }

        var owners = new HashSet<Guid>();
        var unasked = rootOf.Values.Where(o => !asked.Contains(o)).Distinct().ToArray();
        if (unasked.Length > 0)
        {
            AddEligible(user, topParents, unasked, parents, owners);
        }

        foreach (var (id, root) in rootOf)
        {
            if (eligible.Contains(root) || owners.Contains(root))
            {
                eligible.Add(id);
            }
        }
    }

    // The first owner up an owned item's OwnerId chain that isn't owned itself, or null when the chain is broken (an owner
    // that no longer exists), too long or a cycle, or, with a user, an owned item on it fails its own check (its IsVisible
    // and its parents, which owned items rarely have).
    private Guid? RootOwner(BaseItem item, User? user, Dictionary<Guid, bool> parents)
    {
        var seen = new HashSet<Guid>();
        var current = item;
        while (IsOwned(current))
        {
            if (!seen.Add(current.Id) || seen.Count > MaxParentDepth)
            {
                return null;
            }

            if (user is not null && (!_isItemVisible(current, user) || !ParentsVisible(current, user, parents)))
            {
                return null;
            }

            if (_library.GetItemById(current.OwnerId) is not { } owner)
            {
                return null;
            }

            current = owner;
        }

        return seen.Contains(current.Id) ? null : current.Id;
    }

    // Visibility: an id that names nothing is Unknown; one GetItemById<BaseItem>(id, user) refuses is Hidden; a view is
    // judged by its display parent, else its parent, a few levels at most.
    private ItemVisibility VisibilityOf(Guid id, User user, int depth)
    {
        if (id == Guid.Empty || _library.GetItemById(id) is not { } item)
        {
            return ItemVisibility.Unknown;
        }

        if (_library.GetItemById<BaseItem>(id, user) is null)
        {
            return ItemVisibility.Hidden;
        }

        if (item is UserView view && depth < MaxViewDepth)
        {
            var parent = view.DisplayParentId != Guid.Empty ? view.DisplayParentId : view.ParentId;
            if (parent != Guid.Empty && parent != id && VisibilityOf(parent, user, depth + 1) == ItemVisibility.Hidden)
            {
                return ItemVisibility.Hidden;
            }
        }

        return ItemVisibility.Visible;
    }

    private static IReadOnlyList<CollectionFolder> ServerLibraries(ILibraryManager library, User? user)
    {
        var root = library.GetUserRootFolder();
        IEnumerable<BaseItem> children = user is null ? root.Children : root.GetChildren(user, true);
        return children.OfType<CollectionFolder>().ToList();
    }

    // Whether every parent of the item passes the parent check, as core's IsVisibleStandaloneInternal asks (its GetParents:
    // each ParentId through GetItemById, up to the first that is empty or names nothing). known holds, per parent id, whether
    // that parent and all of its own parents pass, so each parent is looked up and checked once per EligibleIds call.
    private bool ParentsVisible(BaseItem item, User user, Dictionary<Guid, bool> known)
    {
        var walked = new List<Guid>();
        var parentId = item.ParentId;
        var visible = true;
        while (parentId != Guid.Empty)
        {
            if (known.TryGetValue(parentId, out var answer))
            {
                visible = answer;
                break;
            }

            if (walked.Count >= MaxParentDepth || walked.Contains(parentId) || parentId == item.Id)
            {
                visible = false;
                break;
            }

            if (_library.GetItemById(parentId) is not { } parent)
            {
                break;
            }

            walked.Add(parentId);
            if (!_isVisible(parent, user))
            {
                visible = false;
                break;
            }

            parentId = parent.ParentId;
        }

        // Every parent walked has the same answer: all of them and theirs pass, or one at or above each of them fails.
        foreach (var id in walked)
        {
            known[id] = visible;
        }

        return visible;
    }

    // The libraries an id stands for: a library itself, a named view's display parent or parent, a grouped view's grouped
    // libraries of its type. Items that are none of these stand for no library.
    private IEnumerable<Guid> LibraryIdsOf(Guid id, User? user, int depth)
    {
        if (depth > 4 || id == Guid.Empty || _library.GetItemById(id) is not { } item)
        {
            return [];
        }

        switch (item)
        {
            case CollectionFolder library:
                return [library.Id];
            case UserView view when view.DisplayParentId != Guid.Empty:
                return LibraryIdsOf(view.DisplayParentId, user, depth + 1);
            case UserView view when view.ParentId != Guid.Empty:
                return LibraryIdsOf(view.ParentId, user, depth + 1);
            case UserView view when user is not null && view.ViewType is { } type && UserView.IsEligibleForGrouping(type):
                return VisibleLibraries(user)
                    .Where(l => (l.CollectionType is null || l.CollectionType == type) && user.IsFolderGrouped(l.Id))
                    .Select(l => l.Id)
                    .ToList();
            default:
                return [];
        }
    }
}
