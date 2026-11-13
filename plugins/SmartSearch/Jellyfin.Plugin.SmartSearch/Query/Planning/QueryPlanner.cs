using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// Turns a validated query into a plan: the libraries the caller may search, the
/// user-aware base query, the pushdown of every rule that can be pushed, the residual and its re-check, the path and the
/// candidates' <c>DtoOptions</c>. <b>Pushdown never loses an item</b>: every pushed filter is Exact or a Superset of its
/// rule, an <c>Any</c> group pushes only what all its branches imply, a negated group pushes nothing, and every Superset
/// rule stays in the residual.
/// </summary>
/// <remarks>
/// <para><b>Access</b> is never wider than core's <c>/Items</c> for the same user: <c>new InternalItemsQuery(user)</c> (plan
/// 08: <c>SetUser</c> with the evaluation user) for the parental limit, the blocked and allowed tags, with
/// <c>BlockUnratedItems</c> set to all of the user's blocked unrated kinds (<c>/Items</c> puts back the <c>Other</c> kind
/// that <c>SetUser</c> leaves out, <c>ItemsController.cs:431-435</c>); explicit <c>TopParentIds</c> from the libraries the
/// user can open (so core's <c>AddUserToQuery</c> never runs); never <c>HasOwnerId</c>; <c>IsVirtualItem = false</c> unless
/// the scope includes missing items; <c>GroupByPresentationUniqueKey = false</c> except on the Fast path.</para>
/// </remarks>
internal sealed class QueryPlanner
{
    /// <summary>The largest page the Fast path serves (<c>MaxPageSize</c>): a larger <c>Limit</c> takes the Residual path.</summary>
    public const int PageWindow = 200;

    private static readonly Dictionary<string, ItemSortBy> CoreSortKeys = new(StringComparer.Ordinal)
    {
        ["SortName"] = ItemSortBy.SortName,
        ["ProductionYear"] = ItemSortBy.ProductionYear,
        ["PremiereDate"] = ItemSortBy.PremiereDate,
        ["DateCreated"] = ItemSortBy.DateCreated,
        ["CommunityRating"] = ItemSortBy.CommunityRating,
        ["CriticRating"] = ItemSortBy.CriticRating,
        ["Runtime"] = ItemSortBy.Runtime,
        ["OfficialRating"] = ItemSortBy.OfficialRating,
        ["VideoBitRate"] = ItemSortBy.VideoBitRate,
        ["DatePlayed"] = ItemSortBy.DatePlayed,
        ["PlayCount"] = ItemSortBy.PlayCount
    };

    // Core orders by these through query.User!.Id (OrderMapper.cs:33-34): the Residual path sorts by them in memory.
    private static readonly HashSet<string> PerUserSortKeys = new(StringComparer.Ordinal) { "DatePlayed", "PlayCount" };

    // The kinds whose IsVisible(user) checks more than SQL's access in 10.11.5: BoxSet (BoxSet.cs:156-182: the user must
    // open a library one of its children is in) and the folder kinds (Folder.cs, CollectionFolder.cs, Playlist.cs,
    // Channel.cs), which aren't searchable but are listed so a new scope kind can't slip through. /Items post-filters a
    // BoxSet-only query in memory and counts what's left (ItemsController -> Folder.QueryWithPostFiltering2), so a Fast
    // page and count of these kinds would be wider than /Items: they take the Residual path.
    private static readonly HashSet<BaseItemKind> OwnVisibilityKinds = [BaseItemKind.BoxSet, BaseItemKind.Folder, BaseItemKind.CollectionFolder, BaseItemKind.UserRootFolder, BaseItemKind.AggregateFolder, BaseItemKind.Playlist, BaseItemKind.PlaylistsFolder, BaseItemKind.Channel, BaseItemKind.ManualPlaylistsFolder];

    private readonly FieldRegistry _registry;
    private readonly LibraryAccess _access;
    private readonly ILibraryManager _library;
    private readonly IPushdownFacts _facts;
    private readonly IScopedLookups _lookups;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryPlanner"/> class.
    /// </summary>
    /// <param name="registry">The field registry.</param>
    /// <param name="access">The kit's library access.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="facts">What the planner reads from core's database (stale clean forms, user data, episodes' series names).</param>
    /// <param name="lookups">What it looks up in the caller's scope (series names, studios); <c>null</c> reads them for
    /// each plan (<see cref="LoadedLookups"/>).</param>
    public QueryPlanner(FieldRegistry registry, LibraryAccess access, ILibraryManager library, IPushdownFacts facts, IScopedLookups? lookups = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(facts);
        _registry = registry;
        _access = access;
        _library = library;
        _facts = facts;
        _lookups = lookups ?? new LoadedLookups(library);
    }

    /// <summary>Plans a query, without a candidate budget or a cancellation token.</summary>
    /// <param name="request">The query (validated and migrated), the user and the purpose.</param>
    /// <param name="environment">The evaluation environment (now, time zone, rating scores); the plan's has its library membership.</param>
    /// <returns>The plan.</returns>
    public QueryPlan Plan(PlanRequest request, EvaluationEnvironment environment) => Plan(request, environment, null, CancellationToken.None);

    /// <summary>Plans a query.</summary>
    /// <param name="request">The query (validated and migrated), the user and the purpose.</param>
    /// <param name="environment">The evaluation environment (now, time zone, rating scores); the plan's has its library membership.</param>
    /// <param name="budget">The evaluation's candidate budget, which the item sets of <c>Library</c> rules count against
    /// (they are loaded while the residual is compiled), or <c>null</c> for none.</param>
    /// <param name="cancellationToken">The evaluation's cancellation token: checked first, and before each of the
    /// planner's database reads (the facts and the library membership).</param>
    /// <returns>The plan.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    /// <exception cref="Execution.SmartSearchRefusedException">A <c>Library</c> rule's items are past the budget (<c>QueryTooBroad</c>).</exception>
    public QueryPlan Plan(PlanRequest request, EvaluationEnvironment environment, CandidateBudget? budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(environment);
        cancellationToken.ThrowIfCancellationRequested();
        var query = request.Query;
        var user = request.User;
        var scope = query.Scope;

        // The scope's kinds, and the requested libraries the caller can open (boxsets libraries only for BoxSet).
        var scopeKinds = QueryLimits.ItemKindOrder.Where(k => scope.ItemKinds.Contains(k)).ToList();
        var requested = scope.LibraryIds ?? [];
        var libraries = (requested.Count == 0 ? _access.VisibleLibraries(user) : _access.ResolveLibraries(requested, user))
            .Where(l => l.CollectionType != CollectionType.boxsets || scopeKinds.Contains(BaseItemKind.BoxSet))
            .Where(l => request.AllowedLibraries is null || request.AllowedLibraries.Contains(l.Id))
            .ToList();
        var allowedFolders = LibraryAccess.TopParentIds(libraries);
        var membership = new LibraryMembership(_access, _library, user, scopeKinds, scope.IncludeMissing, budget, cancellationToken);
        var people = new PeopleRoleResolver(_library, user, scopeKinds, allowedFolders, scope.IncludeMissing, budget, cancellationToken);
        var planEnvironment = new EvaluationEnvironment
        {
            NowUtc = environment.NowUtc,
            TimeZone = environment.TimeZone,
            RatingScore = environment.RatingScore,
            LibraryMembers = membership.Members,
            PeopleMembers = people.Members
        };

        // The rules, which are mandatory, and the kinds the query can return (mandatory ItemType rules narrow them).
        var rules = new List<PlannedRule>();
        Collect(query.Where, "Where", true, false, rules);
        var kinds = scopeKinds.ToHashSet();
        foreach (var (rule, field, _, mandatory, _) in rules)
        {
            if (mandatory && field.Id == "ItemType")
            {
                var named = RuleValues.ItemKinds(rule);
                kinds.RemoveWhere(k => rule.Op == SmartSearchQueryOperator.AnyOf ? !named.Contains(k) : named.Contains(k));
            }
        }

        var planning = new PlanningScope(kinds, allowedFolders, membership, user, scope.IncludeMissing, people);
        var mapper = new PushdownMapper(planning, _facts, _lookups, _library, _registry, planEnvironment, cancellationToken);
        var mappings = new Dictionary<SmartSearchQueryNode, RuleMapping>(ReferenceEqualityComparer.Instance);
        foreach (var (rule, field, _, _, negated) in rules)
        {
            // Nothing under a negated group is pushed, so it isn't mapped (no studio lookups or series reads for it); with
            // the scope alone nothing is.
            mappings[rule] = request.ScopeOnly ? RuleMapping.Declined("the scope alone was asked for")
                : negated ? RuleMapping.Declined("under a negated group")
                : mapper.Map(rule, field);
        }

        // The base, then the tree's pushdown: everything SQL is asked must hold of every match.
        var pushdown = PushdownSet.Of(
                (PushProperty.IncludeItemTypes, new InSetConstraint<BaseItemKind>(scopeKinds, EqualityComparer<BaseItemKind>.Default, singleValued: true)),
                (PushProperty.TopParentIds, new InSetConstraint<Guid>(allowedFolders, EqualityComparer<Guid>.Default, singleValued: true)))
            .And(Push(query.Where, mappings));

        // A rule leaves the residual only when it is mandatory, mapped Exact, and the final query implies its mapping.
        var implied = new HashSet<SmartSearchQueryNode>(ReferenceEqualityComparer.Instance);
        var steps = new List<PushdownStep>();
        foreach (var (rule, field, path, mandatory, _) in rules)
        {
            var mapping = mappings[rule];
            var leaves = !request.RulesInMemory && mandatory && mapping.Exactness == PushdownExactness.Exact && pushdown.Implies(mapping.Set);
            if (leaves)
            {
                implied.Add(rule);
            }

            steps.Add(new PushdownStep(path, field.Id, RuleValues.Op(rule), mandatory, mapping.Exactness, mapping.Set.ToString(), mapping.Reason, leaves));
        }

        var residual = Prune(query.Where, implied);
        var residualRules = new List<PlannedRule>();
        Collect(residual, "Where", true, false, residualRules);
        var sorts = query.Sort ?? [];

        // The path. Fast needs a user: core groups merged versions only with one (BaseItemRepository.cs:1527,
        // EnableGroupByPresentationUniqueKey), so without one a Fast page would list and count every version of a merged
        // item, and core's per-user sorts throw. It needs kinds whose visibility SQL decides (above).
        var finalKinds = pushdown[PushProperty.IncludeItemTypes] is InSetConstraint<BaseItemKind> pushedKinds ? pushedKinds.Values : scopeKinds;
        var fast = request.Purpose == PlanPurpose.Search
            && !request.ScopeOnly
            && user is not null
            && !finalKinds.Any(OwnVisibilityKinds.Contains)
            && residualRules.Count == 0
            && sorts.All(s => _registry.FindSortKey(s.Key.Trim()) is { } key && CoreSortKeys.ContainsKey(key))
            && (query.Limit is null || query.Limit <= PageWindow);

        // The candidates' DtoOptions.
        var perUserSort = !fast && sorts.Any(s => _registry.FindSortKey(s.Key.Trim()) is { } key && PerUserSortKeys.Contains(key));
        var dtoOptions = new DtoOptions(false)
        {
            EnableImages = false,
            EnableUserData = residualRules.Any(r => r.Field.PerUser) || perUserSort,
            Fields = residualRules.Any(r => r.Field.NeedsProviderIds) ? [ItemFields.ProviderIds] : []
        };

        var residualTest = RuleTree.Compile(residual, _registry, planEnvironment);
        bool Recheck(EvaluationRow row) => !LibraryAccess.IsOwned(row.Item) && residualTest(row);
        InternalItemsQuery NewQuery() => CreateAccessQuery(user, request.Purpose);

        if (libraries.Count == 0 || scopeKinds.Count == 0 || pushdown.Impossible)
        {
            return new QueryPlan(QueryPlanPath.Empty, null, libraries, [], residual, steps, Recheck, dtoOptions, NewQuery, planEnvironment);
        }

        var candidates = NewQuery();
        pushdown.Apply(candidates);
        candidates.IsVirtualItem = scope.IncludeMissing ? null : false;
        candidates.DtoOptions = dtoOptions;
        if (fast)
        {
            // The one query that groups merged versions in SQL; the engine sets StartIndex and Limit.
            candidates.GroupByPresentationUniqueKey = true;
            candidates.OrderBy = sorts.Count == 0
                ? [(ItemSortBy.SortName, SortOrder.Ascending)]
                : sorts.Select(s => (CoreSortKeys[_registry.FindSortKey(s.Key.Trim())!], s.Order)).ToList();
        }

        return new QueryPlan(fast ? QueryPlanPath.Fast : QueryPlanPath.Residual, candidates, libraries, candidates.IncludeItemTypes, residual, steps, Recheck, dtoOptions, NewQuery, planEnvironment);
    }

    /// <summary>
    /// Creates a query with a user's access: a search's <c>new InternalItemsQuery(user)</c>, the auto-collections'
    /// <c>SetUser</c>, then <c>BlockUnratedItems</c> as <c>/Items</c> sets it, and <c>GroupByPresentationUniqueKey = false</c>.
    /// Alternate versions are returned as separate items, as on 10.11 (Jellyfin 12 hides them unless asked), so a rule only
    /// a secondary version meets still finds the set, and the version collapse sees every member.
    /// </summary>
    /// <param name="user">The user, or <c>null</c>.</param>
    /// <param name="purpose">The purpose.</param>
    /// <returns>The query.</returns>
    public static InternalItemsQuery CreateAccessQuery(User? user, PlanPurpose purpose)
    {
        InternalItemsQuery query;
        if (purpose == PlanPurpose.Search)
        {
            query = user is null ? new InternalItemsQuery() : new InternalItemsQuery(user);
        }
        else
        {
            query = new InternalItemsQuery();
            if (user is not null)
            {
                query.SetUser(user);
            }
        }

        if (user is not null)
        {
            query.BlockUnratedItems = user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems);
        }

        query.GroupByPresentationUniqueKey = false;
        return KitItemQueries.IncludeEveryVersion(query);
    }

    // The rules with their paths; mandatory while every group above is an All that isn't negated.
    private void Collect(SmartSearchQueryNode node, string path, bool mandatory, bool negated, List<PlannedRule> rules)
    {
        if (node.IsGroup)
        {
            var childrenMandatory = mandatory && !node.Negate && node.Match != SmartSearchMatchMode.Any;
            for (var i = 0; i < node.Children!.Count; i++)
            {
                Collect(node.Children[i], path + ".Children[" + i.ToString(CultureInfo.InvariantCulture) + "]", childrenMandatory, negated || node.Negate, rules);
            }

            return;
        }

        rules.Add(new PlannedRule(node, RuleTree.FieldOf(node, _registry), path, mandatory, negated));
    }

    // The pushdown of a subtree: an All is the conjunction of its children, an Any what all its branches imply, a negated
    // group nothing.
    private static PushdownSet Push(SmartSearchQueryNode node, Dictionary<SmartSearchQueryNode, RuleMapping> mappings)
    {
        if (!node.IsGroup)
        {
            var mapping = mappings[node];
            return mapping.Exactness == PushdownExactness.Declined ? PushdownSet.None : mapping.Set;
        }

        if (node.Negate || node.Children!.Count == 0)
        {
            return PushdownSet.None;
        }

        var children = node.Children.Select(c => Push(c, mappings)).ToList();
        return node.Match == SmartSearchMatchMode.Any
            ? children.Skip(1).Aggregate(children[0], (acc, next) => acc.Or(next))
            : children.Aggregate(PushdownSet.None, (acc, next) => acc.And(next));
    }

    // The tree without the implied rules (they are all on the mandatory All chain from the root). Inside an All group, a
    // child All group left empty holds for every item and goes too; never inside an Any group, where it decides.
    private static SmartSearchQueryNode Prune(SmartSearchQueryNode node, HashSet<SmartSearchQueryNode> implied)
    {
        if (!node.IsGroup || implied.Count == 0)
        {
            return node;
        }

        var children = node.Children!.Where(c => !implied.Contains(c)).Select(c => Prune(c, implied));
        if (node.Match != SmartSearchMatchMode.Any)
        {
            children = children.Where(c => !(c.IsGroup && c.Children!.Count == 0 && !c.Negate && c.Match != SmartSearchMatchMode.Any));
        }

        return new SmartSearchQueryNode { Match = node.Match, Negate = node.Negate, Children = children.ToList() };
    }

    // One rule of the query: its field, its place, whether it is mandatory and whether a negated group is above it.
    private readonly record struct PlannedRule(SmartSearchQueryNode Rule, FieldDescriptor Field, string Path, bool Mandatory, bool Negated);
}
