using System.IO;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The read-only file system calls <see cref="ClipFileGuard"/> makes to judge a clip path: whether the clip folder
/// is there, and the attributes of each step from it down to the file. <see cref="IClipFileSystem"/> has them with the
/// operations that change files; the My Clips list's <see cref="ClipStateCache"/> judges paths through a view of its own that
/// asks each folder and path once per request, keeps the verdicts for a while, and changes nothing.
/// </summary>
internal interface IClipPathProbe
{
    /// <summary>
    /// Gets a path's attributes.
    /// </summary>
    /// <param name="path">A full path.</param>
    /// <returns>The attributes, or <c>null</c> when nothing is there (the path, or a folder on the way, doesn't exist).</returns>
    /// <exception cref="IOException">The path can't be inspected, for example on a drive that isn't ready.</exception>
    /// <exception cref="System.UnauthorizedAccessException">The server may not read it.</exception>
    FileAttributes? GetAttributes(string path);

    /// <summary>
    /// Whether a folder is there, following a junction or a symbolic link to its target (a clip folder may be one).
    /// </summary>
    /// <param name="path">A full path.</param>
    /// <returns>Whether a folder is at the path; <c>false</c> when nothing is there (a drive letter that is gone, a folder
    /// moved or removed), a file is, a link's target isn't there, or it can't be reached at all (a drive that isn't ready,
    /// a share that is offline).</returns>
    bool FolderExists(string path);
}
