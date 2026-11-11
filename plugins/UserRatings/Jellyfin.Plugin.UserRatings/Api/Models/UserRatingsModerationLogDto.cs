using System;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// One row of the moderation log (<c>GET /UserRatings/Admin/ModerationLog</c>). The log itself holds
/// ids, counts and reason codes only; the names here are read when the request is made, so a user deleted since shows
/// none, and an item's name only when the caller can open the item.
/// </summary>
public sealed class UserRatingsModerationLogDto
{
    /// <summary>
    /// Gets or sets the row's number (newer rows have higher numbers).
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets when it happened (UTC).
    /// </summary>
    public DateTime AtUtc { get; set; }

    /// <summary>
    /// Gets or sets the admin who acted; null (left out) for an API key or the server itself.
    /// </summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>
    /// Gets or sets the acting admin's name now; null (left out) when there is none.
    /// </summary>
    public string? ActorName { get; set; }

    /// <summary>
    /// Gets or sets what happened: <c>RemoveText</c>, <c>RemoveEntry</c>, <c>UserDeleted</c>, <c>Detach</c> ….
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the rating it was about; null (left out) for a user's or a bulk action.
    /// </summary>
    public Guid? RatingId { get; set; }

    /// <summary>
    /// Gets or sets the member it was about; null (left out) when none.
    /// </summary>
    public Guid? TargetUserId { get; set; }

    /// <summary>
    /// Gets or sets that member's name now (a former member's stored name); null (left out) when there is none.
    /// </summary>
    public string? TargetName { get; set; }

    /// <summary>
    /// Gets or sets the item it was about; null (left out) when none.
    /// </summary>
    public Guid? ItemId { get; set; }

    /// <summary>
    /// Gets or sets the item's name now, when the caller can open it; null (left out) otherwise.
    /// </summary>
    public string? ItemName { get; set; }

    /// <summary>
    /// Gets or sets the reason: a moderator's code (<c>Spoiler</c>, <c>Offensive</c>, <c>Spam</c>, <c>OffTopic</c>,
    /// <c>Requested</c>, <c>Other</c>), or the server's own for a lifecycle row; null (left out) when none.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets the row's counts as JSON (a removal's <c>{"Stars":1,"Reviews":1,"Deleted":1}</c>); null (left out)
    /// when none.
    /// </summary>
    public string? Details { get; set; }
}
