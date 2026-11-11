namespace Jellyfin.Plugin.UserRatings.Storage;

/// <summary>
/// What <c>userratings.db</c> holds, counted (Diagnostics <c>Records</c>).
/// </summary>
/// <param name="Attached">Attached ratings.</param>
/// <param name="Detached">Detached ratings.</param>
/// <param name="Reviews">Attached ratings with a review.</param>
/// <param name="Users">Distinct authors of attached ratings.</param>
/// <param name="Tombstones">Tombstones.</param>
/// <param name="ModerationLog">Moderation log rows.</param>
/// <param name="ImportLedger">Imported source entries.</param>
/// <param name="DetachedJeWithKeySuffix">Detached Jellyfin Enhanced ratings whose stored TMDb id carries a season or episode
/// suffix, as 0.3.21 kept them (0 expected).</param>
internal sealed record RatingsStoreCounts(long Attached, long Detached, long Reviews, long Users, long Tombstones, long ModerationLog, long ImportLedger, long DetachedJeWithKeySuffix = 0);
