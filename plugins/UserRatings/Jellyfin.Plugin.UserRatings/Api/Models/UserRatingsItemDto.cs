using System;
using System.Collections.Generic;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// An item's member ratings as the caller reads them (<c>UserRatingsItemDto</c>): the summary (the same for every
/// viewer), the caller's own rating and review, a page of the other members' rows the caller may see, the other copies with
/// ratings, and what the caller may do. Fields that are null are left out of the JSON (Jellyfin's options).
/// </summary>
/// <remarks>
/// A write (<c>PUT</c> or <c>DELETE</c> of <c>Mine/Rating</c> or <c>Mine/Review</c>) answers with the item read again after
/// it committed. When that read is a 503 (the database busy or read-only just then) the answer is still 200, from the
/// write's own row: <see cref="IsPartial"/> true, <see cref="Mine"/> as written, no <see cref="Summary"/> and no
/// <see cref="Reviews"/>. A 403 or 404 of that read (ratings turned off, the item's access taken away in between) is sent as
/// it is, although the write was saved.
/// </remarks>
public sealed class UserRatingsItemDto
{
    /// <summary>
    /// Gets or sets the item's id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether only <see cref="Mine"/> is current: a write's answer whose read of the item
    /// failed just then (503). The write was saved; keep the summary and the list shown, and read the item again later.
    /// </summary>
    public bool IsPartial { get; set; }

    /// <summary>
    /// Gets or sets the item's summary: average, counts and histogram of the rows that count, the same for every
    /// viewer. Null (left out) only when <see cref="IsPartial"/>.
    /// </summary>
    public UserRatingsItemSummaryDto? Summary { get; set; } = new();

    /// <summary>
    /// Gets or sets the caller's own rating and review of the item; null (left out) when they have none.
    /// </summary>
    public UserRatingsMineDto? Mine { get; set; }

    /// <summary>
    /// Gets or sets a page of the other members' rows the caller may see (never the caller's own), in the requested order;
    /// empty while reviews are off. Null (left out) only when <see cref="IsPartial"/>.
    /// </summary>
    public QueryResult<UserRatingsReviewDto>? Reviews { get; set; } = new();

    /// <summary>
    /// Gets or sets the other copies of the item (the same film in another library) that the caller can open and
    /// that have ratings, at most 3; empty when there are none.
    /// </summary>
    public IReadOnlyList<UserRatingsOtherCopyDto> OtherCopies { get; set; } = [];

    /// <summary>
    /// Gets or sets what the caller may do on this item.
    /// </summary>
    public UserRatingsItemCanDto Can { get; set; } = new();
}
