using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Services;

/// <summary>
/// The signed-in user's saved queries: their own, and those others share. A private query is only its owner's (and an
/// administrator's); anyone else gets 404 for it, as for an id that doesn't exist. A shared query is run as the user who
/// runs it: their libraries, parental limit and watch state, never the owner's. Errors are the kit's
/// <c>PluginException</c>s, mapped to ProblemDetails by the controller's filter.
/// </summary>
public interface ISmartSearchSavedQueries
{
    /// <summary>Lists queries, ordered by name.</summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="isAdmin">Whether the caller passes Jellyfin's <c>RequiresElevation</c> policy on this request.</param>
    /// <param name="scope"><c>Mine</c>, <c>Shared</c> (others' shared queries) or <c>All</c> (both, the default).</param>
    /// <param name="startIndex">The index of the page's first query.</param>
    /// <param name="limit">The page size, 1 to 500.</param>
    /// <returns>The page.</returns>
    SmartSearchSavedQueryListDto List(User user, bool isAdmin, string? scope, int startIndex, int limit);

    /// <summary>Gets a query the caller may open.</summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="isAdmin">Whether the caller passes <c>RequiresElevation</c>.</param>
    /// <param name="id">The query's id.</param>
    /// <returns>The query.</returns>
    SmartSearchSavedQueryDto Get(User user, bool isAdmin, Guid id);

    /// <summary>Saves a new query, owned by the caller.</summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="isAdmin">Whether the caller passes <c>RequiresElevation</c>.</param>
    /// <param name="request">The body, as its reader read it.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The saved query.</returns>
    Task<SmartSearchSavedQueryDto> CreateAsync(User user, bool isAdmin, SmartSearchSavedQueryRequestDto request, CancellationToken cancellationToken);

    /// <summary>Changes a query: its owner, or an administrator.</summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="isAdmin">Whether the caller passes <c>RequiresElevation</c>.</param>
    /// <param name="id">The query's id.</param>
    /// <param name="request">The body, as its reader read it.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The changed query.</returns>
    Task<SmartSearchSavedQueryDto> UpdateAsync(User user, bool isAdmin, Guid id, SmartSearchSavedQueryRequestDto request, CancellationToken cancellationToken);

    /// <summary>Copies a query the caller may open into a new private query of theirs.</summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="isAdmin">Whether the caller passes <c>RequiresElevation</c>.</param>
    /// <param name="id">The query's id.</param>
    /// <param name="request">The body, as its reader read it.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The copy.</returns>
    Task<SmartSearchSavedQueryDto> DuplicateAsync(User user, bool isAdmin, Guid id, SmartSearchSavedQueryDuplicateDto request, CancellationToken cancellationToken);

    /// <summary>Deletes a query: its owner, or an administrator.</summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="isAdmin">Whether the caller passes <c>RequiresElevation</c>.</param>
    /// <param name="id">The query's id.</param>
    /// <param name="detachAutoCollections">Whether auto-collections that use it keep a copy instead of refusing the delete.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>A task.</returns>
    Task DeleteAsync(User user, bool isAdmin, Guid id, bool detachAutoCollections, CancellationToken cancellationToken);

    /// <summary>Runs a query the caller may open, as the caller, and returns a page as <c>/Search</c> does.</summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="isAdmin">Whether the caller passes <c>RequiresElevation</c>.</param>
    /// <param name="id">The query's id.</param>
    /// <param name="request">The page, as its reader read it.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>The page.</returns>
    Task<SmartSearchResultDto> RunAsync(User user, bool isAdmin, Guid id, SmartSearchSavedQueryRunDto request, CancellationToken cancellationToken);
}
