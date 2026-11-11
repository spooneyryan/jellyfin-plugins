using System;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.UserRatings.Domain;

/// <summary>
/// One row of <c>Ratings</c>: one member's stars and review on one item, with the item's identity snapshot (the
/// kit's <see cref="IdentitySnapshot"/>: kind, name, year, provider ids, the series' ids and numbers, the container ids and
/// the admin-only path), so the row survives moves and rescans.
/// </summary>
internal sealed record RatingRecord
{
    /// <summary>Gets the row's id.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the member's user id (or a former member's id after <c>Anonymize</c>).</summary>
    public required Guid UserId { get; init; }

    /// <summary>Gets the item's identity; its <see cref="IdentitySnapshot.ItemId"/> is the row's <c>ItemId</c> (the last known
    /// one while detached), and its container ids are <c>SeriesId</c>, <c>SeasonId</c> and <c>AlbumId</c>.</summary>
    public required IdentitySnapshot Identity { get; init; }

    /// <summary>Gets the stars in half-star units, 1 to 10 (7 = 3.5 stars); null for a review without stars.</summary>
    public int? Stars2 { get; init; }

    /// <summary>Gets the review text, already normalised (<c>ReviewText</c>); null for stars without a review.</summary>
    public string? ReviewText { get; init; }

    /// <summary>Gets a value indicating whether the review is marked as containing spoilers.</summary>
    public bool IsSpoiler { get; init; }

    /// <summary>Gets when the row was created (UTC).</summary>
    public required DateTime CreatedUtc { get; init; }

    /// <summary>Gets when anything in the row last changed (UTC).</summary>
    public required DateTime UpdatedUtc { get; init; }

    /// <summary>Gets when the review text last changed (UTC); drives "(edited)".</summary>
    public DateTime? ReviewUpdatedUtc { get; init; }

    /// <summary>Gets how many times the review text was edited.</summary>
    public int EditCount { get; init; }

    /// <summary>Gets where the row came from (<see cref="Storage.RatingValues.Origin"/>).</summary>
    public string Origin { get; init; } = Storage.RatingValues.Origin.User;

    /// <summary>Gets when the row was detached from its item (UTC); null while attached.</summary>
    public DateTime? DetachedUtc { get; init; }

    /// <summary>Gets why the row is detached (<see cref="Storage.RatingValues.DetachReason"/>); null while attached.</summary>
    public string? DetachReason { get; init; }

    /// <summary>Gets the value last written into <c>UserData.Rating</c> by the mirror.</summary>
    public double? MirroredValue { get; init; }

    /// <summary>
    /// Gets when the row was last attached to its item by a relink (UTC; <c>M002_AttachedUtc</c>): the move-journal
    /// line's time, or when a library change relinked it. Null for a row on the item it was created on, which is attached
    /// since <see cref="CreatedUtc"/> (<see cref="AttachedSinceUtc"/>). A detached row keeps it. The kit follows only the
    /// journal lines after it (a path used again gives an old id again).
    /// </summary>
    public DateTime? AttachedUtc { get; init; }

    /// <summary>Gets when the row was attached to its current (or, while detached, last) item: <see cref="AttachedUtc"/>,
    /// else <see cref="CreatedUtc"/>.</summary>
    public DateTime AttachedSinceUtc => AttachedUtc ?? CreatedUtc;

    /// <summary>Gets the item id (<see cref="IdentitySnapshot.ItemId"/>).</summary>
    public Guid ItemId => Identity.ItemId;

    /// <summary>Gets a value indicating whether the row is attached to a library item.</summary>
    public bool IsAttached => DetachedUtc is null;
}
