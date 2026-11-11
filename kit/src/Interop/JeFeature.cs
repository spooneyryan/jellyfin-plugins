namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// A Jellyfin Enhanced feature one of our plugins replaces: the overlap banner and the switch-off checklist
/// are about it.
/// </summary>
internal enum JeFeature
{
    /// <summary>The plugin replaces nothing of Jellyfin Enhanced's.</summary>
    None,

    /// <summary>Jellyfin Enhanced's Bookmarks (<c>BookmarksEnabled</c>, its page, its Custom Tab, the <c>B</c> key), replaced by Bookmarks 1.1.</summary>
    Bookmarks,

    /// <summary>Jellyfin Enhanced's User Reviews (<c>ShowUserReviews</c>), replaced by User Ratings.</summary>
    Reviews
}
