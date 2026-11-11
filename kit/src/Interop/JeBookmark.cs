namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// One Jellyfin Enhanced bookmark (JE's <c>BookmarkItem</c>, as in 12.8 to 12.12), with its values as JE wrote them.
/// </summary>
/// <param name="Key">The bookmark's key in the file (<c>Bm_&lt;epoch ms&gt;_...</c>).</param>
/// <param name="ItemId">The video's id (N format), or empty.</param>
/// <param name="TmdbId">The TMDb id, or empty.</param>
/// <param name="TvdbId">The TVDb id, or empty.</param>
/// <param name="MediaType"><c>movie</c> or <c>tv</c>.</param>
/// <param name="Name">The video's title when the bookmark was made.</param>
/// <param name="Timestamp">The position, in seconds (a float).</param>
/// <param name="Label">The user's label: the bookmark's name.</param>
/// <param name="CreatedAt">When it was made (ISO 8601, as JE wrote it).</param>
/// <param name="UpdatedAt">When it was last changed.</param>
/// <param name="SyncedFrom">Non-empty for JE's cross-version copies.</param>
/// <param name="SeasonNumber">The season, for an episode.</param>
/// <param name="EpisodeNumber">The episode, for an episode.</param>
internal sealed record JeBookmark(
    string Key,
    string? ItemId,
    string? TmdbId,
    string? TvdbId,
    string? MediaType,
    string? Name,
    double Timestamp,
    string? Label,
    string? CreatedAt,
    string? UpdatedAt,
    string? SyncedFrom,
    int? SeasonNumber,
    int? EpisodeNumber);
