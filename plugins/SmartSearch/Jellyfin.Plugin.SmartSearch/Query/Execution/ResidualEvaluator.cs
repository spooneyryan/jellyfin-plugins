using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>
/// The Residual path (the design's <c>CandidateLoader</c>): one <c>GetItemIds</c> for the whole
/// candidate id list (with the user; no <c>StartIndex</c>, so ties and <c>IsVisible</c> can't make it skip rows), counted
/// against <c>MaxCandidates</c>; hydration by <c>ItemIds</c> in batches of <see cref="BatchSize"/> through
/// <c>GetItemList</c> (which adds <c>IsVisible(user)</c>), each batch evaluated and dropped; then version collapse,
/// presentation-key grouping, the sort and the limit.
/// </summary>
/// <remarks>
/// <para><b>Per batch</b>: first the <c>OwnerId</c> safety net (an owned item is dropped, whatever the query
/// asked SQL); then phase A, the three-valued test (<see cref="RuleTree.CompileTriState"/>); then phase B for the
/// batch's undecided items only, whose facts are fetched in chunks of <see cref="FactsChunkSize"/> and which are tested
/// again. An item that is still undecided refuses the search: never a match dropped or let through unseen.</para>
/// <para><b>Versions</b>: <see cref="VersionCollapse"/>; a set (with a user, a set's members of one presentation key)
/// matches when any member does, and its primary represents it when the primary is eligible and of that key (one batched
/// <c>GetItemList</c> with the plan's access, scope and kinds, so with a user it passes <c>IsVisible</c> too), else the
/// first matching member by sort name, then id.</para>
/// <para><b>Presentation keys</b>: with a user, core's <c>/Items</c> groups its rows by <c>PresentationUniqueKey</c>
/// (series of one show in two libraries, a pair that shares a key without being a set), so the representatives are
/// grouped the same way, first in the order: never more results than <c>/Items</c> counts.</para>
/// <para><b>Cancellation</b>: the token is checked before the id list, before each batch, each facts chunk and the
/// eligibility check. A statement in flight isn't interrupted (core's item queries take no token).</para>
/// </remarks>
internal sealed class ResidualEvaluator
{
    /// <summary>The candidates hydrated per <c>GetItemList</c>.</summary>
    public const int BatchSize = 1000;

    /// <summary>The undecided candidates whose facts are fetched at once.</summary>
    public const int FactsChunkSize = 500;

    // The kinds core groups by presentation key with a user (BaseItemRepository.cs:1527, EnableGroupByPresentationUniqueKey).
    private static readonly HashSet<BaseItemKind> GroupedKinds = [BaseItemKind.Episode, BaseItemKind.Video, BaseItemKind.Movie, BaseItemKind.MusicVideo, BaseItemKind.Series, BaseItemKind.Season];

    private readonly FieldRegistry _registry;
    private readonly ILibraryManager _library;
    private readonly IUserDataManager _userData;
    private readonly IItemFactsSource[] _facts;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResidualEvaluator"/> class.
    /// </summary>
    /// <param name="registry">The field registry.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="userData">The user data manager (<c>GetUserData</c>, which reads what the item was loaded with).</param>
    /// <param name="facts">The facts sources (none yet).</param>
    public ResidualEvaluator(FieldRegistry registry, ILibraryManager library, IUserDataManager userData, IEnumerable<IItemFactsSource> facts)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(userData);
        ArgumentNullException.ThrowIfNull(facts);
        _registry = registry;
        _library = library;
        _userData = userData;
        _facts = facts.ToArray();
    }

    /// <summary>Gets a value indicating whether core groups a user's results of these kinds by presentation key.</summary>
    /// <param name="user">The user, or <c>null</c>.</param>
    /// <param name="kinds">The kinds.</param>
    /// <returns>Whether it does.</returns>
    public static bool GroupsByPresentationKey(User? user, IReadOnlyCollection<BaseItemKind> kinds)
        => user is not null && (kinds.Count == 0 || kinds.Any(GroupedKinds.Contains));

    /// <summary>Runs a Residual plan.</summary>
    /// <param name="plan">The plan (<see cref="QueryPlanPath.Residual"/>); its candidate query is used and restored.</param>
    /// <param name="query">The query (its sort and limit).</param>
    /// <param name="user">The plan's user, or <c>null</c>.</param>
    /// <param name="budget">The evaluation's candidate budget.</param>
    /// <param name="randomSeed">The seed of the <c>Random</c> sort key.</param>
    /// <param name="cancellationToken">The evaluation's cancellation token.</param>
    /// <param name="factsViewerId">The user whose view facts are read with; the plan's user when left out.</param>
    /// <param name="wholeSets">Whether a version set is one result even where a user's <c>/Items</c> shows a row per
    /// presentation key.</param>
    /// <param name="itemCache">The candidates already loaded in this pass, shared with the pass's other evaluations, or <c>null</c>.</param>
    /// <param name="candidateIds">The candidate ids already read (the pass's scope), or <c>null</c> to read them with the plan's query.</param>
    /// <returns>The ordered, limited result.</returns>
    /// <exception cref="SmartSearchRefusedException">Past <c>MaxCandidates</c>, or a facts field nothing answers.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public ResidualResult Run(QueryPlan plan, SmartSearchQueryDefinition query, User? user, CandidateBudget budget, int randomSeed, CancellationToken cancellationToken, Guid? factsViewerId = null, bool wholeSets = false, PassItemCache? itemCache = null, IReadOnlyList<Guid>? candidateIds = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(budget);
        if (plan.Path != QueryPlanPath.Residual || plan.CandidateQuery is not { } candidates)
        {
            throw new ArgumentException("Only a Residual plan is run here.", nameof(plan));
        }

        // Phase B's sources, before anything is read: a facts field nothing answers refuses the search.
        var factsFields = RuleTree.FactsFields(plan.Residual, _registry);
        var sources = factsFields.GroupBy(f => _facts.FirstOrDefault(s => s.Answers(f))).ToList();
        if (sources.FirstOrDefault(g => g.Key is null) is { } unanswered)
        {
            throw SmartSearchRefusedException.FieldUnavailable(unanswered);
        }

        var test = RuleTree.CompileTriState(plan.Residual, _registry, plan.Environment);
        var order = MatchOrder.For(query.Sort, _registry, user?.Id, randomSeed);
        var readUserData = user is not null && (plan.NeedsUserData || order.NeedsUserData);

        // The candidate ids in one statement, one more than the budget allows so that a list past it shows.
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Guid> ids;
        var limit = candidates.Limit;
        if (candidateIds is not null)
        {
            ids = candidateIds;
        }
        else
        {
            try
            {
                candidates.Limit = budget.NextLimit;
                ids = _library.GetItemIds(candidates);
            }
            finally
            {
                candidates.Limit = limit;
            }
        }

        budget.Take(ids.Count, () => _library.GetCount(candidates));

        // Batches, each evaluated and dropped; only the matches' rows survive.
        var matches = new List<MatchRow>();
        var batches = 0;
        var factsFetched = 0;
        foreach (var batch in ids.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            batches++;
            List<EvaluationRow>? undecided = null;
            foreach (var item in Hydrate(plan, user, batch, itemCache))
            {
                // The safety net: never an owned item (extras, trailers), whatever SQL let through.
                if (LibraryAccess.IsOwned(item))
                {
                    continue;
                }

                var row = new EvaluationRow(item, readUserData ? _userData.GetUserData(user!, item) : null);
                switch (test(row))
                {
                    case TriState.True:
                        matches.Add(order.Capture(item, row.UserData));
                        break;
                    case TriState.Unknown:
                        (undecided ??= []).Add(row);
                        break;
                }
            }

            if (undecided is not null)
            {
                factsFetched += PhaseB(undecided, sources!, test, order, matches, factsViewerId ?? user?.Id, cancellationToken);
            }
        }

        // One representative per version set.
        cancellationToken.ThrowIfCancellationRequested();
        var byKey = !wholeSets && GroupsByPresentationKey(user, plan.ItemKinds);
        var representatives = Collapse(matches, plan, user, readUserData, order, byKey, cancellationToken);

        // The order; then, as /Items does for a user, one result per presentation key (the first in the order).
        representatives.Sort(order);
        if (byKey)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            representatives.RemoveAll(r => !seen.Add(r.PresentationUniqueKey ?? "\0"));
        }

        // The limit.
        var ordered = representatives.Select(r => r.Id);
        if (query.Limit is { } max)
        {
            ordered = ordered.Take(max);
        }

        return new ResidualResult(ordered.ToList(), ids.Count, matches.Count, batches, factsFetched);
    }

    // A batch of candidates: from the pass's cache when there is one (the same user, libraries and fields), else loaded.
    private IEnumerable<BaseItem> Hydrate(QueryPlan plan, User? user, Guid[] batch, PassItemCache? cache)
    {
        if (cache is null || cache.MaxItems == 0)
        {
            return _library.GetItemList(plan.CreateHydrationQuery(batch));
        }

        var scope = ItemScopeOf(plan, user);
        return cache.Load(scope, batch, ids => _library.GetItemList(plan.CreateHydrationQuery([.. ids])));
    }

    /// <summary>The key the pass's cache keeps a plan's candidates under (<see cref="PassItemCache.ScopeOf"/>).</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="user">The plan's user, or <c>null</c>.</param>
    /// <returns>The key.</returns>
    public static string ItemScopeOf(QueryPlan plan, User? user)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return PassItemCache.ScopeOf(user?.Id, plan.CandidateQuery!.TopParentIds, plan.NeedsUserData, plan.DtoOptions.Fields.Select(f => f.ToString()));
    }

    private int PhaseB(
        List<EvaluationRow> undecided,
        List<IGrouping<IItemFactsSource?, string>> sources,
        Func<EvaluationRow, TriState> test,
        MatchOrder order,
        List<MatchRow> matches,
        Guid? viewerId,
        CancellationToken cancellationToken)
    {
        foreach (var chunk in undecided.Chunk(FactsChunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = chunk.Select(r => r.Item).ToList();
            var facts = new Dictionary<Guid, Dictionary<string, object?>>();
            foreach (var source in sources)
            {
                foreach (var (id, values) in source.Key!.Load(items, source.ToList(), viewerId, cancellationToken))
                {
                    if (!facts.TryGetValue(id, out var merged))
                    {
                        merged = new Dictionary<string, object?>(StringComparer.Ordinal);
                        facts[id] = merged;
                    }

                    foreach (var (field, value) in values)
                    {
                        merged[field] = value;
                    }
                }
            }

            foreach (var row in chunk)
            {
                // An item a source left out can't be decided: refused, never dropped or let through unseen.
                if (!facts.TryGetValue(row.Item.Id, out var itemFacts) || sources.Any(s => s.Any(f => !itemFacts.ContainsKey(f))))
                {
                    throw SmartSearchRefusedException.FieldUnavailable(sources.SelectMany(s => s));
                }

                var decided = test(row with { Facts = itemFacts });
                if (decided == TriState.Unknown)
                {
                    throw new InvalidOperationException("A rule stayed unknown with its item's facts.");
                }

                if (decided == TriState.True)
                {
                    matches.Add(order.Capture(row.Item, row.UserData));
                }
            }
        }

        return undecided.Count;
    }

    private List<MatchRow> Collapse(List<MatchRow> matches, QueryPlan plan, User? user, bool readUserData, MatchOrder order, bool byKey, CancellationToken cancellationToken)
    {
        // Without a member in a set, every match is its own set and represents it (what Collapse returns, without the work).
        if (!matches.Any(m => m.Version is not null))
        {
            return matches;
        }

        var byId = new Dictionary<Guid, MatchRow>();
        foreach (var match in matches)
        {
            byId.TryAdd(match.Id, match);
        }

        // A primary that didn't match represents its set only when it is eligible: the plan's access, libraries, kinds and
        // missing-item rule (so with a user it passes IsVisible), never GetItemById's unchecked lookup.
        var roots = new Dictionary<Guid, MatchRow>();
        IReadOnlyDictionary<Guid, string?> Eligible(IReadOnlyCollection<Guid> ids)
        {
            var eligible = new Dictionary<Guid, string?>();
            foreach (var batch in ids.Chunk(BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var query = plan.CreateHydrationQuery(batch);
                query.IncludeItemTypes = plan.ItemKinds.ToArray();
                query.IsVirtualItem = plan.CandidateQuery!.IsVirtualItem;
                foreach (var item in _library.GetItemList(query))
                {
                    if (!LibraryAccess.IsOwned(item) && eligible.TryAdd(item.Id, item.PresentationUniqueKey))
                    {
                        roots[item.Id] = order.Capture(item, readUserData ? _userData.GetUserData(user!, item) : null);
                    }
                }
            }

            return eligible;
        }

        // By presentation key (a search with a user), a set is collapsed per key, as /Items shows it (a chain's or a listed
        // item's own key is its own row there); otherwise the whole set is one result.
        return VersionCollapse.Collapse(
                matches.Select(m => (m.Member, m.PresentationUniqueKey)).ToList(),
                byKey,
                Eligible,
                VersionSets.LibraryLookup(_library),
                VersionSets.LibraryPaths(_library),
                cancellationToken)
            .Select(r => byId.TryGetValue(r.Id, out var row) ? row : roots[r.Id])
            .ToList();
    }
}
