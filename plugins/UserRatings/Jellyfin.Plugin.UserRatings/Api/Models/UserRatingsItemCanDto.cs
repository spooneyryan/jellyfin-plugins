namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// What the caller may do on an item (<c>Can</c>): what a write would check now, in its order, so the UI never
/// offers a control whose save is a 403. The server checks again on every write.
/// </summary>
public sealed class UserRatingsItemCanDto
{
    /// <summary>
    /// Gets or sets a value indicating whether the caller may rate: ratings on, the ratings data writable, <c>WhoCanRate</c>
    /// includes them, and, while <c>RequirePlayedToRate</c> is on, they have played the item (when that can't be read just
    /// now it stays true, and the save answers 503 if it still can't).
    /// </summary>
    public bool Rate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the caller may write a review: <see cref="Rate"/> and reviews on.
    /// </summary>
    public bool Review { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the caller may remove other members' entries (an administrator, by
    /// Jellyfin's <c>RequiresElevation</c> policy on this request).
    /// </summary>
    public bool Moderate { get; set; }

    /// <summary>
    /// Gets or sets why <see cref="Rate"/> is false, for the note beside the read-only stars: <c>ReadOnly</c> (the ratings
    /// data is read-only), <c>NotInLibrary</c> (the library no longer has the item, which the server still answers from its
    /// cache: a deleted folder's album; every write on it is a 404), <c>NotARater</c> ("Rating is limited to selected
    /// members") or <c>NeedsPlay</c> ("Play this first: rating it needs it to be played"). Null (left out) while
    /// <see cref="Rate"/> is true.
    /// </summary>
    public string? Reason { get; set; }
}
