using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Services;

/// <summary>
/// Searches and checks queries for the signed-in user. Errors are the kit's <c>PluginException</c>s, mapped to
/// ProblemDetails by the controller's filter: 400 <c>InvalidRequest</c> or <c>InvalidQuery</c> with <c>errors</c>,
/// <c>QueryTooBroad</c> with <c>candidateCount</c> and <c>kinds</c>, <c>FieldUnavailable</c>; 403 <c>AdminOnlyField</c>; 413;
/// 503 <c>Timeout</c> or <c>Busy</c>.
/// </summary>
public interface ISmartSearchService
{
    /// <summary>Runs a search and returns the page asked for.</summary>
    /// <param name="user">The signed-in user (never an API key).</param>
    /// <param name="isAdmin">Whether the caller passes Jellyfin's <c>RequiresElevation</c> policy on this request.</param>
    /// <param name="request">The request, as the body's reader read it.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>The page.</returns>
    Task<SmartSearchResultDto> SearchAsync(User user, bool isAdmin, SmartSearchRequestDto request, CancellationToken cancellationToken);

    /// <summary>Checks a query and tells which path a search of it would take.</summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="isAdmin">Whether the caller passes <c>RequiresElevation</c>.</param>
    /// <param name="request">The request, as the body's reader read it.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>What was found.</returns>
    Task<SmartSearchValidationDto> ValidateAsync(User user, bool isAdmin, SmartSearchValidateRequestDto request, CancellationToken cancellationToken);
}
