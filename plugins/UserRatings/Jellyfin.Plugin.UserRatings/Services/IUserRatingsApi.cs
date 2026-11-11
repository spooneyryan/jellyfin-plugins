using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.UserRatings.Api.Models;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// The member endpoints' service: the controller's only dependency besides Jellyfin's, so the
/// controller names no internal type and no SQLite type. Each method takes the
/// signed-in user (the controller resolves it from the claim; API keys get 403 first) and answers DTOs; errors are the
/// kit's <c>PluginException</c>s, which the controller's filter turns into ProblemDetails.
/// </summary>
public interface IUserRatingsApi
{
    /// <summary>
    /// Gets what User Ratings offers the user (<c>UserRatingsCapabilitiesDto</c>). While ratings are off it still
    /// answers, with <c>Enabled</c> false, so the web client can hide itself.
    /// </summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="isAdmin">Whether the request passes Jellyfin's <c>RequiresElevation</c> policy.</param>
    /// <returns>The capabilities.</returns>
    UserRatingsCapabilitiesDto GetCapabilities(User user, bool isAdmin);

    /// <summary>
    /// Gets an item's ratings for the user (<c>GET Items/{itemId}</c>).
    /// </summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="isModerator">Whether the request passes <c>RequiresElevation</c> (<c>Can.Moderate</c>).</param>
    /// <param name="startIndex">The first review of the page (0 and up).</param>
    /// <param name="limit">The page size (1 to 200), or <c>null</c> for <c>ReviewsPageSize</c>.</param>
    /// <param name="sort">The order, or <c>null</c> for <see cref="UserRatingsReviewSort.Newest"/>.</param>
    /// <returns>The item's ratings.</returns>
    UserRatingsItemDto GetItem(User user, Guid itemId, bool isModerator, int startIndex, int? limit, UserRatingsReviewSort? sort);

    /// <summary>
    /// Gets a page of the other members' rows of an item the user may see (<c>GET Items/{itemId}/Reviews</c>).
    /// </summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="startIndex">The first review of the page.</param>
    /// <param name="limit">The page size, or <c>null</c> for <c>ReviewsPageSize</c>.</param>
    /// <param name="sort">The order, or <c>null</c> for <see cref="UserRatingsReviewSort.Newest"/>.</param>
    /// <returns>The page.</returns>
    QueryResult<UserRatingsReviewDto> GetReviews(User user, Guid itemId, int startIndex, int? limit, UserRatingsReviewSort? sort);

    /// <summary>
    /// Sets the user's stars on an item (<c>PUT Items/{itemId}/Mine/Rating</c>).
    /// </summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="request">The stars.</param>
    /// <param name="isModerator">Whether the request passes <c>RequiresElevation</c> (the answer's <c>Can.Moderate</c>).</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>The item's ratings after the write.</returns>
    Task<UserRatingsItemDto> SetRatingAsync(User user, Guid itemId, UserRatingsRatingRequest request, bool isModerator, CancellationToken cancellationToken);

    /// <summary>
    /// Clears the user's stars on an item (<c>DELETE Items/{itemId}/Mine/Rating</c>): a row with a review keeps it,
    /// a row without one is deleted; without stars nothing changes.
    /// </summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="isModerator">Whether the request passes <c>RequiresElevation</c>.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>The item's ratings after the write.</returns>
    Task<UserRatingsItemDto> ClearRatingAsync(User user, Guid itemId, bool isModerator, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the user's review of an item (<c>PUT Items/{itemId}/Mine/Review</c>).
    /// </summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="request">The review, its spoiler flag and at most one precondition (<c>IfUpdatedUtc</c>, <c>IfReviewUpdatedUtc</c>, <c>IfNoReview</c>).</param>
    /// <param name="isModerator">Whether the request passes <c>RequiresElevation</c>.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>The item's ratings after the write.</returns>
    Task<UserRatingsItemDto> SetReviewAsync(User user, Guid itemId, UserRatingsReviewRequest request, bool isModerator, CancellationToken cancellationToken);

    /// <summary>
    /// Clears the user's review of an item (<c>DELETE Items/{itemId}/Mine/Review</c>): a row with stars keeps them,
    /// a row without is deleted; without a review nothing changes.
    /// </summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="isModerator">Whether the request passes <c>RequiresElevation</c>.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>The item's ratings after the write.</returns>
    Task<UserRatingsItemDto> ClearReviewAsync(User user, Guid itemId, bool isModerator, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the user's rating and review of an item (<c>DELETE Items/{itemId}/Mine</c>).
    /// </summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>A task.</returns>
    Task DeleteMineAsync(User user, Guid itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a page of the user's own ratings on items they can no longer open (<c>GET Mine/Inaccessible</c>): their
    /// stars, a snippet of their text and the dates, nothing about the items.
    /// </summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="startIndex">The first row of the page.</param>
    /// <param name="limit">The page size (1 to 200), or <c>null</c> for 50.</param>
    /// <returns>The page.</returns>
    QueryResult<UserRatingsOwnRowDto> GetInaccessible(User user, int startIndex, int? limit);

    /// <summary>
    /// Deletes one of the user's own ratings by its id, with no item lookup (<c>DELETE Mine/{ratingId}</c>), so it
    /// works after they lost access to the item.
    /// </summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="ratingId">The rating.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>A task.</returns>
    Task DeleteOwnRowAsync(User user, Guid ratingId, CancellationToken cancellationToken);
}
