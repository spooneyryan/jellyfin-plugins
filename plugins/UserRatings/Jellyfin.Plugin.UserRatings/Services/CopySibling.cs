using System;

namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// Another copy of an item with ratings the viewer may see (<c>OtherCopies</c>).
/// </summary>
/// <param name="ItemId">The copy.</param>
/// <param name="LibraryName">Its library among the viewer's, or <c>null</c>.</param>
/// <param name="Summary">Its summary, as every viewer sees it.</param>
/// <param name="MyStars">The viewer's own stars on it, or <c>null</c>.</param>
internal sealed record CopySibling(Guid ItemId, string? LibraryName, RatingSummary Summary, decimal? MyStars);
