using System;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// One row of the Moderation list (<c>GET /UserRatings/Admin/Reviews</c>): a member's rating or
/// review as moderation sees it, rows the Hide settings keep from members included. The item is described by the
/// row's stored snapshot, as it was when last attached; the author's name is read when the request is made.
/// </summary>
public sealed class UserRatingsAdminEntryDto
{
    /// <summary>
    /// Gets or sets the rating's id, as <c>DELETE /UserRatings/Admin/Reviews/{ratingId}</c> takes it.
    /// </summary>
    public Guid RatingId { get; set; }

    /// <summary>
    /// Gets or sets the author's user id (a former member's stand-in id after the Anonymize policy).
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the author's name: the user's name now, or a former member's ("Former member 2"); null (left out) for
    /// an orphan (a deleted user's id the KeepAsOrphan policy kept). Show it as text only.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Gets or sets what the author is: <c>Member</c>, <c>FormerMember</c> or <c>Orphan</c>.
    /// </summary>
    public string AuthorKind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the author is hidden from login screens (counted per HideReviewsFromHiddenUsers).
    /// </summary>
    public bool IsHiddenUser { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the author is disabled (counted per HideReviewsFromDisabledUsers).
    /// </summary>
    public bool IsDisabledUser { get; set; }

    /// <summary>
    /// Gets or sets the item's id (the last one, for a detached row).
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the item's name as stored with the row. Show it as text only.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item's kind (<c>Movie</c>, <c>Episode</c> …).
    /// </summary>
    public string ItemKind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item's year; null (left out) when unknown.
    /// </summary>
    public int? ProductionYear { get; set; }

    /// <summary>
    /// Gets or sets an episode's or season's series name; null (left out) otherwise.
    /// </summary>
    public string? SeriesName { get; set; }

    /// <summary>
    /// Gets or sets an episode's season number; null (left out) otherwise.
    /// </summary>
    public int? SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets an episode's number; null (left out) otherwise.
    /// </summary>
    public int? EpisodeNumber { get; set; }

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
    /// Gets or sets a value indicating whether the review's text was changed after it was first written.
    /// </summary>
    public bool Edited { get; set; }

    /// <summary>
    /// Gets or sets the row's state: <c>Attached</c> or <c>Detached</c>.
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets why a detached row is detached (<c>Removed</c>, <c>Missing</c>, <c>Unmatched</c>, <c>Ambiguous</c>); null
    /// (left out) for an attached row.
    /// </summary>
    public string? DetachReason { get; set; }

    /// <summary>
    /// Gets or sets when a detached row was detached (UTC); null (left out) for an attached row.
    /// </summary>
    public DateTime? DetachedUtc { get; set; }
}
