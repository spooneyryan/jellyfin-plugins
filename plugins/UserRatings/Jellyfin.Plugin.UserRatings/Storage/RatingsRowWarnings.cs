using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserRatings.Storage;

/// <summary>
/// The Warnings about stored values the store reads tolerantly: one per row and column for as long as
/// the plugin runs, however often the row is read, so a details page read on every view doesn't fill the log. Ids and
/// column names only.
/// </summary>
internal sealed class RatingsRowWarnings
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<(Guid RatingId, string Column), byte> _warned = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RatingsRowWarnings"/> class.
    /// </summary>
    /// <param name="logger">The store's logger.</param>
    public RatingsRowWarnings(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Reports a provider-id map that wasn't as the store writes it.
    /// </summary>
    /// <param name="ratingId">The rating.</param>
    /// <param name="column">The column (<c>ProviderIds</c> or <c>SeriesProviderIds</c>).</param>
    /// <param name="problem">What was wrong (<see cref="ProviderIdsJson.Read"/>).</param>
    /// <param name="kept">How many ids were kept.</param>
    /// <returns>Whether it was logged now (false when this row and column were reported before).</returns>
    public bool ProviderIds(Guid ratingId, string column, string problem, int kept)
    {
        if (!_warned.TryAdd((ratingId, column), 0))
        {
            return false;
        }

        _logger.LogWarning(
            "The rating {RatingId}'s stored {Column} {Problem}; it was read with {Kept} provider ids, and the next save of the rating writes them back as the store writes them",
            ratingId,
            column,
            problem,
            kept);
        return true;
    }
}
