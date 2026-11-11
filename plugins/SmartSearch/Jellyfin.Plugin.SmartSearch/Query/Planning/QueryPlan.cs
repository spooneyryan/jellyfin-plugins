using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// A planned query: the SQL query that returns the candidates, a superset of the matches, and the
/// in-memory re-check that keeps exactly the matches. The engine runs it; nothing here touches the library.
/// </summary>
internal sealed class QueryPlan
{
    private readonly Func<InternalItemsQuery> _newQuery;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryPlan"/> class.
    /// </summary>
    /// <param name="path">How the plan is run.</param>
    /// <param name="candidateQuery">The candidate query, or <c>null</c> for <see cref="QueryPlanPath.Empty"/>.</param>
    /// <param name="libraries">The allowed libraries.</param>
    /// <param name="itemKinds">The kinds the query can return.</param>
    /// <param name="residual">The rules SQL doesn't fully enforce (an empty <c>All</c> when none is left).</param>
    /// <param name="steps">What happened to each rule.</param>
    /// <param name="recheck">The in-memory re-check of a candidate.</param>
    /// <param name="dtoOptions">The candidates' <c>DtoOptions</c>.</param>
    /// <param name="newQuery">Creates a query with the plan's access (user, parental limits, unrated items, tags).</param>
    /// <param name="environment">The evaluation environment, with the plan's library membership.</param>
    public QueryPlan(
        QueryPlanPath path,
        InternalItemsQuery? candidateQuery,
        IReadOnlyList<CollectionFolder> libraries,
        IReadOnlyList<BaseItemKind> itemKinds,
        SmartSearchQueryNode residual,
        IReadOnlyList<PushdownStep> steps,
        Func<EvaluationRow, bool> recheck,
        DtoOptions dtoOptions,
        Func<InternalItemsQuery> newQuery,
        EvaluationEnvironment environment)
    {
        Path = path;
        CandidateQuery = candidateQuery;
        Libraries = libraries;
        ItemKinds = itemKinds;
        Residual = residual;
        Steps = steps;
        Recheck = recheck;
        DtoOptions = dtoOptions;
        _newQuery = newQuery;
        Environment = environment;
    }

    /// <summary>Gets how the plan is run.</summary>
    public QueryPlanPath Path { get; }

    /// <summary>
    /// Gets the candidate query: for <see cref="QueryPlanPath.Residual"/> the one <c>GetItemIds</c> call; for
    /// <see cref="QueryPlanPath.Fast"/> the <c>GetItemsResult</c> page query (with <c>OrderBy</c> and
    /// <c>GroupByPresentationUniqueKey = true</c>, the engine adds paging); <c>null</c> for <see cref="QueryPlanPath.Empty"/>.
    /// </summary>
    public InternalItemsQuery? CandidateQuery { get; }

    /// <summary>Gets the allowed libraries: the requested ones the caller can open (boxsets libraries only for <c>BoxSet</c>).</summary>
    public IReadOnlyList<CollectionFolder> Libraries { get; }

    /// <summary>Gets the kinds the query can return.</summary>
    public IReadOnlyList<BaseItemKind> ItemKinds { get; }

    /// <summary>Gets the residual: the query's tree without the rules SQL fully enforces.</summary>
    public SmartSearchQueryNode Residual { get; }

    /// <summary>Gets a value indicating whether the residual has no rule left.</summary>
    public bool ResidualIsEmpty => Residual.Children is { Count: 0 } && !Residual.Negate;

    /// <summary>Gets what happened to each rule, in the query's order.</summary>
    public IReadOnlyList<PushdownStep> Steps { get; }

    /// <summary>
    /// Gets the re-check of one candidate: an owned item (an extra, a local alternate version) is never a
    /// result of its own, then the residual decides.
    /// </summary>
    public Func<EvaluationRow, bool> Recheck { get; }

    /// <summary>Gets the candidates' <c>DtoOptions</c>.</summary>
    public DtoOptions DtoOptions { get; }

    /// <summary>Gets a value indicating whether candidates need the user's data (a per-user rule in the residual, or a per-user sort in memory).</summary>
    public bool NeedsUserData => DtoOptions.EnableUserData;

    /// <summary>Gets a value indicating whether candidates need their provider ids.</summary>
    public bool NeedsProviderIds => DtoOptions.ContainsField(MediaBrowser.Model.Querying.ItemFields.ProviderIds);

    /// <summary>Gets the evaluation environment, whose <c>LibraryMembers</c> is the plan's resolver.</summary>
    public EvaluationEnvironment Environment { get; }

    /// <summary>Gets the sort the Fast path asks core for, or an empty list.</summary>
    public IReadOnlyList<(ItemSortBy OrderBy, SortOrder SortOrder)> OrderBy => CandidateQuery?.OrderBy ?? [];

    /// <summary>
    /// Creates a query that counts the plan's scope: the items of its kinds in its libraries, with its access and
    /// whether missing items count, whatever its rules say; <c>null</c> for an empty plan.
    /// </summary>
    /// <returns>The query, or <c>null</c>.</returns>
    public InternalItemsQuery? CreateScopeQuery()
    {
        if (CandidateQuery is null || ItemKinds.Count == 0 || Libraries.Count == 0)
        {
            return null;
        }

        var query = _newQuery();
        query.IncludeItemTypes = [.. ItemKinds];
        query.TopParentIds = LibraryAccess.TopParentIds(Libraries);
        query.IsVirtualItem = CandidateQuery.IsVirtualItem;
        query.Recursive = true;
        return query;
    }

    /// <summary>
    /// Creates a batch hydration query: the plan's access, the given ids, the same <c>TopParentIds</c>,
    /// <c>GroupByPresentationUniqueKey = false</c> and the candidates' <c>DtoOptions</c>.
    /// </summary>
    /// <param name="ids">The ids of one batch.</param>
    /// <returns>The query.</returns>
    /// <exception cref="InvalidOperationException">The plan is empty.</exception>
    public InternalItemsQuery CreateHydrationQuery(IReadOnlyCollection<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (CandidateQuery is null)
        {
            throw new InvalidOperationException("An empty plan has no candidates to load.");
        }

        var query = _newQuery();
        query.ItemIds = [.. ids];
        query.TopParentIds = CandidateQuery.TopParentIds;
        query.GroupByPresentationUniqueKey = false;
        query.DtoOptions = DtoOptions;
        return query;
    }
}
