namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What <see cref="ClipFileGuard"/> found for a path.
/// </summary>
internal enum ClipPathVerdict
{
    /// <summary>A clip file in a clip folder, and it exists.</summary>
    Exists,

    /// <summary>A clip file's path in a clip folder that is there, with nothing at the path (the file, or a folder between
    /// the clip folder and it, doesn't exist).</summary>
    Missing,

    /// <summary>Refused: blank, not a path, or a device path (<c>\\?\</c>, <c>\\.\</c>).</summary>
    NotAPath,

    /// <summary>Refused: not a name Bookmarks gives a clip file (<c>.mp4</c> or <c>.mkv</c>, or its <c>.partial</c> where that is
    /// allowed).</summary>
    NotAClipFile,

    /// <summary>Refused: under no recorded clip folder (<c>ClipRoots</c>), or the folder itself.</summary>
    OutsideClipFolders,

    /// <summary>Refused: the file, or a folder between its clip folder and it, is a reparse point (a junction, a symbolic link,
    /// a mount point).</summary>
    ReparsePoint,

    /// <summary>Refused: a folder is where the file should be.</summary>
    NotAFile,

    /// <summary>The path couldn't be inspected: a drive that isn't ready, a folder the server may not read.</summary>
    Unreadable,

    /// <summary>The clip folder the path lies under isn't there (the clip shows as offline): an unplugged drive whose
    /// letter is gone, a folder moved or removed, a share that can't be reached, a link to a drive that isn't there. The file
    /// is probably fine where the folder is, so it is neither missing nor deleted, and nothing is touched.</summary>
    FolderUnavailable
}
