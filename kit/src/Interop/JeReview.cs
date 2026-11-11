namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// One Jellyfin Enhanced review (JE's <c>UserReview</c>, as in 12.8 to 12.12), with its values as JE wrote them.
/// </summary>
/// <param name="Key">The review's key (<c>&lt;userIdN&gt;:&lt;mediaType&gt;:&lt;tmdbKey&gt;</c>).</param>
/// <param name="UserId">The author (N format).</param>
/// <param name="TmdbId">The TMDb id.</param>
/// <param name="MediaType"><c>movie</c> or <c>tv</c>.</param>
/// <param name="Content">The text; empty for a rating without a review.</param>
/// <param name="Rating">The stars, in half steps; null for a review without a rating.</param>
/// <param name="CreatedAt">When it was written.</param>
/// <param name="UpdatedAt">When it was last changed.</param>
internal sealed record JeReview(string Key, string? UserId, string? TmdbId, string? MediaType, string? Content, double? Rating, string? CreatedAt, string? UpdatedAt);
