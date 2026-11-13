using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// The <c>PeopleRoleResolver</c>: which items a <c>People</c> rule's person is credited on, in a role or in any, for the
/// evaluator (<see cref="Evaluation.EvaluationEnvironment.PeopleMembers"/>) and the planner's pushdown.
/// </summary>
/// <remarks>
/// <para><b>The person</b> is the <c>Person</c> item the reference's id names; when that id no longer is one (people were
/// rebuilt, or the database came from another server), the person of the reference's name (<c>GetPerson</c>), so a saved
/// query keeps working; neither is a person credited on nothing. The stored id isn't rewritten.</para>
/// <para><b>The items</b> come from one <c>GetItemIds</c> with <c>PersonIds = [person]</c>, which core matches through the
/// credits' names (<c>BaseItemRepository</c>: the <c>Person</c> item's name joined to <c>Peoples.Name</c>), in the plan's
/// libraries (their physical folders, never empty), of its kinds, with the caller's access (<c>SetUser</c>, ungrouped), so
/// the budget and a <c>QueryTooBroad</c> count only items the caller can see, as for <see cref="LibraryMembership"/>.
/// The ids only ever narrow candidates that carry the same scope and access.</para>
/// <para><b>The role</b>: Jellyfin 12 filters the credits' type in that same query (<c>PersonTypes</c> beside
/// <c>PersonIds</c>, on the same credit), exactly. Jellyfin 10.11 ignores <c>PersonTypes</c> in item queries, so there
/// the person's items in any role are read, and the role is checked lazily, only for an item the rule is asked about
/// (a candidate that the other pushed filters and rules let through): <c>GetPeople(new InternalPeopleQuery([role], [])
/// { ItemId = id })</c>, the item kept when a credit of that type has the person's name (ordinal, as the SQL join
/// compares), remembered per item for the plan, the evaluation's cancellation token checked before each.</para>
/// <para>Each (person, role) is loaded once per plan, and counts against the evaluation's <see cref="CandidateBudget"/>,
/// as a <c>Library</c> rule's items do. The 10.11 supersets for a role don't (they hold credits the rule doesn't count,
/// and only narrow), but they have a bound of their own: all of them in one evaluation hold at most <c>MaxCandidates</c>
/// ids together (every rule and person), past which the evaluation is refused (<c>QueryTooBroad</c>, with the count of
/// the superset that went past it added to those loaded before).</para>
/// </remarks>
internal sealed class PeopleRoleResolver
{
    private readonly ILibraryManager _library;
    private readonly User? _user;
    private readonly BaseItemKind[] _kinds;
    private readonly Guid[] _folders;
    private readonly bool _includeMissing;
    private readonly CandidateBudget? _budget;
    private readonly CancellationToken _cancellationToken;
    private readonly ConcurrentDictionary<(Guid Id, string? Name), Person?> _people = new();
    private readonly ConcurrentDictionary<(Guid Id, string? Name, string? Role), Func<Guid, bool>> _members = new();
#if !JELLYFIN_12
    private readonly ConcurrentDictionary<(Guid Item, Guid Person, string Role), bool> _roleChecks = new();
    private int _supersetIds;
#endif

    /// <summary>
    /// Initializes a new instance of the <see cref="PeopleRoleResolver"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="user">The caller, or <c>null</c> for no user's access.</param>
    /// <param name="kinds">The query's item kinds.</param>
    /// <param name="folders">The physical folders of the libraries the plan searches.</param>
    /// <param name="includeMissing">Whether missing (virtual) items count.</param>
    /// <param name="budget">The evaluation's candidate budget, or <c>null</c> for none.</param>
    /// <param name="cancellationToken">The evaluation's cancellation token.</param>
    public PeopleRoleResolver(ILibraryManager library, User? user, IEnumerable<BaseItemKind> kinds, IEnumerable<Guid> folders, bool includeMissing, CandidateBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(folders);
        _library = library;
        _user = user;
        _kinds = kinds.ToArray();
        _folders = folders.ToArray();
        _includeMissing = includeMissing;
        _budget = budget;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Finds the person a reference names: by its id, else by its name.</summary>
    /// <param name="id">The reference's id.</param>
    /// <param name="name">The reference's name, or <c>null</c>.</param>
    /// <returns>The person, or <c>null</c> when the server has none of that id or name.</returns>
    public Person? Resolve(Guid id, string? name)
    {
        var key = (id, string.IsNullOrWhiteSpace(name) ? null : name.Trim());
        return _people.GetOrAdd(key, k =>
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (k.Id != Guid.Empty && _library.GetItemById(k.Id) is Person person)
            {
                return person;
            }

            return k.Name is null ? null : _library.GetPerson(k.Name);
        });
    }

    /// <summary>Gets the items a reference's person is credited on in the role (the evaluator's people function).</summary>
    /// <param name="id">The reference's id.</param>
    /// <param name="name">The reference's name, or <c>null</c>.</param>
    /// <param name="role">The role (a <see cref="Fields.FieldRegistry.PersonRoles"/> name), or <c>null</c> for any.</param>
    /// <returns>Whether an item id is credited.</returns>
    public Func<Guid, bool> Members(Guid id, string? name, string? role)
    {
        var key = (id, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), role);
        return _members.GetOrAdd(key, k => Load(k.Id, k.Name, k.Role));
    }

    /// <summary>Gets the query for a person's items: the plan's folders, kinds and access, and on Jellyfin 12 the role.</summary>
    /// <param name="personId">The person's item id.</param>
    /// <param name="role">The role, or <c>null</c> for any.</param>
    /// <returns>The query, or <c>null</c> when the plan has no folder or kind (nothing is credited there).</returns>
    public InternalItemsQuery? Query(Guid personId, string? role)
    {
        if (_folders.Length == 0 || _kinds.Length == 0)
        {
            return null;
        }

        var query = QueryPlanner.CreateAccessQuery(_user, PlanPurpose.EvaluateAll);
        query.TopParentIds = _folders;
        query.IncludeItemTypes = _kinds;
        query.IsVirtualItem = _includeMissing ? null : false;
        query.PersonIds = [personId];
#if JELLYFIN_12
        if (role is not null)
        {
            query.PersonTypes = [role];
        }
#endif
        query.GroupByPresentationUniqueKey = false;
        query.DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false };
        return query;
    }

    private Func<Guid, bool> Load(Guid id, string? name, string? role)
    {
        if (Resolve(id, name) is not { } person || Query(person.Id, role) is not { } query)
        {
            return static _ => false;
        }

        _cancellationToken.ThrowIfCancellationRequested();
#if !JELLYFIN_12
        if (role is not null)
        {
            // 10.11: the person's items in any role, not counted against the results' budget but bounded together with
            // every other superset of this evaluation; the role checked per item asked about.
            var before = Volatile.Read(ref _supersetIds);
            query.Limit = _budget is null ? null : Math.Max(0, _budget.Max - before) + 1;
            var anyRole = _library.GetItemIds(query).ToHashSet();
            var total = Interlocked.Add(ref _supersetIds, anyRole.Count);
            if (_budget is not null && total > _budget.Max)
            {
                var all = anyRole.Count == query.Limit ? _library.GetCount(Query(person.Id, role)!) : anyRole.Count;
                throw Execution.SmartSearchRefusedException.QueryTooBroad(total - anyRole.Count + all, _budget.Max, _budget.Kinds);
            }

            return item => anyRole.Contains(item) && _roleChecks.GetOrAdd((item, person.Id, role), k => HasRole(k.Item, person.Name, k.Role));
        }
#endif
        query.Limit = _budget?.NextLimit;
        var ids = _library.GetItemIds(query);
        _budget?.Take(ids.Count, () => _library.GetCount(Query(person.Id, role)!));
        var set = ids.ToHashSet();
        return set.Contains;
    }

#if !JELLYFIN_12
    // 10.11 ignores PersonTypes in item queries: the item's credits of the role, by the person's name.
    private bool HasRole(Guid itemId, string personName, string role)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return _library.GetPeople(new InternalPeopleQuery([role], []) { ItemId = itemId }).Any(p => string.Equals(p.Name, personName, StringComparison.Ordinal));
    }
#endif
}
