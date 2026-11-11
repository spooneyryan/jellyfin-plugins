using System;

namespace Jellyfin.Plugin.SmartSearch.Query;

/// <summary>A rule's date value.</summary>
/// <param name="Utc">For a day, its UTC midnight (only the date counts); for an instant, the instant in UTC.</param>
/// <param name="IsDay">Whether the value is a calendar day (<c>yyyy-MM-dd</c>) rather than an instant.</param>
internal readonly record struct QueryDate(DateTime Utc, bool IsDay)
{
    /// <summary>Gets the calendar day of a day value.</summary>
    public DateOnly Day => DateOnly.FromDateTime(Utc);
}
