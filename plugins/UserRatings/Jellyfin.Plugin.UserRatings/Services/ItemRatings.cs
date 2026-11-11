using System.Collections.Generic;
using Jellyfin.Plugin.UserRatings.Configuration;
using Jellyfin.Plugin.UserRatings.Domain;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// An item's ratings as a viewer reads them (<c>UserRatingsItemDto</c>; the
/// <see cref="UserRatingsApi"/> makes the DTO, with names, avatars, the order and the page).
/// </summary>
/// <param name="Item">The item, as the viewer can open it.</param>
/// <param name="Summary">The summary, the same for every viewer.</param>
/// <param name="Mine">The viewer's own row, or <c>null</c>.</param>
/// <param name="Others">The other rows the viewer may see, newest change first; none while reviews are off.</param>
/// <param name="OtherCopies">The other copies with ratings the viewer may see.</param>
/// <param name="Can">What the viewer may do.</param>
internal sealed record ItemRatings(
    BaseItem Item,
    RatingSummary Summary,
    RatingRecord? Mine,
    IReadOnlyList<RatingRecord> Others,
    IReadOnlyList<CopySibling> OtherCopies,
    ItemCapabilities Can)
{
    /// <summary>Gets the settings the read used (one read per request), for the display's values.</summary>
    public required RatingsSettings Settings { get; init; }

    /// <summary>Gets the request's visibility policy, for the authors' kinds and former members' names.</summary>
    public required VisibilityPolicy Policy { get; init; }
}
