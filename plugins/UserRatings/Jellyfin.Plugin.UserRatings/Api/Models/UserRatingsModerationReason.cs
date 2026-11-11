namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// Why a moderator removed a rating or review (an optional reason): a code, never free text, so the
/// moderation log holds no words of the member's or the moderator's.
/// </summary>
public enum UserRatingsModerationReason
{
    /// <summary>The review gives the story away without the spoiler mark.</summary>
    Spoiler,

    /// <summary>The review is offensive.</summary>
    Offensive,

    /// <summary>The review is spam or advertising.</summary>
    Spam,

    /// <summary>The review isn't about the item.</summary>
    OffTopic,

    /// <summary>Its author asked for the removal.</summary>
    Requested,

    /// <summary>Another reason.</summary>
    Other
}
