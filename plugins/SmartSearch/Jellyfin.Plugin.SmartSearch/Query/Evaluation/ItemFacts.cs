using System;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SmartSearch.Query.Evaluation;

/// <summary>
/// The derived values some fields read: the year inside the validator's range, resolution buckets, runtime
/// in minutes, size in GB, bitrate in Mbps, ratings as stored, and dates as instants in UTC and as calendar days.
/// </summary>
internal static class ItemFacts
{
    /// <summary>The resolution choices, in the rule builder's order.</summary>
    public static readonly string[] Resolutions = ["SD", "720p", "1080p", "1440p", "2160p", "Unknown"];

    private const double BytesPerGb = 1024d * 1024 * 1024;
    private const double BitsPerMbit = 1_000_000d;

    /// <summary>
    /// Gets the production year, or <c>null</c> when it is missing or outside 1800–2100 (bad NFO data such as 0 or 9999
    /// counts as no year, and a missing year matches no positive comparison).
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The year.</returns>
    public static double? Year(BaseItem item)
        => item.ProductionYear is { } year && year >= QueryLimits.MinYear && year <= QueryLimits.MaxYear ? year : null;

    /// <summary>
    /// Gets the resolution bucket of a width: ≥3800 is 2160p, ≥2500 1440p, ≥1900 1080p, ≥1200 720p,
    /// any other positive width SD, and 0 (or less) Unknown. Derived from the width, so 1920×800 is 1080p.
    /// </summary>
    /// <param name="width">The width in pixels.</param>
    /// <returns>The bucket.</returns>
    public static string Resolution(int width) => width switch
    {
        >= 3800 => "2160p",
        >= 2500 => "1440p",
        >= 1900 => "1080p",
        >= 1200 => "720p",
        > 0 => "SD",
        _ => "Unknown"
    };

    /// <summary>
    /// Gets the runtime in whole minutes as jellyfin-web 10.11.5 shows it (<c>datetime.getDisplayDuration</c>,
    /// <c>Math.round(ticks / 6e8) || 1</c>): rounded half up (89.5 minutes is 90) and at least 1 for a known runtime under
    /// half a minute; or <c>null</c> when it is unknown (no ticks, which the web doesn't show).
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The minutes.</returns>
    public static double? RuntimeMinutes(BaseItem item)
        => item.RunTimeTicks is { } ticks and > 0 ? Math.Max(1, Math.Round((double)ticks / TimeSpan.TicksPerMinute, MidpointRounding.AwayFromZero)) : null;

    /// <summary>Gets the primary file's size in GB (1 GB = 1024³ bytes, as Windows shows sizes), or <c>null</c> when it is unknown.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The size.</returns>
    public static double? FileSizeGb(BaseItem item) => item.Size is { } bytes and > 0 ? bytes / BytesPerGb : null;

    /// <summary>Gets the total bitrate in Mbps (10⁶ bits per second), or <c>null</c> when it is unknown.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The bitrate.</returns>
    public static double? BitrateMbps(BaseItem item) => item.TotalBitrate is { } bits and > 0 ? bits / BitsPerMbit : null;

    /// <summary>
    /// Gets a rating as the database holds it: the <c>float</c> widened to a <c>double</c> (SQLite's <c>REAL</c>), so
    /// 7.1f is 7.099999904632568. A rule's number is rounded to a <c>float</c> the same way
    /// (<see cref="SinglePrecision"/>), so <c>Eq 7.1</c> and <c>Ge 7.1</c> match 7.1f and every comparison agrees with
    /// the stored value (ratings compare as stored).
    /// </summary>
    /// <param name="value">The rating.</param>
    /// <returns>The number, or <c>null</c>.</returns>
    public static double? Rating(float? value) => value is { } f && float.IsFinite(f) ? f : null;

    /// <summary>Rounds a rule's number to a <c>float</c>, as a rating is stored (see <see cref="Rating"/>).</summary>
    /// <param name="value">The rule's number.</param>
    /// <returns>The number at single precision, widened back to a <c>double</c>.</returns>
    public static double SinglePrecision(double value) => (float)value;

    /// <summary>
    /// Gets a date as an instant in UTC, as core's database holds it: a UTC time as it is, a local time converted, and a
    /// time without a kind taken as the server's local time (core's <c>DateTimeKindValueConverter</c> stores
    /// <c>ToUniversalTime()</c>, which treats it so; items read back from the database are UTC). An unset
    /// <c>DateCreated</c> (<see cref="DateTime.MinValue"/>) is no date.
    /// </summary>
    /// <param name="value">The date.</param>
    /// <param name="zone">The server's time zone.</param>
    /// <returns>The instant, or <c>null</c>.</returns>
    public static DateTime? Utc(DateTime? value, TimeZoneInfo zone) => value switch
    {
        null => null,
        { } d when d == DateTime.MinValue => null,
        { Kind: DateTimeKind.Utc } d => d,
        { Kind: DateTimeKind.Local } d => d.ToUniversalTime(),
        { } d => Shift(d, -zone.GetUtcOffset(d), DateTimeKind.Utc)
    };

    /// <summary>Gets the calendar day an instant falls on in the server's time zone (<c>DateCreated</c>, <c>LastPlayedDate</c>).</summary>
    /// <param name="utc">The instant.</param>
    /// <param name="zone">The server's time zone.</param>
    /// <returns>The day.</returns>
    public static DateOnly LocalDay(DateTime utc, TimeZoneInfo zone) => DateOnly.FromDateTime(Shift(utc, zone.GetUtcOffset(utc), DateTimeKind.Unspecified));

    /// <summary>
    /// Gets the day a calendar date (<c>PremiereDate</c>) names: a stored time of exactly 00:00:00 UTC is that UTC day
    /// (NFO files and most providers write <c>00:00Z</c>), and any other time is the server's local day (a date stored
    /// without a kind went through <c>ToUniversalTime()</c> as local midnight, so it is local midnight again here, on
    /// servers west and east of UTC alike).
    /// </summary>
    /// <param name="utc">The stored instant.</param>
    /// <param name="zone">The server's time zone.</param>
    /// <returns>The day.</returns>
    public static DateOnly CalendarDay(DateTime utc, TimeZoneInfo zone) => utc.TimeOfDay == TimeSpan.Zero ? DateOnly.FromDateTime(utc) : LocalDay(utc, zone);

    // Adds an offset, clamped to DateTime's range (the years 1 and 9999 are outside any rule's 1800-2100 anyway).
    private static DateTime Shift(DateTime value, TimeSpan offset, DateTimeKind kind)
    {
        var ticks = Math.Clamp(value.Ticks + offset.Ticks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
        return new DateTime(ticks, kind);
    }
}
