namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// What a Jellyfin Enhanced data file turned out to be.
/// </summary>
internal enum JeDataFormat
{
    /// <summary>There is no such file.</summary>
    Missing,

    /// <summary>A user's <c>bookmark.json</c>: <c>{"Bookmarks": {"&lt;key&gt;": {ItemId, Timestamp, ...}}}</c> (JE 12.8 to 12.12 <c>UserBookmark</c>).</summary>
    Bookmarks1,

    /// <summary><c>reviews.json</c>: <c>{"Reviews": {"&lt;userIdN&gt;:&lt;type&gt;:&lt;tmdbId&gt;": {UserId, TmdbId, Rating, ...}}}</c> (JE 12.8 to 12.12 <c>AllReviewsStore</c>).</summary>
    Reviews1,

    /// <summary>JSON of another shape: "unsupported JE format", never an exception.</summary>
    Unsupported,

    /// <summary>The file couldn't be read, is larger than 20 MB or isn't JSON.</summary>
    Unreadable
}
