namespace Jellyfin.Plugin.TagCloud.Api.Models;

/// <summary>
/// What <c>GET /TagCloud/Titles</c> sorts a word's titles by (<c>sort=</c>), either way round (<c>descending=</c>). Ties go
/// by sort name, then year. A title without the value sorts last both ways.
/// </summary>
public enum TagCloudTitleSort
{
    /// <summary>By sort name, A–Z (Z–A descending).</summary>
    Name,

    /// <summary>By release date: the premiere date, else the production year; oldest first (newest descending).</summary>
    ReleaseDate,

    /// <summary>By when the title was added to the library, oldest first (newest descending).</summary>
    DateAdded,

    /// <summary>By community rating, lowest first (highest descending).</summary>
    Rating
}
