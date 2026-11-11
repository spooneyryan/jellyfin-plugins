namespace Jellyfin.Plugin.Bookmarks.Model;

/// <summary>
/// Where a bookmark record came from (schema 2).
/// </summary>
/// <remarks>
/// Public because <c>BookmarkDto.Origin</c> exposes it; prefixed, so its schema id can't collide. All three values are
/// defined in 1.1.0, although only 1.3.0 writes <see cref="Adopted"/>, so no later release needs a schema change and a
/// downgraded 1.1 can read every record (an unknown value would make the file unreadable).
/// </remarks>
public enum BookmarkOrigin
{
    /// <summary>
    /// Made with Bookmarks: the web UI or the REST API. Every 1.0 record is migrated as native.
    /// </summary>
    Native,

    /// <summary>
    /// Imported from Jellyfin Enhanced's bookmarks.
    /// </summary>
    JellyfinEnhanced,

    /// <summary>
    /// A clip file found in a clip folder without a record and adopted by an admin (from 1.3.0). It has no library
    /// item: its <c>ItemId</c> is empty and no code looks it up.
    /// </summary>
    Adopted
}
