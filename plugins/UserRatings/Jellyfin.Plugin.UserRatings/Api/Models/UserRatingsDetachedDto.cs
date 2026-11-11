using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// A detached rating in the Data tab's "Detached &amp; ambiguous" list. It names the item the rating
/// was on, as stored, and carries no review text.
/// </summary>
public sealed class UserRatingsDetachedDto
{
    /// <summary>Gets the rating's id.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the member's id.</summary>
    public required Guid UserId { get; init; }

    /// <summary>Gets the member's name; "Former member" for an anonymized one, "Deleted user" when the server has none.</summary>
    public required string UserName { get; init; }

    /// <summary>Gets the item the rating was on.</summary>
    public required Guid ItemId { get; init; }

    /// <summary>Gets the item's kind, as stored.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the item's name, as stored.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the item's year, as stored.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the series' name of an episode or season, as stored.</summary>
    public string? SeriesName { get; init; }

    /// <summary>Gets the season number, as stored.</summary>
    public int? Season { get; init; }

    /// <summary>Gets the episode number, as stored.</summary>
    public int? Episode { get; init; }

    /// <summary>Gets the stars (0.5 to 5), or null.</summary>
    public double? Stars { get; init; }

    /// <summary>Gets a value indicating whether the rating has a review.</summary>
    public bool HasReview { get; init; }

    /// <summary>Gets when it was detached.</summary>
    public required DateTime DetachedUtc { get; init; }

    /// <summary>Gets why: <c>Removed</c>, <c>Missing</c>, <c>Unmatched</c> or <c>Ambiguous</c>.</summary>
    public required string DetachReason { get; init; }

    /// <summary>Gets up to five items it could be relinked to, best first, in the member's view.</summary>
    public IReadOnlyList<UserRatingsDetachedCandidateDto> Candidates { get; init; } = [];
}
