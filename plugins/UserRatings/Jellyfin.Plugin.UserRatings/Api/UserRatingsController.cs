using System;
using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.UserRatings.Api.Models;
using Jellyfin.Plugin.UserRatings.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.UserRatings.Api;

/// <summary>
/// User Ratings' member endpoints: what a signed-in member reads and writes. Every action but the
/// script needs a user: the user comes from the request's <c>Jellyfin-UserId</c> claim, never from a parameter, and an API
/// key (no user) gets 403. An admin decision (<c>Can.Moderate</c>, <c>IsAdmin</c>) is Jellyfin's
/// <c>RequiresElevation</c> policy on the request, never the stored flag. Errors are ProblemDetails:
/// 400 a bad value, 403 not allowed or the feature off, 404 an item the caller can't open or rate (one answer for both, so
/// existence doesn't leak), 409 a review changed elsewhere, 413 a body over 64 KB, 503 the ratings data read-only or busy
/// for more than 5 s.
/// </summary>
/// <remarks>
/// <para>No action names a SQLite type, in its signature or its body (kit README "SQLite", Isolation):
/// the actions call <see cref="IUserRatingsApi"/>, whose SQLite work sits behind the store's own methods, so a server whose
/// Microsoft.Data.Sqlite can't be bound answers the store's 503 instead of a 404 from the routing. A query-string enum
/// value is a name or a 400: one that names no member (a <c>sort</c> of <c>Best</c>) and a number (<c>sort=1</c>)
/// alike (the kit's <see cref="StrictEnumQueryAttribute"/> with <c>NamesOnly</c>).</para>
/// <para>A write answers with the item read again after it committed. A 403 or 404 of that read (ratings turned off, the
/// item's access taken away in between) is not a refused write: the change was saved. A 503 of that read is answered 200
/// from the write's own row instead, with <c>IsPartial</c> true (<see cref="UserRatingsItemDto"/>).</para>
/// </remarks>
[ApiController]
[Authorize]
[Route("UserRatings")]
[Produces(MediaTypeNames.Application.Json)]
[KitAbiGate]
[PluginExceptionFilter]
[StrictEnumQuery(NamesOnly = true)]
public class UserRatingsController : ControllerBase
{
    /// <summary>The 403 for API keys and requests without a user.</summary>
    internal const string NoUserMessage = "This endpoint needs a signed-in user; API keys can't be used.";

    /// <summary>
    /// The largest request body a PUT reads (64 KB on the review; the rating's body has the same
    /// limit). Kestrel answers a larger one with 413, which the kit's filter sends as ProblemDetails.
    /// </summary>
    internal const int MaxBodyBytes = 64 * 1024;

    private readonly IUserRatingsApi _api;
    private readonly IUserManager _userManager;
    private readonly IEmbeddedScript _scripts;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserRatingsController"/> class.
    /// </summary>
    /// <param name="api">The member endpoints' service.</param>
    /// <param name="userManager">The user manager (the signed-in user).</param>
    /// <param name="scripts">The kit's embedded scripts.</param>
    public UserRatingsController(IUserRatingsApi api, IUserManager userManager, IEmbeddedScript scripts)
    {
        _api = api;
        _userManager = userManager;
        _scripts = scripts;
    }

    /// <summary>
    /// Gets what User Ratings offers the signed-in user, so the web client shows the right controls: whether ratings are on,
    /// whether the user may rate and review, the review limits, the rateable item kinds, the display settings, and whether
    /// the ratings data is read-only. While ratings are off it answers 200 with Enabled false.
    /// </summary>
    /// <response code="200">The capabilities, as JSON.</response>
    /// <response code="403">An API key; there is no user.</response>
    /// <returns>The capabilities.</returns>
    [HttpGet("Capabilities")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<UserRatingsCapabilitiesDto>> GetUserRatingsCapabilities()
    {
        var user = GetCurrentUser();
        return _api.GetCapabilities(user, await IsElevatedAsync().ConfigureAwait(false));
    }

    /// <summary>
    /// Gets an item's member ratings for the signed-in user: the summary (average, counts, histogram; the same for every
    /// member), the user's own rating and review, the first page of the other members' rows, the other copies of the item
    /// with ratings, and what the user may do.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="startIndex">The first review of the page (0 by default).</param>
    /// <param name="limit">The page size, 1 to 200 (the ReviewsPageSize setting by default, 20).</param>
    /// <param name="sort">The order of the reviews: Newest (the default), Oldest, Highest or Lowest.</param>
    /// <response code="200">The item's ratings, as JSON.</response>
    /// <response code="400">A paging or sort value is out of range or unknown.</response>
    /// <response code="403">An API key, or ratings are off on the server.</response>
    /// <response code="404">The item doesn't exist, the user can't open it, or its kind can't be rated.</response>
    /// <returns>The item's ratings.</returns>
    [HttpGet("Items/{itemId:guid}")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserRatingsItemDto>> GetUserRatingsItem(
        [FromRoute, Required] Guid itemId,
        [FromQuery, Range(0, int.MaxValue)] int startIndex = 0,
        [FromQuery, Range(1, 200)] int? limit = null,
        [FromQuery] UserRatingsReviewSort? sort = null)
    {
        var user = GetCurrentUser();
        return _api.GetItem(user, itemId, await IsElevatedAsync().ConfigureAwait(false), startIndex, limit, sort);
    }

    /// <summary>
    /// Gets a page of the other members' ratings and reviews of an item that the signed-in user may see (never the user's
    /// own).
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="startIndex">The first review of the page (0 by default).</param>
    /// <param name="limit">The page size, 1 to 200 (the ReviewsPageSize setting by default, 20).</param>
    /// <param name="sort">The order: Newest (the default), Oldest, Highest or Lowest.</param>
    /// <response code="200">The page, with the number of rows there are.</response>
    /// <response code="400">A paging or sort value is out of range or unknown.</response>
    /// <response code="403">An API key, or ratings or reviews are off on the server.</response>
    /// <response code="404">The item doesn't exist, the user can't open it, or its kind can't be rated.</response>
    /// <returns>The page.</returns>
    [HttpGet("Items/{itemId:guid}/Reviews")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<QueryResult<UserRatingsReviewDto>> GetUserRatingsItemReviews(
        [FromRoute, Required] Guid itemId,
        [FromQuery, Range(0, int.MaxValue)] int startIndex = 0,
        [FromQuery, Range(1, 200)] int? limit = null,
        [FromQuery] UserRatingsReviewSort? sort = null)
        => _api.GetReviews(GetCurrentUser(), itemId, startIndex, limit, sort);

    /// <summary>
    /// Sets the signed-in user's stars on an item. Sent twice at once (a double click, two tabs), the last write wins: the
    /// user still has one rating of the item.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="request">The stars: 0.5 to 5 in steps of 0.5.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The item's ratings after the change.</response>
    /// <response code="400">The stars are missing or not 0.5 to 5 in steps of 0.5.</response>
    /// <response code="403">An API key; ratings are off; rating is limited to selected members; or the item must be played
    /// first.</response>
    /// <response code="404">The item doesn't exist, the user can't open it, or its kind can't be rated.</response>
    /// <response code="413">The request is over 64 KB.</response>
    /// <response code="503">The ratings data is read-only or busy; nothing changed.</response>
    /// <returns>The item's ratings.</returns>
    [HttpPut("Items/{itemId:guid}/Mine/Rating")]
    [NoStore]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<UserRatingsItemDto>> SetUserRatingsMyRating(
        [FromRoute, Required] Guid itemId,
        [FromBody, Required] UserRatingsRatingRequest request,
        CancellationToken cancellationToken)
    {
        var user = GetCurrentUser();
        return await _api.SetRatingAsync(user, itemId, request, await IsElevatedAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the signed-in user's stars on an item: a rating with a review keeps the review, one without is deleted; without
    /// stars nothing changes. Allowed whatever "Who can rate" says.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The item's ratings after the change.</response>
    /// <response code="403">An API key, or ratings are off.</response>
    /// <response code="404">The item doesn't exist, the user can't open it, or its kind can't be rated.</response>
    /// <response code="503">The ratings data is read-only or busy; nothing changed.</response>
    /// <returns>The item's ratings.</returns>
    [HttpDelete("Items/{itemId:guid}/Mine/Rating")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<UserRatingsItemDto>> DeleteUserRatingsMyRating([FromRoute, Required] Guid itemId, CancellationToken cancellationToken)
    {
        var user = GetCurrentUser();
        return await _api.ClearRatingAsync(user, itemId, await IsElevatedAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the signed-in user's review of an item, under at most one optional precondition: IfReviewUpdatedUtc (the user's
    /// Mine.ReviewUpdatedUtc as last read: a review changed or deleted since gives 409, stars changed since don't), IfNoReview
    /// (a first review: a review saved meanwhile gives 409), or IfUpdatedUtc (the user's Mine.UpdatedUtc as last read: any
    /// change of the rating since gives 409). Without one the review replaces whatever is there.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="request">The review, its spoiler flag and optionally one precondition.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The item's ratings after the change.</response>
    /// <response code="400">The review is empty or longer than the server's limit, or more than one precondition was
    /// sent.</response>
    /// <response code="403">An API key; ratings or reviews are off; rating is limited to selected members; or the item must
    /// be played first.</response>
    /// <response code="404">The item doesn't exist, the user can't open it, or its kind can't be rated.</response>
    /// <response code="409">The precondition doesn't hold (the review, or with IfUpdatedUtc the rating, changed since);
    /// nothing changed.</response>
    /// <response code="413">The request is over 64 KB.</response>
    /// <response code="503">The ratings data is read-only or busy; nothing changed.</response>
    /// <returns>The item's ratings.</returns>
    [HttpPut("Items/{itemId:guid}/Mine/Review")]
    [NoStore]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<UserRatingsItemDto>> SetUserRatingsMyReview(
        [FromRoute, Required] Guid itemId,
        [FromBody, Required] UserRatingsReviewRequest request,
        CancellationToken cancellationToken)
    {
        var user = GetCurrentUser();
        return await _api.SetReviewAsync(user, itemId, request, await IsElevatedAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the signed-in user's review of an item: a rating with stars keeps them, one without is deleted; without a
    /// review nothing changes. Allowed whatever "Who can rate" says.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The item's ratings after the change.</response>
    /// <response code="403">An API key, or ratings are off.</response>
    /// <response code="404">The item doesn't exist, the user can't open it, or its kind can't be rated.</response>
    /// <response code="503">The ratings data is read-only or busy; nothing changed.</response>
    /// <returns>The item's ratings.</returns>
    [HttpDelete("Items/{itemId:guid}/Mine/Review")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<UserRatingsItemDto>> DeleteUserRatingsMyReview([FromRoute, Required] Guid itemId, CancellationToken cancellationToken)
    {
        var user = GetCurrentUser();
        return await _api.ClearReviewAsync(user, itemId, await IsElevatedAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the signed-in user's rating and review of an item. Allowed whatever "Who can rate" says.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Deleted, or there was nothing to delete.</response>
    /// <response code="403">An API key, or ratings are off.</response>
    /// <response code="404">The item doesn't exist, the user can't open it, or its kind can't be rated.</response>
    /// <response code="503">The ratings data is read-only or busy; nothing changed.</response>
    /// <returns>No content.</returns>
    [HttpDelete("Items/{itemId:guid}/Mine")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> DeleteUserRatingsMine([FromRoute, Required] Guid itemId, CancellationToken cancellationToken)
    {
        await _api.DeleteMineAsync(GetCurrentUser(), itemId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Lists the signed-in user's own ratings that the item routes can't reach under the server's settings now: on items they
    /// can't open (a library taken away, a parental limit, a parent that hides the item), of a kind that can no longer be
    /// rated, and, while ratings are turned off, all of them. Their stars, the start of their own review and the dates,
    /// nothing about the items. Delete one with DELETE /UserRatings/Mine/{ratingId}, which works in all those cases.
    /// </summary>
    /// <param name="startIndex">The first row of the page (0 by default).</param>
    /// <param name="limit">The page size, 1 to 200 (50 by default).</param>
    /// <response code="200">The page, with the number of rows there are.</response>
    /// <response code="400">A paging value is out of range.</response>
    /// <response code="403">An API key; there is no user.</response>
    /// <returns>The page.</returns>
    [HttpGet("Mine/Inaccessible")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<QueryResult<UserRatingsOwnRowDto>> GetUserRatingsInaccessible(
        [FromQuery, Range(0, int.MaxValue)] int startIndex = 0,
        [FromQuery, Range(1, 200)] int? limit = null)
        => _api.GetInaccessible(GetCurrentUser(), startIndex, limit);

    /// <summary>
    /// Deletes one of the signed-in user's own ratings by its id, without looking at the item, so it works after the user
    /// lost access to it. Allowed whatever "Who can rate" and the server's switches say.
    /// </summary>
    /// <param name="ratingId">The rating.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Deleted.</response>
    /// <response code="403">An API key; there is no user.</response>
    /// <response code="404">No rating of the user has that id (the same answer whether it exists or not).</response>
    /// <response code="503">The ratings data is read-only or busy; nothing changed.</response>
    /// <returns>No content.</returns>
    [HttpDelete("Mine/{ratingId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> DeleteUserRatingsOwnRow([FromRoute, Required] Guid ratingId, CancellationToken cancellationToken)
    {
        await _api.DeleteOwnRowAsync(GetCurrentUser(), ratingId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Gets the web client script <c>index.html</c> loads: anonymous and static, holding no user data,
    /// with the build's MVID as its ETag.
    /// </summary>
    /// <response code="200">The script, <c>text/javascript</c>.</response>
    /// <response code="304">The browser's copy is current.</response>
    /// <returns>The script, or 304.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public IActionResult GetUserRatingsClientScript() => _scripts.ClientScript();

    /// <summary>
    /// Gets the settings page's dashboard module (with the Moderation tab), which the page imports: anonymous and
    /// static, holding no user data; every call it makes is an admin route.
    /// </summary>
    /// <response code="200">The ES module, <c>text/javascript</c>.</response>
    /// <response code="304">The browser's copy is current.</response>
    /// <returns>The module, or 304.</returns>
    [HttpGet("AdminScript")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public IActionResult GetUserRatingsAdminScript() => _scripts.AdminScript();

    // The user of the claim; an API key or an unknown user is 403 before anything else runs.
    private JellyfinUser GetCurrentUser() => User.RequireUser(_userManager, NoUserMessage);

    // Jellyfin's RequiresElevation on this request, never the stored flag.
    private Task<bool> IsElevatedAsync() => CurrentUser.IsElevatedAsync(HttpContext);
}
