using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// The <c>LibraryMembershipResolver</c>: which items a <c>Library</c> rule's libraries hold, for the
/// evaluator (<see cref="Evaluation.EvaluationEnvironment.LibraryMembers"/>) and the planner. The requested ids are first
/// intersected with the libraries the caller can open (<see cref="LibraryAccess.ResolveLibraries"/>), so
/// <c>Library AnyOf [a hidden library]</c> is false on every item, even one in a folder that library shares with a visible
/// one: a rule reveals nothing about libraries the caller can't see. Members are the items whose top parent is one of the
/// libraries' physical folders, of the query's kinds, that the caller's access lets through (<c>SetUser</c>: the parental
/// limit, blocked and allowed tags, blocked unrated kinds; ungrouped), so the budget and a <c>QueryTooBroad</c> count
/// only items the caller can see; one <c>GetItemIds</c> per set of libraries, cached for the plan.
/// Each set counts against the evaluation's <see cref="CandidateBudget"/> (<c>MaxCandidates</c>), and a
/// load checks the evaluation's cancellation token first (a statement in flight isn't interrupted: core's item queries
/// take no token).
/// </summary>
internal sealed class LibraryMembership
{
    private readonly LibraryAccess _access;
    private readonly ILibraryManager _library;
    private readonly User? _user;
    private readonly BaseItemKind[] _kinds;
    private readonly bool _includeMissing;
    private readonly CandidateBudget? _budget;
    private readonly CancellationToken _cancellationToken;
    private readonly ConcurrentDictionary<string, IReadOnlySet<Guid>> _members = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryMembership"/> class.
    /// </summary>
    /// <param name="access">The kit's library access.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="user">The caller, or <c>null</c> for every library.</param>
    /// <param name="kinds">The query's item kinds.</param>
    /// <param name="includeMissing">Whether missing (virtual) items count.</param>
    /// <param name="budget">The evaluation's candidate budget, or <c>null</c> for none.</param>
    /// <param name="cancellationToken">The evaluation's cancellation token.</param>
    public LibraryMembership(LibraryAccess access, ILibraryManager library, User? user, IEnumerable<BaseItemKind> kinds, bool includeMissing, CandidateBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(kinds);
        _access = access;
        _library = library;
        _user = user;
        _kinds = kinds.ToArray();
        _includeMissing = includeMissing;
        _budget = budget;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Gets the physical folders of the requested libraries the caller can open.</summary>
    /// <param name="libraryIds">The rule's library ids.</param>
    /// <returns>The folder ids; empty when none is left.</returns>
    public Guid[] FoldersOf(IReadOnlyCollection<Guid> libraryIds)
    {
        ArgumentNullException.ThrowIfNull(libraryIds);

        // ResolveLibraries reads an empty request as "every library"; a rule always names some, and none is none.
        var asked = libraryIds.Where(id => id != Guid.Empty).Distinct().ToList();
        return asked.Count == 0 ? [] : LibraryAccess.TopParentIds(_access.ResolveLibraries(asked, _user));
    }

    /// <summary>Gets the items in the requested libraries the caller can open (the evaluator's library function).</summary>
    /// <param name="libraryIds">The rule's library ids.</param>
    /// <returns>The member ids.</returns>
    public IReadOnlySet<Guid> Members(IReadOnlyCollection<Guid> libraryIds)
    {
        ArgumentNullException.ThrowIfNull(libraryIds);
        var key = string.Join(",", libraryIds.Distinct().Order().Select(id => id.ToString("N")));
        return _members.GetOrAdd(key, _ => Load(libraryIds));
    }

    private HashSet<Guid> Load(IReadOnlyCollection<Guid> libraryIds)
    {
        var folders = FoldersOf(libraryIds);
        if (folders.Length == 0 || _kinds.Length == 0)
        {
            return [];
        }

        _cancellationToken.ThrowIfCancellationRequested();
        var query = Query(folders);
        query.Limit = _budget?.NextLimit;
        var ids = _library.GetItemIds(query);
        _budget?.Take(ids.Count, () => _library.GetCount(Query(folders)));
        return ids.ToHashSet();
    }

    // Explicit TopParentIds (never empty) and the caller's access (the candidates carry the same, so nothing they hold is
    // lost), ungrouped.
    private InternalItemsQuery Query(Guid[] folders)
    {
        var query = QueryPlanner.CreateAccessQuery(_user, PlanPurpose.EvaluateAll);
        query.TopParentIds = folders;
        query.IncludeItemTypes = _kinds;
        query.IsVirtualItem = _includeMissing ? null : false;
        query.GroupByPresentationUniqueKey = false;
        query.DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false };
        return query;
    }
}
