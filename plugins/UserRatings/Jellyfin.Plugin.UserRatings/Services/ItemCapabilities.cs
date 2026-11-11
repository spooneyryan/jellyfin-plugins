namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// What a viewer may do on an item (<c>Can</c>): what a write would check now, in the write's order, so
/// <paramref name="Rate"/> false is a 403, 404 (an item no longer in the library) or 503 the write would give. The server
/// checks again on every write.
/// </summary>
/// <param name="Rate">Whether they may rate: ratings on, the store writable, <c>WhoCanRate</c> lets them, and, while
/// <c>RequirePlayedToRate</c> is on, they have played the item (when that can't be read just now, true: the write answers
/// its own 503).</param>
/// <param name="Review">Whether they may review: <paramref name="Rate"/> and reviews on.</param>
/// <param name="Moderate">Whether they may remove others' entries (an admin).</param>
/// <param name="Reason">Why <paramref name="Rate"/> is false: <see cref="RatingsService.ReadOnlyReason"/>,
/// <see cref="RatingsService.NotInLibraryReason"/>, <see cref="RatingsService.NotARaterReason"/> or
/// <see cref="RatingsService.NeedsPlayReason"/>; null while it is true.</param>
internal sealed record ItemCapabilities(bool Rate, bool Review, bool Moderate, string? Reason = null);
