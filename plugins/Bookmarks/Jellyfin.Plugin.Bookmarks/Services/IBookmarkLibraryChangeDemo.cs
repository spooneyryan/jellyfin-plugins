using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// The developer demo of the kit's library-change monitor behind
/// <see cref="Api.BookmarksLibraryChangeDemoController"/>, turned on by the setting <c>EnableLibraryChangeDemo</c>. Public
/// only so the public controller can take it; its results are plain dictionaries, like the Diagnostics
/// body.
/// </summary>
public interface IBookmarkLibraryChangeDemo
{
    /// <summary>
    /// Lists the batches the demo consumer received while it was on, oldest first (at most the last 50).
    /// </summary>
    /// <returns><c>{Received, Batches}</c>.</returns>
    IDictionary<string, object?> Describe();

    /// <summary>
    /// Forgets the batches received so far.
    /// </summary>
    void Clear();
}
