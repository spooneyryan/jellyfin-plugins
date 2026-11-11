namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// A user's state as an author of ratings, read per request (core raises no event when a user's policy changes): <c>PermissionKind.IsHidden</c> and
/// <c>IsDisabled</c>.
/// </summary>
/// <param name="IsHidden">Whether the user is hidden.</param>
/// <param name="IsDisabled">Whether the user is disabled.</param>
internal readonly record struct RatingAuthor(bool IsHidden, bool IsDisabled);
