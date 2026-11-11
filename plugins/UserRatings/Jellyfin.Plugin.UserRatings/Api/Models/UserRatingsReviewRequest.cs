using System;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// The body of <c>PUT /UserRatings/Items/{itemId}/Mine/Review</c>:
/// <c>{"Text":"…","IsSpoiler":false,"IfReviewUpdatedUtc":"…"}</c>, with at most one of the three preconditions.
/// </summary>
public sealed class UserRatingsReviewRequest
{
    /// <summary>
    /// Gets or sets the review. It is normalised (line endings, control characters, more than three blank lines in a row);
    /// empty after that, or longer than <c>MaxReviewLength</c>, is a 400.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the review contains spoilers; kept only while the server allows the flag.
    /// </summary>
    public bool IsSpoiler { get; set; }

    /// <summary>
    /// Gets or sets the caller's row's <c>UpdatedUtc</c> as they last read it (<c>Mine.UpdatedUtc</c>). When the row changed
    /// since (another window or device saved it, its stars too, or deleted it) the answer is 409 and nothing is saved. Left
    /// out, with no other precondition, the review replaces whatever is there. Not with <see cref="IfReviewUpdatedUtc"/> or
    /// <see cref="IfNoReview"/> (400).
    /// </summary>
    public DateTime? IfUpdatedUtc { get; set; }

    /// <summary>
    /// Gets or sets the caller's review's own time as they last read it (<c>Mine.ReviewUpdatedUtc</c>, which moves only when
    /// the text or the spoiler flag changes). When the review changed since (other words saved, the spoiler flag changed, or
    /// the review deleted, in another window or device) the answer is 409 and nothing is saved; a change of the stars alone
    /// is not one. Optional; not with
    /// <see cref="IfUpdatedUtc"/> or <see cref="IfNoReview"/> (400).
    /// </summary>
    public DateTime? IfReviewUpdatedUtc { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the caller expects to have no review of the item yet (a first review; stars
    /// alone are no review). When one is there (written in another window or device meanwhile) the answer is 409 and
    /// nothing is saved. Optional (false or left out: no such precondition); not with <see cref="IfUpdatedUtc"/> or
    /// <see cref="IfReviewUpdatedUtc"/> (400).
    /// </summary>
    public bool? IfNoReview { get; set; }
}
