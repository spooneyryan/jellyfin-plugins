using System;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;

namespace Jellyfin.Plugin.UserRatings.Import;

/// <summary>
/// The times an import writes: a source's time ahead of this server's clock
/// (another server's clock running ahead, or local times read as UTC) is written as the import's own time, so no imported row
/// is newer than what is done here next, a member's delete among it. The ledger keeps the source's own time, so a re-import
/// of the same entry is still known; <see cref="Written"/> is the time that import wrote.
/// </summary>
internal static class ImportTimes
{
    /// <summary>
    /// The latest time an import writes: its own time, to the millisecond.
    /// </summary>
    /// <param name="nowUtc">The import's time.</param>
    /// <returns>The ceiling.</returns>
    public static DateTime Ceiling(DateTime nowUtc) => RatingsService.ToMilliseconds(nowUtc);

    /// <summary>
    /// A source's time, no later than the ceiling (a time that isn't ahead is kept as it is).
    /// </summary>
    /// <param name="value">The source's time.</param>
    /// <param name="ceiling">The ceiling (<see cref="Ceiling"/>).</param>
    /// <returns>The time to write.</returns>
    public static DateTime Clamp(DateTime value, DateTime ceiling) => IsAhead(value, ceiling) ? ceiling : value;

    /// <summary>
    /// Whether a source's time is ahead of the ceiling (so it is clamped).
    /// </summary>
    /// <param name="value">The source's time.</param>
    /// <param name="ceiling">The ceiling.</param>
    /// <returns>Whether it is clamped.</returns>
    public static bool IsAhead(DateTime value, DateTime ceiling) => Utc(value) > ceiling;

    // A time in UTC: one with an offset converted, one without taken as UTC (as RatingsService.ToMilliseconds).
    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    /// <summary>
    /// The time the import a ledger entry records wrote: the source's time, or the import's own when the source's was ahead.
    /// </summary>
    /// <param name="entry">The ledger entry.</param>
    /// <returns>The written time.</returns>
    public static DateTime Written(ImportLedgerRecord entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Clamp(entry.SourceUpdatedUtc, Ceiling(entry.ImportedUtc));
    }
}
