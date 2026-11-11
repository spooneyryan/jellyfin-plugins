using System.Collections.Generic;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// What User Ratings offers the caller, so the web client can show the right controls or hide itself
/// (<c>UserRatingsCapabilitiesDto</c>). While ratings are off on the server it is still 200, with <see cref="Enabled"/>
/// false, nothing the caller may do and no rateable kind.
/// </summary>
public sealed class UserRatingsCapabilitiesDto
{
    /// <summary>
    /// Gets or sets a value indicating whether ratings are on (<c>EnableRatings</c>); while off, every item read and write
    /// answers 403.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the caller passes Jellyfin's <c>RequiresElevation</c> policy on this request.
    /// The server checks again on every admin action.
    /// </summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the caller may rate at all: ratings on, <c>WhoCanRate</c> includes them, and
    /// the data writable. While <see cref="RequirePlayedToRate"/> is on that is only the items they have played: an item's
    /// <c>Can.Rate</c> and <c>Can.Reason</c> tell per item.
    /// </summary>
    public bool CanRate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether rating an item needs the caller to have played it (<c>RequirePlayedToRate</c>;
    /// for a series, season or album, any of its episodes or tracks). An item's <c>Can.Reason</c> is <c>NeedsPlay</c> until
    /// then.
    /// </summary>
    public bool RequirePlayedToRate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the caller may write reviews: <see cref="CanRate"/> and reviews on.
    /// </summary>
    public bool CanReview { get; set; }

    /// <summary>
    /// Gets or sets the longest review, in UTF-16 code units (as a browser's <c>maxlength</c> counts).
    /// </summary>
    public int MaxReviewLength { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a review can be marked as containing spoilers.
    /// </summary>
    public bool AllowSpoilerFlag { get; set; }

    /// <summary>
    /// Gets or sets the item kinds members can rate (Jellyfin's <c>BaseItemKind</c> names); empty while ratings are off.
    /// </summary>
    public IReadOnlyList<string> RateableItemTypes { get; set; } = [];

    /// <summary>
    /// Gets or sets the display settings of the details page and the posters.
    /// </summary>
    public UserRatingsDisplayDto Display { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the Top Rated page is there to link to: wanted (<c>EnableTopRatedPage</c>) and
    /// registered. False until the page exists (1.1).
    /// </summary>
    public bool TopRatedPageEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the ratings data is read-only (written by a newer plugin version, an upgrade
    /// that failed, damage, or a database that can't be opened); every write then answers 503, and reading goes on where it
    /// can.
    /// </summary>
    public bool StoreReadOnly { get; set; }
}
