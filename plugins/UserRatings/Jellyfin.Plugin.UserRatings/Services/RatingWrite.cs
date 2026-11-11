using Jellyfin.Plugin.UserRatings.Domain;

namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// What a member's write left (<see cref="RatingsService"/>): their row as stored, and the write's generation (the store's
/// <c>Generation</c> once it committed).
/// </summary>
/// <param name="Mine">The member's row now, or <c>null</c> when they have none.</param>
/// <param name="Generation">The generation the write committed as; a write that changed nothing gives the next one.</param>
/// <param name="Changed">Whether anything was written (a clear of nothing writes nothing).</param>
internal sealed record RatingWrite(RatingRecord? Mine, long Generation, bool Changed = true);
