using System;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// One of the caller's own ratings on an item they can no longer open (<c>Mine/Inaccessible</c>,
/// <c>UserRatingsOwnRowDto</c>, "Lost library access"): their own stars, the start of their own text and the dates, and
/// nothing about the item (no name, image, path or library), since the admin has taken that access away. Delete it with
/// <c>DELETE /UserRatings/Mine/{RatingId}</c>.
/// </summary>
public sealed class UserRatingsOwnRowDto
{
    /// <summary>
    /// Gets or sets the rating's id.
    /// </summary>
    public Guid RatingId { get; set; }

    /// <summary>
    /// Gets or sets the stars, 0.5 to 5; null (left out) for a review without stars.
    /// </summary>
    public double? Stars { get; set; }

    /// <summary>
    /// Gets or sets the first 120 characters of the caller's own review (never splitting a character); null (left out)
    /// without a review.
    /// </summary>
    public string? TextSnippet { get; set; }

    /// <summary>
    /// Gets or sets when the row was created (UTC).
    /// </summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// Gets or sets when the row last changed (UTC).
    /// </summary>
    public DateTime UpdatedUtc { get; set; }
}
