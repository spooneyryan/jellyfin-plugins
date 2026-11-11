namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// The body of <c>PUT /UserRatings/Items/{itemId}/Mine/Rating</c>: <c>{"Stars": 3.5}</c>.
/// </summary>
public sealed class UserRatingsRatingRequest
{
    /// <summary>
    /// Gets or sets the stars: 0.5 to 5 in steps of 0.5. Anything else (0, 0.25, 3.3, 5.5, none) is a 400.
    /// </summary>
    public decimal? Stars { get; set; }
}
