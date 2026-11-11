using System;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// Another member's rating or review of an item, as the caller may see it (<c>UserRatingsReviewDto</c>). The
/// author's name and avatar are read when the request is made, so a rename shows at once.
/// </summary>
public sealed class UserRatingsReviewDto
{
    /// <summary>
    /// Gets or sets the rating's id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the author's user id (a former member's stand-in id after the Anonymize policy).
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the author's name: the user's name now, or "Former member" (with a number) for a deleted user's rows
    /// kept by the Anonymize policy.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the tag of the author's profile image (as Jellyfin's <c>UserDto.PrimaryImageTag</c>); null (left out)
    /// without an image, for a former member, or while <c>ShowReviewerAvatars</c> is off.
    /// </summary>
    public string? UserImageTag { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the author is a former member.
    /// </summary>
    public bool IsFormerMember { get; set; }

    /// <summary>
    /// Gets or sets the stars, 0.5 to 5; null (left out) for a review without stars.
    /// </summary>
    public double? Stars { get; set; }

    /// <summary>
    /// Gets or sets the review text; null (left out) for stars without a review. Show it as text only, never as HTML.
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
    /// Gets or sets when the row last changed (UTC).
    /// </summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the review text was changed after it was first written.
    /// </summary>
    public bool Edited { get; set; }
}
