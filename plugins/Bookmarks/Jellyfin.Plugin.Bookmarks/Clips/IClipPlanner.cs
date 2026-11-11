using Jellyfin.Plugin.Bookmarks.Model;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Turns a bookmark into a runnable clip job.
/// </summary>
internal interface IClipPlanner
{
    /// <summary>
    /// Resolves the source media and output location for a clip of the bookmark's range.
    /// </summary>
    /// <param name="bookmark">The bookmark; its start and end define the clip.</param>
    /// <param name="mode">The extraction mode.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ClipExtractionException">The bookmark can't be clipped; the message explains why.</exception>
    ClipPlan Plan(Bookmark bookmark, BookmarkClipMode mode);
}
