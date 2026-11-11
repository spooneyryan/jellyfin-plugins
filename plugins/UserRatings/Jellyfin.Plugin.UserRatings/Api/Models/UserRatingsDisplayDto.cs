namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// The display settings of the details page and the posters (<c>Display</c>).
/// </summary>
public sealed class UserRatingsDisplayDto
{
    /// <summary>
    /// Gets or sets a value indicating whether the media-info row shows the member chip.
    /// </summary>
    public bool ShowAverageChip { get; set; }

    /// <summary>
    /// Gets or sets where the Rate button shows: <c>Auto</c> (series, season and album pages), <c>Always</c> or <c>Never</c>.
    /// </summary>
    public string ShowDetailButton { get; set; } = "Auto";

    /// <summary>
    /// Gets or sets a value indicating whether the section starts expanded.
    /// </summary>
    public bool SectionExpandedByDefault { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the section shows the histogram.
    /// </summary>
    public bool ShowHistogram { get; set; }

    /// <summary>
    /// Gets or sets how many ratings an item needs before its average shows (1 to 10).
    /// </summary>
    public int MinRatingsForAverage { get; set; }

    /// <summary>
    /// Gets or sets how many reviews a page of the list holds (5 to 50).
    /// </summary>
    public int ReviewsPageSize { get; set; }

    /// <summary>
    /// Gets or sets the poster badges' settings (1.1).
    /// </summary>
    public UserRatingsPostersDto Posters { get; set; } = new();
}
