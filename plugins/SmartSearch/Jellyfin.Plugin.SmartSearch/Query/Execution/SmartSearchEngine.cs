using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>
/// The engine: plans a query (<see cref="QueryPlanner"/>), runs the plan's path, caches the
/// result and hydrates the page asked for. <b>Never a false negative, and never wider than core's <c>/Items</c> for the
/// same user</b>, in items or in counts.
/// </summary>
/// <remarks>
/// <para><b>Fast</b> (every rule pushed down as Exact, core sort keys, a page at most 200): SQL pages and counts
/// (<c>GetItemsResult</c> with the plan's grouping by presentation key, the count on the first page only, as
/// <c>/Items</c>' <c>TotalRecordCount</c>), then <see cref="PageHydrator"/>, which applies <c>IsVisible(user)</c> as
/// <c>DtoService.GetBaseItemDtos</c> does, so a page can be short. A page's row of merged versions stands for its set:
/// shown as the primary when the primary is eligible and a member the user may see matches. Among rows with equal sort keys SQL's order isn't defined (core's
/// <c>ApplyOrder</c> adds no tiebreak), so the Fast path's pages are only as stable as SQL's order: the decision
/// is to document this, not to add a sort key core doesn't add.</para>
/// <para><b>Residual</b>: <see cref="ResidualEvaluator"/> (one id list, batches, three-valued evaluation, the
/// <c>OwnerId</c> net, version collapse, presentation keys, a deterministic sort with an id tiebreak, the limit), cached
/// as ordered ids and paged from the cache.</para>
/// <para><b>Limits</b>: <c>MaxCandidates</c> (a <see cref="CandidateBudget"/> per evaluation: the id list and the
/// library sets), <c>SearchTimeoutSeconds</c> (linked with the caller's token, checked by the planner's reads, the
/// membership and between batches), and <see cref="EngineSlots"/>. Past a limit the search is refused
/// (<see cref="SmartSearchRefusedException"/>), never cut short.</para>
/// </remarks>
internal sealed class SmartSearchEngine : ISmartSearchEngine
{
    /// <summary>The warning of a page whose token had expired, so the result was evaluated again.</summary>
    public const string ReevaluatedWarning = "Reevaluated";

    /// <summary>The warning of a page whose cached ids no longer all load.</summary>
    public const string ItemsChangedWarning = "ItemsChanged";

    // Appended to the query's hash for a search with the scope alone pushed down (a hash is hex, so it can't clash).
    private const string ScopeOnlyHashSuffix = ":scope-only";

    private readonly QueryPlanner _planner;
    private readonly QueryCanonicalizer _canonicalizer;
    private readonly LibraryAccess _access;
    private readonly ILibraryManager _library;
    private readonly ResidualEvaluator _residual;
    private readonly PageHydrator _hydrator;
    private readonly ResultCache _cache;
    private readonly EngineSlots _slots;
    private readonly Func<EngineSettings> _settings;
    private readonly Func<EvaluationEnvironment> _environment;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmartSearchEngine"/> class.
    /// </summary>
    /// <param name="planner">The planner.</param>
    /// <param name="canonicalizer">The canonicalizer (the cache's query hash).</param>
    /// <param name="access">The kit's library access.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="residual">The Residual path.</param>
    /// <param name="hydrator">The page hydrator.</param>
    /// <param name="cache">The result cache.</param>
    /// <param name="slots">The admission control.</param>
    /// <param name="settings">The settings, read on each evaluation.</param>
    /// <param name="environment">A fresh evaluation environment (now, the time zone, the rating scores).</param>
    /// <param name="time">The clock (the timeout's timer and the elapsed time).</param>
    public SmartSearchEngine(
        QueryPlanner planner,
        QueryCanonicalizer canonicalizer,
        LibraryAccess access,
        ILibraryManager library,
        ResidualEvaluator residual,
        PageHydrator hydrator,
        ResultCache cache,
        EngineSlots slots,
        Func<EngineSettings> settings,
        Func<EvaluationEnvironment> environment,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(canonicalizer);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(residual);
        ArgumentNullException.ThrowIfNull(hydrator);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(time);
        _planner = planner;
        _canonicalizer = canonicalizer;
        _access = access;
        _library = library;
        _residual = residual;
        _hydrator = hydrator;
        _cache = cache;
        _slots = slots;
        _settings = settings;
        _environment = environment;
        _time = time;
    }

    /// <inheritdoc />
    public async Task<SmartSearchPage> SearchAsync(SmartSearchQueryDefinition query, User user, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(page);
        if (page.StartIndex < 0 || page.Limit < 1 || page.Limit > QueryPlanner.PageWindow)
        {
            throw Kit.Api.PluginException.Invalid("StartIndex must be 0 or more and Limit from 1 to " + QueryPlanner.PageWindow + ".");
        }

        var started = _time.GetTimestamp();
        using var slot = await _slots.EnterAsync(EvaluationPurpose.Search, user.Id, cancellationToken).ConfigureAwait(false);
        var settings = _settings();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.SearchTimeoutSeconds), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return Search(query, user, page, settings, started, linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw SmartSearchRefusedException.Timeout(settings.SearchTimeoutSeconds);
        }
    }

    /// <inheritdoc />
    public Task<EvaluationResult> EvaluateAllAsync(SmartSearchQueryDefinition query, User? evaluationUser, EvaluationPurpose purpose, CancellationToken cancellationToken)
        => EvaluateAllAsync(query, new EvaluationContext(purpose, evaluationUser, WholeSets: false), cancellationToken);

    /// <inheritdoc />
    public async Task<EvaluationResult> EvaluateAllAsync(SmartSearchQueryDefinition query, EvaluationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var evaluationUser = context.EvaluationUser;
        var started = _time.GetTimestamp();
        using var slot = await _slots.EnterAsync(context.Purpose, evaluationUser?.Id, cancellationToken).ConfigureAwait(false);
        var settings = _settings();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.SearchTimeoutSeconds), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var budget = new CandidateBudget(settings.MaxCandidates, query.Scope.ItemKinds);
            var environment = _environment();
            if (context.NowUtc is { } now)
            {
                environment = new EvaluationEnvironment { NowUtc = now, TimeZone = environment.TimeZone, RatingScore = environment.RatingScore, LibraryMembers = environment.LibraryMembers };
            }

            var request = new PlanRequest(query, evaluationUser, PlanPurpose.EvaluateAll, context.AllowedLibraries);
            var (plan, scopeIds) = PlanInMemory(request, environment, budget, context.ItemCache, linked.Token);
            if (plan is null)
            {
                budget = new CandidateBudget(settings.MaxCandidates, query.Scope.ItemKinds);
                plan = _planner.Plan(request, environment, budget, linked.Token);
            }

            if (plan.Path == QueryPlanPath.Empty)
            {
                return new EvaluationResult([], 0, plan, [], _time.GetElapsedTime(started));
            }

            // The planner never gives the auto-collections the Fast path (it needs the whole set, the collapse and the final IsVisible).
            var result = _residual.Run(plan, query, evaluationUser, budget, RandomNumberGenerator.GetInt32(int.MaxValue), linked.Token, context.RatingsViewerId ?? evaluationUser?.Id, context.WholeSets, context.ItemCache, scopeIds);
            return new EvaluationResult(result.OrderedIds, result.CandidateCount, plan, [], _time.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw SmartSearchRefusedException.Timeout(settings.SearchTimeoutSeconds);
        }
    }

    // Within a pass, a query over a scope the pass's cache can hold is checked in memory: the scope's ids (its kinds in its
    // libraries, with its access) are read once per pass and its items loaded once, and each definition's whole rule tree
    // runs over them; the plan is otherwise the usual one (its kinds, libraries and version collapse), so the result is the
    // same as with the rules pushed into the query, which the differential tests hold. A scope too large for the cache or
    // the candidate budget, or an empty plan, goes the usual way.
    private (QueryPlan? Plan, IReadOnlyList<Guid>? ScopeIds) PlanInMemory(PlanRequest request, EvaluationEnvironment environment, CandidateBudget budget, PassItemCache? cache, CancellationToken cancellationToken)
    {
        if (cache is not { MaxItems: > 0 })
        {
            return (null, null);
        }

        var plan = _planner.Plan(request with { RulesInMemory = true }, environment, budget, cancellationToken);
        if (plan.Path != QueryPlanPath.Residual || plan.CreateScopeQuery() is not { } scope)
        {
            return (plan, null);
        }

        var key = PassItemCache.CandidatesOf(request.User?.Id, scope.TopParentIds, scope.IncludeItemTypes, scope.IsVirtualItem);
        var ids = cache.Candidates(key, () =>
        {
            scope.Limit = (int)Math.Min((long)cache.MaxItems + 1, int.MaxValue);
            return _library.GetItemIds(scope);
        });

        return ids.Count <= cache.MaxItems && ids.Count < budget.NextLimit && cache.CanHold(ResidualEvaluator.ItemScopeOf(plan, request.User), ids) ? (plan, ids) : (null, null);
    }

    /// <inheritdoc />
    public async Task<QueryPlan> ExplainAsync(SmartSearchQueryDefinition query, User? user, PlanPurpose purpose, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Planning reads the database (a Library rule's items, the facts), so it takes a slot, the budget and the timeout
        // like an evaluation.
        using var slot = await _slots.EnterAsync(purpose == PlanPurpose.Search ? EvaluationPurpose.Search : EvaluationPurpose.Preview, user?.Id, cancellationToken).ConfigureAwait(false);
        var settings = _settings();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.SearchTimeoutSeconds), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var budget = new CandidateBudget(settings.MaxCandidates, query.Scope.ItemKinds);
            return _planner.Plan(new PlanRequest(query, user, purpose), _environment(), budget, linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw SmartSearchRefusedException.Timeout(settings.SearchTimeoutSeconds);
        }
    }

    private SmartSearchPage Search(SmartSearchQueryDefinition query, User user, PageRequest page, EngineSettings settings, long started, CancellationToken cancellationToken)
    {
        // A search with the scope alone pushed down is cached apart from the query's own result.
        var hash = _canonicalizer.Canonicalize(query).Hash + (page.ForceResidual ? ScopeOnlyHashSuffix : string.Empty);
        var fingerprint = ResultCache.Fingerprint(user, _access.VisibleLibraries(user).Select(l => l.Id));
        var warnings = new List<string>();
        CachedResult? entry = null;
        if (!string.IsNullOrEmpty(page.ResultToken))
        {
            entry = _cache.TryGet(page.ResultToken, user.Id, hash, fingerprint);
            if (entry is null)
            {
                warnings.Add(ReevaluatedWarning);
            }
        }

        var environment = _environment();
        entry ??= _cache.TryFind(user.Id, hash, ResultCache.Bucket(environment.NowUtc), fingerprint);
        entry ??= Evaluate(query, user, page.ForceResidual, hash, fingerprint, environment, settings, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        return entry.Path switch
        {
            QueryPlanPath.Residual => ServeResidual(entry, user, page, warnings, started, cancellationToken),
            QueryPlanPath.Fast => ServeFast(entry, query, user, page, warnings, started, cancellationToken),
            _ => new SmartSearchPage([], page.IncludeTotal ? 0 : null, page.StartIndex, entry.Token, entry.Path, warnings, _time.GetElapsedTime(started), false, 0, 0)
        };
    }

    private CachedResult Evaluate(SmartSearchQueryDefinition query, User user, bool scopeOnly, string hash, string fingerprint, EvaluationEnvironment environment, EngineSettings settings, CancellationToken cancellationToken)
    {
        // The generations before anything is read: an item or user event during the evaluation ends the entry.
        var generations = _cache.Snapshot(user.Id);
        var budget = new CandidateBudget(settings.MaxCandidates, query.Scope.ItemKinds);
        var plan = _planner.Plan(new PlanRequest(query, user, PlanPurpose.Search, ScopeOnly: scopeOnly), environment, budget, cancellationToken);
        var libraries = plan.Libraries.Select(l => l.Id).ToList();
        CachedResult entry;
        if (plan.Path == QueryPlanPath.Residual)
        {
            var result = _residual.Run(plan, query, user, budget, RandomNumberGenerator.GetInt32(int.MaxValue), cancellationToken);
            entry = NewEntry(user, hash, fingerprint, environment, plan, libraries, result.OrderedIds, null, result.OrderedIds.Count, result.CandidateCount);
        }
        else if (plan.Path == QueryPlanPath.Fast)
        {
            entry = NewEntry(user, hash, fingerprint, environment, plan, libraries, [], plan, null, 0);
        }
        else
        {
            entry = NewEntry(user, hash, fingerprint, environment, plan, libraries, [], null, 0, 0);
        }

        _cache.Store(entry, generations);
        return entry;
    }

    private static CachedResult NewEntry(User user, string hash, string fingerprint, EvaluationEnvironment environment, QueryPlan evaluated, IReadOnlyList<Guid> libraries, IReadOnlyList<Guid> ids, QueryPlan? plan, int? count, int candidates) => new()
    {
        UserId = user.Id,
        QueryHash = hash,
        Bucket = ResultCache.Bucket(environment.NowUtc),
        Fingerprint = fingerprint,
        Path = evaluated.Path,
        ReadsUserData = ReadsUserData(evaluated),
        NowUtc = environment.NowUtc,
        LibraryIds = libraries,
        OrderedIds = ids,
        Plan = plan,
        Count = count,
        CandidateCount = candidates
    };

    // Whether a plan's result depends on the user's data: a residual per-user rule or sort (the plan loads user data), or
    // one SQL is asked (a pushed IsPlayed, IsFavorite or IsResumable, a DatePlayed or PlayCount order).
    private static bool ReadsUserData(QueryPlan plan)
        => plan.NeedsUserData
            || (plan.CandidateQuery is { } q && (q.IsPlayed is not null || q.IsFavorite is not null || q.IsResumable is not null
                || q.OrderBy.Any(o => o.OrderBy is Jellyfin.Data.Enums.ItemSortBy.DatePlayed or Jellyfin.Data.Enums.ItemSortBy.PlayCount)));

    private SmartSearchPage ServeResidual(CachedResult entry, User user, PageRequest page, List<string> warnings, long started, CancellationToken cancellationToken)
    {
        var ids = entry.OrderedIds.Skip(page.StartIndex).Take(page.Limit).ToList();
        var hydrated = _hydrator.Hydrate(ids, user, entry.LibraryIds, cancellationToken);
        if (hydrated.Dropped.Count > 0)
        {
            warnings.Add(ItemsChangedWarning);
        }

        return new SmartSearchPage(
            hydrated.Items,
            page.IncludeTotal ? entry.Count : null,
            page.StartIndex,
            entry.Token,
            entry.Path,
            warnings,
            _time.GetElapsedTime(started),
            page.StartIndex + ids.Count < entry.OrderedIds.Count,
            hydrated.Dropped.Count,
            entry.CandidateCount);
    }

    private SmartSearchPage ServeFast(CachedResult entry, SmartSearchQueryDefinition query, User user, PageRequest page, List<string> warnings, long started, CancellationToken cancellationToken)
    {
        var plan = entry.Plan!;
        var cap = query.Limit ?? int.MaxValue;
        var take = (int)Math.Min(page.Limit, (long)cap - page.StartIndex);
        var (rows, ids) = entry.Locked<(IReadOnlyList<BaseItem> Rows, List<Guid> Ids)>(e =>
        {
            var candidates = plan.CandidateQuery!;
            var countNow = page.IncludeTotal && e.Count is null;
            if (take <= 0 && !countNow)
            {
                return ([], []);
            }

            var (startIndex, limit, enableCount) = (candidates.StartIndex, candidates.Limit, candidates.EnableTotalRecordCount);
            QueryResult<BaseItem> result;
            try
            {
                // Paged and grouped in SQL, and counted (after grouping, before paging) on the first page only.
                candidates.StartIndex = page.StartIndex;
                candidates.Limit = Math.Max(take, 1);
                candidates.EnableTotalRecordCount = countNow;
                result = _library.GetItemsResult(candidates);
            }
            finally
            {
                (candidates.StartIndex, candidates.Limit, candidates.EnableTotalRecordCount) = (startIndex, limit, enableCount);
            }

            if (countNow)
            {
                e.Count = Math.Min(result.TotalRecordCount, cap);
            }

            IReadOnlyList<BaseItem> shown = take > 0 ? result.Items : [];
            return (shown, Representatives(shown, plan, cancellationToken));
        });

        var hydrated = _hydrator.Hydrate(ids, user, entry.LibraryIds, cancellationToken);
        var hasMore = entry.Count is { } total ? page.StartIndex + rows.Count < total : rows.Count == take && (long)page.StartIndex + take < cap;
        return new SmartSearchPage(hydrated.Items, page.IncludeTotal ? entry.Count : null, page.StartIndex, entry.Token, entry.Path, warnings, _time.GetElapsedTime(started), hasMore, hydrated.Dropped.Count, 0);
    }

    // The rows of a Fast page, one per presentation key (SQL's grouping). For merged versions SQL keeps any member of a
    // key, even one IsVisible refuses. As on the Residual path, the unit is the row's version set within the row's key (a
    // chain's or a listed item's own key is its own row): it matches when a member of that key the user may see matches
    // (the plan's candidate query over the set's members, ungrouped: its rules are all Exact) and is shown as its primary
    // when the primary is of that key and eligible (the plan's access, libraries, kinds and missing-item rule; with the
    // user, IsVisible), else as its first matching member. A unit none of whose visible members matches keeps SQL's row,
    // which the hydrator then drops, as /Items does. Runs under the entry's lock.
    private List<Guid> Representatives(IReadOnlyList<BaseItem> rows, QueryPlan plan, CancellationToken cancellationToken)
    {
        var versioned = rows.Where(MatchOrder.IsVersioned).ToList();
        if (versioned.Count == 0)
        {
            return rows.Select(r => r.Id).ToList();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var lookup = VersionSets.LibraryLookup(_library);
        var paths = VersionSets.LibraryPaths(_library);
        var sets = VersionSets.Group(versioned.Select(VersionMember.From), lookup, paths, cancellationToken);
        var rootOf = sets.SelectMany(s => s.MemberIds.Select(id => (id, s.RootId))).GroupBy(p => p.id).ToDictionary(g => g.Key, g => g.First().RootId);
        var members = sets.SelectMany(s => s.MemberIds).Distinct().ToList();

        // The members that match the rules and that the user may see.
        var candidates = plan.CandidateQuery!;
        var (itemIds, group, startIndex, limit, enableCount) = (candidates.ItemIds, candidates.GroupByPresentationUniqueKey, candidates.StartIndex, candidates.Limit, candidates.EnableTotalRecordCount);
        List<(VersionMember Member, string? Key)> matching;
        try
        {
            candidates.ItemIds = itemIds.Length == 0 ? [.. members] : [.. members.Intersect(itemIds)];
            candidates.GroupByPresentationUniqueKey = false;
            (candidates.StartIndex, candidates.Limit, candidates.EnableTotalRecordCount) = (null, null, false);
            matching = candidates.ItemIds.Length == 0
                ? []
                : _library.GetItemList(candidates).Where(i => !LibraryAccess.IsOwned(i)).Select(i => (VersionMember.From(i), (string?)i.PresentationUniqueKey)).ToList();
        }
        finally
        {
            (candidates.ItemIds, candidates.GroupByPresentationUniqueKey) = (itemIds, group);
            (candidates.StartIndex, candidates.Limit, candidates.EnableTotalRecordCount) = (startIndex, limit, enableCount);
        }

        IReadOnlyDictionary<Guid, string?> Eligible(IReadOnlyCollection<Guid> roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = plan.CreateHydrationQuery(roots);
            query.IncludeItemTypes = plan.ItemKinds.ToArray();
            query.IsVirtualItem = candidates.IsVirtualItem;
            var eligible = new Dictionary<Guid, string?>();
            foreach (var item in _library.GetItemList(query).Where(i => !LibraryAccess.IsOwned(i)))
            {
                eligible.TryAdd(item.Id, item.PresentationUniqueKey);
            }

            return eligible;
        }

        // The Fast path always has a user and groups by key: one representative per (set, key).
        var representatives = matching.Count == 0
            ? []
            : VersionCollapse.Collapse(matching, byKey: true, Eligible, lookup, paths, cancellationToken).ToDictionary(r => (r.RootId, r.Key ?? string.Empty), r => r.Id);
        return rows
            .Select(r => rootOf.TryGetValue(r.Id, out var root) && representatives.TryGetValue((root, r.PresentationUniqueKey ?? string.Empty), out var shown) ? shown : r.Id)
            .Distinct()
            .ToList();
    }
}
