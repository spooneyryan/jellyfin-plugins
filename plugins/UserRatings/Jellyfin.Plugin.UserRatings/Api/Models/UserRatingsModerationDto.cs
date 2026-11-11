using System;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// The answer of <c>DELETE /UserRatings/Admin/Reviews/{ratingId}</c>: what the removal takes
/// (a dry run, the default, with its <see cref="PreviewToken"/>) or took (the real run, which must send that token back as
/// <c>ifPreview</c>).
/// </summary>
public sealed class UserRatingsModerationDto
{
    /// <summary>
    /// Gets or sets a value indicating whether this was a dry run: nothing was changed.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Gets or sets the rating's id.
    /// </summary>
    public Guid RatingId { get; set; }

    /// <summary>
    /// Gets or sets the moderation-log action: <c>RemoveText</c> (Remove text only) or <c>RemoveEntry</c> (Remove rating and
    /// review).
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the author's user id.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the author's name now (a former member's stored name); null (left out) for an orphan. Show it as text
    /// only.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Gets or sets the item's id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the item's name as stored with the row. Show it as text only.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the removal takes the stars (only when it deletes the rating).
    /// </summary>
    public bool RemovesStars { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the removal takes a review (the row has one).
    /// </summary>
    public bool RemovesReview { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the rating is deleted, with a <c>Moderator</c> tombstone so no import brings it
    /// back: Remove rating and review, or Remove text only on a review without stars.
    /// </summary>
    public bool DeletesRating { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the ratings' archive holds earlier lines of this rating (it was archived and
    /// came back), which go too, with no copy.
    /// </summary>
    public bool ArchiveLines { get; set; }

    /// <summary>
    /// Gets or sets the token that binds the real run to what this dry run showed (<c>m1-</c> and 20 hex digits); the real
    /// run answers it again.
    /// </summary>
    public string PreviewToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the moderation-log row the real run wrote; null (left out) for a dry run.
    /// </summary>
    public long? LogId { get; set; }

    /// <summary>
    /// Gets or sets the pre-operation snapshot taken before the real run (kept 7 days); null (left out) for a dry run or
    /// when none could be taken.
    /// </summary>
    public string? SafetySnapshot { get; set; }
}
