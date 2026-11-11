namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// Which rows the Moderation list shows (<c>Admin/Reviews?state=</c>).
/// </summary>
public enum UserRatingsModerationState
{
    /// <summary>Every row (the default).</summary>
    All,

    /// <summary>Rows attached to an item.</summary>
    Attached,

    /// <summary>Detached rows (their item is gone or not found).</summary>
    Detached
}
