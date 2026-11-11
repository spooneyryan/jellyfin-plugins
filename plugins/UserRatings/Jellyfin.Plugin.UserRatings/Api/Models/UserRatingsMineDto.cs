using System;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// The caller's own rating and review of an item (<c>Mine</c>). The caller always sees it, also when the Hide
/// settings keep it out of the summary.
/// </summary>
public sealed class UserRatingsMineDto
{
    /// <summary>
    /// Gets or sets the rating's id (for <c>DELETE /UserRatings/Mine/{ratingId}</c>).
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the stars, 0.5 to 5 in half steps; null (left out) for a review without stars.
    /// </summary>
    public double? Stars { get; set; }

    /// <summary>
    /// Gets or sets the review text as stored (normalised); null (left out) for stars without a review.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the review is marked as containing spoilers.
    /// </summary>
    public bool IsSpoiler { get; set; }

    /// <summary>
    /// Gets or sets when the row was created (UTC).
    /// </summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// Gets or sets when anything in the row last changed (UTC): the value to send back as <c>IfUpdatedUtc</c> with a review.
    /// </summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>
    /// Gets or sets when the review last changed (UTC): its text or its spoiler flag; the stars never
    /// move it. Null (left out) without a review.
    /// </summary>
    public DateTime? ReviewUpdatedUtc { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the review text was changed after it was first written.
    /// </summary>
    public bool Edited { get; set; }
}
