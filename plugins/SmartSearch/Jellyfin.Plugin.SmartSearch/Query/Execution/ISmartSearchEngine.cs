using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Planning;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>
/// The one query engine: the search endpoint and the auto-collections run queries through it, with
/// the same semantics. Internal: no endpoint reaches it yet. Queries are validated and migrated before they get
/// here (the caller's <c>QueryValidator</c> decides which fields the caller may use).
/// </summary>
/// <remarks>
/// <see cref="SearchAsync"/> takes no <c>SearchOptions</c> yet (the search endpoint adds what <c>/Search</c> needs:
/// <c>Explain</c>, facts columns). The auto-collections evaluate with an <see cref="EvaluationContext"/>; the form with only
/// the evaluation user and the purpose keeps a search's grouping by presentation key.
/// </remarks>
internal interface ISmartSearchEngine
{
    /// <summary>
    /// Searches for a user and returns one page: the first call evaluates and caches the result under a
    /// <c>ResultToken</c>; later pages pass it back. Every item on a page passes the user's access and
    /// <c>IsVisible(user)</c> (so a Fast page can be short), and the count is never more than <c>/Items</c> counts.
    /// </summary>
    /// <param name="query">The validated query.</param>
    /// <param name="user">The searching user.</param>
    /// <param name="page">The page to return, and the token of an earlier one.</param>
    /// <param name="cancellationToken">The request's cancellation token (linked with <c>SearchTimeoutSeconds</c>).</param>
    /// <returns>The page.</returns>
    /// <exception cref="SmartSearchRefusedException"><c>QueryTooBroad</c>, <c>FieldUnavailable</c>, <c>Timeout</c> or <c>Busy</c>.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    Task<SmartSearchPage> SearchAsync(SmartSearchQueryDefinition query, User user, PageRequest page, CancellationToken cancellationToken);

    /// <summary>
    /// Evaluates a query whole, for the auto-collections: always the Residual path, every match collapsed to one representative
    /// per version set, ordered deterministically and limited; ids only. With an evaluation user, their access applies
    /// (<c>SetUser</c>) and every item passes <c>IsVisible</c> (the hydration's <c>GetItemList</c> checks it); without one,
    /// every library is searched.
    /// </summary>
    /// <param name="query">The validated query.</param>
    /// <param name="evaluationUser">The evaluation user, or <c>null</c> for server-wide work.</param>
    /// <param name="purpose">What the evaluation is for: <see cref="EvaluationPurpose.AutoCollection"/> takes the worker's
    /// own slot, the others the search slots.</param>
    /// <param name="cancellationToken">The caller's cancellation token (linked with <c>SearchTimeoutSeconds</c>).</param>
    /// <returns>The result.</returns>
    /// <exception cref="SmartSearchRefusedException"><c>QueryTooBroad</c>, <c>FieldUnavailable</c>, <c>Timeout</c> or <c>Busy</c>.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    Task<EvaluationResult> EvaluateAllAsync(SmartSearchQueryDefinition query, User? evaluationUser, EvaluationPurpose purpose, CancellationToken cancellationToken);

    /// <summary>
    /// Evaluates a query whole in a server context (the auto-collections): the Residual path, the context's libraries
    /// only, its evaluation user's access and watch state, ratings read as its ratings viewer, relative dates from its
    /// time, one representative per version set; ids only, ordered and limited.
    /// </summary>
    /// <param name="query">The validated query.</param>
    /// <param name="context">The context.</param>
    /// <param name="cancellationToken">The caller's cancellation token (linked with <c>SearchTimeoutSeconds</c>).</param>
    /// <returns>The result.</returns>
    /// <exception cref="SmartSearchRefusedException"><c>QueryTooBroad</c>, <c>FieldUnavailable</c>, <c>Timeout</c> or <c>Busy</c>.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    Task<EvaluationResult> EvaluateAllAsync(SmartSearchQueryDefinition query, EvaluationContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Plans a query without running it (the <c>Explain</c> view). Planning reads the database (a <c>Library</c> rule's
    /// items, the facts), so it takes a slot, the candidate budget and the timeout like an evaluation.
    /// </summary>
    /// <param name="query">The validated query.</param>
    /// <param name="user">The user, or <c>null</c>.</param>
    /// <param name="purpose">The plan's purpose.</param>
    /// <param name="cancellationToken">The request's cancellation token (linked with <c>SearchTimeoutSeconds</c>).</param>
    /// <returns>The plan.</returns>
    /// <exception cref="SmartSearchRefusedException"><c>QueryTooBroad</c>, <c>Timeout</c> or <c>Busy</c>.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    Task<QueryPlan> ExplainAsync(SmartSearchQueryDefinition query, User? user, PlanPurpose purpose, CancellationToken cancellationToken);
}
