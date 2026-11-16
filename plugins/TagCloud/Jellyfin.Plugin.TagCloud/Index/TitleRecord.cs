using System;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.TagCloud.Index;

/// <summary>
/// A title the index holds: a movie or a series, with what the titles list shows of it and what it is sorted and
/// filtered by (<c>GET /TagCloud/Titles</c>'s sort, name filter and minimum resolution). All of it is columns of the
/// item's own row, read with the title at a full build and again at every patch, so it follows the library as the
/// tags do.
/// </summary>
/// <param name="Id">The item id.</param>
/// <param name="Name">The display name.</param>
/// <param name="SortName">The name it sorts by.</param>
/// <param name="Year">The production year, or null.</param>
/// <param name="Kind">Movie or Series.</param>
internal sealed record TitleRecord(Guid Id, string Name, string SortName, int? Year, BaseItemKind Kind)
{
    /// <summary>Gets the original title, or null.</summary>
    public string? OriginalTitle { get; init; }

    /// <summary>Gets the release date (the premiere date), or null.</summary>
    public DateTime? PremiereDate { get; init; }

    /// <summary>Gets when the title was added to the library, or null when unknown.</summary>
    public DateTime? DateCreated { get; init; }

    /// <summary>Gets the community rating, or null.</summary>
    public float? CommunityRating { get; init; }

    /// <summary>Gets the picture's width in pixels (a movie's; 0 when unknown or a series).</summary>
    public int Width { get; init; }

    /// <summary>Gets the picture's height in pixels (a movie's; 0 when unknown or a series).</summary>
    public int Height { get; init; }

    /// <summary>
    /// Gets the name and the original title folded for the name filter (<see cref="TitleText.Fold"/>), one per line, as
    /// <see cref="TitleReader.ToRecord"/> works it out once per title; null on a record made without it, which the filter
    /// folds when it reads it.
    /// </summary>
    public string? SearchText { get; init; }

    /// <summary>
    /// Gets the date the release-date sort uses: the premiere date, else the first of January of the year, else null.
    /// </summary>
    public DateTime? ReleaseDate => PremiereDate ?? (Year is >= 1 and <= 9999 ? new DateTime(Year.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null);
}
