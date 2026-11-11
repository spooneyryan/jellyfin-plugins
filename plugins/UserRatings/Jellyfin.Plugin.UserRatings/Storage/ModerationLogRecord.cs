using System;

namespace Jellyfin.Plugin.UserRatings.Storage;

/// <summary>
/// One row of the append-only moderation log (<c>ModerationLog</c>).
/// </summary>
internal sealed record ModerationLogRecord
{
    /// <summary>Gets the row's number; 0 before it is written.</summary>
    public long Id { get; init; }

    /// <summary>Gets when it happened (UTC).</summary>
    public required DateTime AtUtc { get; init; }

    /// <summary>Gets the acting admin; null for an API key or the system.</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>Gets what happened (<see cref="RatingValues.ModerationAction"/>).</summary>
    public required string Action { get; init; }

    /// <summary>Gets the rating concerned, if one.</summary>
    public Guid? RatingId { get; init; }

    /// <summary>Gets the member concerned, if one.</summary>
    public Guid? TargetUserId { get; init; }

    /// <summary>Gets the item concerned, if one.</summary>
    public Guid? ItemId { get; init; }

    /// <summary>Gets the moderator's reason.</summary>
    public string? Reason { get; init; }

    /// <summary>Gets the removed text, kept until <see cref="SnapshotExpiresUtc"/>.</summary>
    public string? TextSnapshot { get; init; }

    /// <summary>Gets when <see cref="TextSnapshot"/> is nulled (UTC).</summary>
    public DateTime? SnapshotExpiresUtc { get; init; }

    /// <summary>Gets JSON counts for bulk actions.</summary>
    public string? Details { get; init; }
}
