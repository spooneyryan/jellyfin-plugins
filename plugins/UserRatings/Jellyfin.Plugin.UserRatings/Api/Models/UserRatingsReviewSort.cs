namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// The order of a review list (<c>sort</c>). Ties go to the row changed last, then by id, so pages never overlap.
/// </summary>
public enum UserRatingsReviewSort
{
    /// <summary>The row changed last first (the default).</summary>
    Newest,

    /// <summary>The row changed first first.</summary>
    Oldest,

    /// <summary>The most stars first; reviews without stars last.</summary>
    Highest,

    /// <summary>The fewest stars first; reviews without stars last.</summary>
    Lowest
}
