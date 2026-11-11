using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// An item's member summary (<c>Summary</c>): the counted rows' average (exact, in stars: 3.5 and 4.0 give
/// 3.75), how many have stars and how many have a review, and the 10-bucket histogram (<c>Distribution[0]</c> is 0.5 stars,
/// <c>[9]</c> 5 stars). The same for every viewer: <see cref="VisibilityPolicy.Summarize"/> builds it from the rows
/// the policy counts. Whether the average shows (<c>MinRatingsForAverage</c>) is the display's choice.
/// </summary>
internal sealed record RatingSummary
{
    private static readonly int[] _noRatings = new int[10];

    private RatingSummary(int count, int reviewCount, double? average, int[] distribution)
    {
        Count = count;
        ReviewCount = reviewCount;
        Average = average;
        Distribution = distribution;
    }

    /// <summary>Gets the summary of no rating.</summary>
    public static RatingSummary Empty { get; } = new(0, 0, null, _noRatings);

    /// <summary>Gets how many counted rows have stars.</summary>
    public int Count { get; }

    /// <summary>Gets how many counted rows have a review.</summary>
    public int ReviewCount { get; }

    /// <summary>Gets the average stars of the counted rows with stars, 0.5 to 5; <c>null</c> when none has stars.</summary>
    public double? Average { get; }

    /// <summary>Gets how many counted rows have each half step: index 0 for 0.5 stars to index 9 for 5.</summary>
    public IReadOnlyList<int> Distribution { get; }

    /// <summary>
    /// Builds the summary of counted contributions.
    /// </summary>
    /// <param name="rows">Each counted row's half-star units (null without stars) and whether it has a review.</param>
    /// <returns>The summary.</returns>
    public static RatingSummary Of(IEnumerable<(int? Stars2, bool HasText)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var distribution = new int[10];
        int count = 0, reviews = 0;
        long sum = 0;
        foreach (var (stars2, hasText) in rows)
        {
            if (hasText)
            {
                reviews++;
            }

            if (stars2 is { } s and >= 1 and <= 10)
            {
                count++;
                sum += s;
                distribution[s - 1]++;
            }
        }

        return count == 0 && reviews == 0
            ? Empty
            : new RatingSummary(count, reviews, count == 0 ? null : sum / 2.0 / count, distribution);
    }

    /// <inheritdoc />
    public bool Equals(RatingSummary? other)
        => other is not null
            && Count == other.Count
            && ReviewCount == other.ReviewCount
            && Nullable.Equals(Average, other.Average)
            && Distribution.SequenceEqual(other.Distribution);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Count, ReviewCount, Average, Distribution.Aggregate(17, (h, n) => (h * 31) + n));
}
