using System;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// Another copy of an item with ratings (<c>OtherCopies</c>): the same kind and a shared TMDb, IMDb or TVDb
/// id, in a library the caller can open. Its summary follows the same rules as the item's.
/// </summary>
public sealed class UserRatingsOtherCopyDto
{
    /// <summary>
    /// Gets or sets the copy's item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the name of the copy's library among the caller's libraries; null (left out) when it can't be told.
    /// </summary>
    public string? LibraryName { get; set; }

    /// <summary>
    /// Gets or sets the copy's average stars; null (left out) when no counted row has stars.
    /// </summary>
    public double? Average { get; set; }

    /// <summary>
    /// Gets or sets how many counted rows of the copy have stars.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Gets or sets the caller's own stars on the copy; null (left out) when they haven't rated it.
    /// </summary>
    public double? MyStars { get; set; }
}
