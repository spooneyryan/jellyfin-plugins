using System;

namespace Jellyfin.Plugin.UserRatings.Storage;

/// <summary>
/// One member's contribution to an item's summary (the summary query): per-user rows rather than totals, so the
/// rows the summary doesn't count (hidden or disabled authors under the Hide settings, orphaned ids) can be left out at read time.
/// </summary>
/// <param name="ItemId">The item.</param>
/// <param name="UserId">The member.</param>
/// <param name="Stars2">The stars in half-star units; null for a review without stars.</param>
/// <param name="HasText">Whether the row has a review.</param>
internal sealed record SummaryRow(Guid ItemId, Guid UserId, int? Stars2, bool HasText);
