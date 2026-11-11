using System;
using System.IO;
using Jellyfin.Plugin.Kit.Hosting;

namespace Jellyfin.Plugin.Bookmarks.Storage;

/// <summary>
/// Locations of the plugin's files inside its data folder (the kit's <see cref="KitPaths"/>: <c>data/bookmarks</c>).
/// </summary>
internal sealed class BookmarkPaths
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkPaths"/> class.
    /// </summary>
    /// <param name="kitPaths">The plugin's data folder.</param>
    public BookmarkPaths(KitPaths kitPaths)
        : this(kitPaths?.Root ?? throw new ArgumentNullException(nameof(kitPaths)))
    {
    }

    // Private, so dependency injection only ever sees the KitPaths constructor.
    private BookmarkPaths(string root)
    {
        Root = root;
    }

    /// <summary>
    /// Gets the plugin's data folder.
    /// </summary>
    public string Root { get; }

    /// <summary>
    /// Gets the path of the bookmark database file.
    /// </summary>
    public string StoreFile => Path.Combine(Root, "bookmarks.json");

    /// <summary>
    /// Gets the folder clips are written to when no output folder is configured.
    /// </summary>
    public string DefaultClipsDirectory => Path.Combine(Root, "clips");

    /// <summary>
    /// Gets the snapshot folder of the plugin's stores: daily snapshots, and pre-operation ones in its
    /// <c>pre-op</c> sub-folder.
    /// </summary>
    public string SnapshotsDirectory => Path.Combine(Root, "snapshots");

    /// <summary>
    /// Gets the store file of the developer user data demo (behind <c>EnableUserDataDemo</c>).
    /// </summary>
    public string UserDataDemoFile => Path.Combine(Root, "userdata-demo.json");

    /// <summary>
    /// Gets the store file of the developer library demo (behind <c>EnableLibraryDemo</c>).
    /// </summary>
    public string LibraryDemoFile => Path.Combine(Root, "library-demo.json");

    /// <summary>
    /// Creates the paths of a data folder given directly, for a store opened outside the plugin's services (tests, tools).
    /// </summary>
    /// <param name="root">The data folder.</param>
    /// <returns>The paths.</returns>
    public static BookmarkPaths ForRoot(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        return new BookmarkPaths(root);
    }
}
