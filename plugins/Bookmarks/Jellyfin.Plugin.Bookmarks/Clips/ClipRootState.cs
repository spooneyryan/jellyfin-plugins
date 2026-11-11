namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What <see cref="ClipFileOps.CheckRoot(string)"/> found for a recorded clip folder.
/// </summary>
internal enum ClipRootState
{
    /// <summary>Nothing is there (an unplugged drive whose letter is gone, a folder moved or removed, a share that can't be
    /// reached, a link to a drive that isn't there), or the folder can't be looked at.</summary>
    Absent,

    /// <summary>A folder is there, but without a marker file that names it and the token recorded for it
    /// (<see cref="ClipRootMarker"/>): perhaps not the folder its clips were made in (an empty mount point, a disk or share that
    /// isn't mounted, another drive under the same letter, a folder made again, an older drive or a backup copy back under the
    /// same path), or one of Bookmarks 1.1, which wrote none.</summary>
    Unmarked,

    /// <summary>The folder is there with its marker file, naming the token recorded for it: the folder its clips were made
    /// in.</summary>
    Marked,

    /// <summary>The folder is there with a marker file that names it, and no token is recorded for the folder (a marker of an
    /// earlier build, which had none, or one whose token the store lost): taken for the folder once, as it was before the
    /// token, and the sweep or the clip worker records its token, giving it one first if it has none
    /// (<see cref="ClipFileOps.WriteRootMarkerAsync"/>). Only <see cref="Marked"/> lets the retention act.</summary>
    Adoptable
}
