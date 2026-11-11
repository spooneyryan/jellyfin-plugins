using System;
using System.Globalization;

namespace Jellyfin.Plugin.SmartSearch.Query;

/// <summary>
/// The date values of rules: <c>yyyy-MM-dd</c>, a whole calendar day, or an ISO 8601 date
/// and time with <c>Z</c> or an offset, an exact instant. A time without a zone is refused.
/// </summary>
internal static class QueryDates
{
    private static readonly string[] _utcFormats = ["yyyy-MM-dd'T'HH:mm'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];
    private static readonly string[] _offsetFormats = ["yyyy-MM-dd'T'HH:mmzzz", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];

    /// <summary>Reads a date value.</summary>
    /// <param name="text">The value.</param>
    /// <param name="date">The day, or the instant in UTC.</param>
    /// <returns>Whether it is a date value.</returns>
    public static bool TryParse(string text, out QueryDate date)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            date = new QueryDate(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), true);
            return true;
        }

        if (DateTimeOffset.TryParseExact(text, _utcFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc)
            || DateTimeOffset.TryParseExact(text, _offsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out utc))
        {
            date = new QueryDate(utc.UtcDateTime, false);
            return true;
        }

        date = default;
        return false;
    }

    /// <summary>
    /// Writes a date value canonically: a day as <c>yyyy-MM-dd</c>, an instant as its UTC time with <c>Z</c> (also at
    /// midnight, because an instant and a day mean different things).
    /// </summary>
    /// <param name="date">The date.</param>
    /// <returns>The text.</returns>
    public static string Format(QueryDate date)
        => date.IsDay
            ? date.Utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : date.Utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
}
