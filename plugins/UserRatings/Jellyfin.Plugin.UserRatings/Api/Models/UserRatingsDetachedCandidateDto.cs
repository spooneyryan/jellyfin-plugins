using System;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// An item a detached rating could be relinked to.
/// </summary>
public sealed class UserRatingsDetachedCandidateDto
{
    /// <summary>Gets the item's id.</summary>
    public required Guid ItemId { get; init; }

    /// <summary>Gets the item's name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the item's kind.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the item's year.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the series' name of an episode or season.</summary>
    public string? SeriesName { get; init; }

    /// <summary>Gets how strongly it matches: <c>Strong</c>, <c>Weak</c>, ….</summary>
    public required string Strength { get; init; }

    /// <summary>Gets a value indicating whether the member already has an attached rating there (Relink would be refused).</summary>
    public bool MemberRatesIt { get; init; }
}
