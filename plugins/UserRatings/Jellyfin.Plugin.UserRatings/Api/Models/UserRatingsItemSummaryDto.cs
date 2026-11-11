using System.Collections.Generic;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// An item's member summary (<c>Summary</c>): the rows that count, which are the same for every viewer (
/// hidden or disabled authors under the Hide settings and unknown author ids don't count; former members do).
/// </summary>
public sealed class UserRatingsItemSummaryDto
{
    /// <summary>
    /// Gets or sets the exact average stars (0.5 to 5) of the counted rows with stars; null (left out) when none has stars.
    /// Whether it shows (<c>MinRatingsForAverage</c>) is the client's choice.
    /// </summary>
    public double? Average { get; set; }

    /// <summary>
    /// Gets or sets how many counted rows have stars.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Gets or sets how many counted rows have a review.
    /// </summary>
    public int ReviewCount { get; set; }

    /// <summary>
    /// Gets or sets the histogram: ten counts, index 0 for 0.5 stars to index 9 for 5 stars.
    /// </summary>
    public IReadOnlyList<int> Distribution { get; set; } = new int[10];
}
