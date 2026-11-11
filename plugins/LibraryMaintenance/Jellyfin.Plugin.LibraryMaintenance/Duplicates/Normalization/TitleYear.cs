using System;
using System.Globalization;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Normalization;

/// <summary>
/// Where a movie's year came from, kept for display.
/// </summary>
internal enum YearSource
{
    /// <summary>No year: the movie matches in Title + Year mode as the unknown-year policy says.</summary>
    Unknown = 0,

    /// <summary>The item's <c>ProductionYear</c>.</summary>
    ProductionYear,

    /// <summary>The year of the item's <c>PremiereDate</c>.</summary>
    PremiereDate,

    /// <summary>The year the title carried (N3's bracketed year or N10's release-name year).</summary>
    Title,
}

/// <summary>
/// A movie's year for the Title + Year key: <c>ProductionYear ?? PremiereYear ?? BracketYear</c>, else unknown.
/// </summary>
/// <param name="Year">The year, or <c>null</c> when unknown.</param>
/// <param name="Source">Where it came from.</param>
internal readonly record struct TitleYear(int? Year, YearSource Source)
{
    /// <summary>The earliest year taken as known; a smaller value (0, or <c>DateTime.MinValue</c>'s year 1) is unknown.</summary>
    public const int MinYear = 1800;

    /// <summary>The latest year taken as known.</summary>
    public const int MaxYear = 9999;

    /// <summary>Gets the unknown year.</summary>
    public static TitleYear Unknown => new(null, YearSource.Unknown);

    /// <summary>
    /// Picks a movie's year.
    /// </summary>
    /// <param name="productionYear">The item's <c>ProductionYear</c>.</param>
    /// <param name="premiereDate">The item's <c>PremiereDate</c>.</param>
    /// <param name="titleYear">The year its title carried (<see cref="NormalizedTitle.BracketYear"/>).</param>
    /// <returns>The first of the three that is a plausible year, with its source.</returns>
    public static TitleYear Resolve(int? productionYear, DateTime? premiereDate, int? titleYear)
        => Resolve(productionYear, premiereDate?.Year, titleYear);

    /// <summary>
    /// Picks a movie's year from its premiere date's year, as the index keeps it (<c>MovieRecord.PremiereYear</c>).
    /// </summary>
    /// <param name="productionYear">The item's <c>ProductionYear</c>.</param>
    /// <param name="premiereYear">The year of the item's <c>PremiereDate</c>.</param>
    /// <param name="titleYear">The year its title carried (<see cref="NormalizedTitle.BracketYear"/>).</param>
    /// <returns>The first of the three that is a plausible year, with its source.</returns>
    public static TitleYear Resolve(int? productionYear, int? premiereYear, int? titleYear)
    {
        if (IsYear(productionYear))
        {
            return new(productionYear, YearSource.ProductionYear);
        }

        if (IsYear(premiereYear))
        {
            return new(premiereYear, YearSource.PremiereDate);
        }

        return IsYear(titleYear) ? new(titleYear, YearSource.Title) : Unknown;
    }

    /// <summary>
    /// Gets the Title + Year key: the title key, <c>|</c>, and the year or <c>?</c> when it is unknown.
    /// </summary>
    /// <param name="titleKey">The title key (<see cref="NormalizedTitle.Key"/>).</param>
    /// <returns>The key.</returns>
    public string KeyFor(string titleKey)
        => titleKey + "|" + (Year is { } year ? year.ToString(CultureInfo.InvariantCulture) : "?");

    private static bool IsYear(int? year) => year is >= MinYear and <= MaxYear;
}
