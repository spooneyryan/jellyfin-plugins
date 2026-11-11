using Jellyfin.Plugin.Bookmarks.Model;

namespace Jellyfin.Plugin.Bookmarks.Storage;

/// <summary>
/// What <see cref="IBookmarkStore.UpdateOrRemoveAsync"/> did to a record, as seen under the store's lock.
/// </summary>
/// <param name="Before">The record before the change, or <c>null</c> when there was none.</param>
/// <param name="After">The record after it: the same instance when nothing was written, the new one when it was
/// replaced, <c>null</c> when it was removed (or when there was none).</param>
internal readonly record struct BookmarkChange(Bookmark? Before, Bookmark? After)
{
    /// <summary>
    /// Gets a value indicating whether the record existed.
    /// </summary>
    public bool Found => Before is not null;

    /// <summary>
    /// Gets a value indicating whether the record was removed.
    /// </summary>
    public bool Removed => Before is not null && After is null;

    /// <summary>
    /// Gets a value indicating whether anything was written: the record was replaced or removed.
    /// </summary>
    public bool Changed => !ReferenceEquals(Before, After);
}
