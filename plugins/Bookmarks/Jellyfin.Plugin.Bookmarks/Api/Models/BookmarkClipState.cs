namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// What a clip is right now, as the My Clips page shows it: worked out on each request
/// from the clip's job and its file, never stored. It is also the <c>state</c> filter of <c>GET /Bookmarks/Clips</c>.
/// </summary>
public enum BookmarkClipState
{
    /// <summary>
    /// The clip file is there, in its clip folder.
    /// </summary>
    Ready,

    /// <summary>
    /// A job is waiting or being made (<c>Queued</c> or <c>Running</c>); an earlier file, if any, stays until the new one is
    /// written.
    /// </summary>
    InProgress,

    /// <summary>
    /// There is no clip file: the job failed or was cancelled before it made one ("Clear the failed clip").
    /// </summary>
    Failed,

    /// <summary>
    /// The clip file is gone from a clip folder that is there ("File missing": deleted in Explorer or through Jellyfin).
    /// </summary>
    Missing,

    /// <summary>
    /// The clip's folder can't be reached ("Clip folder offline": an unplugged drive, a folder moved or removed, a share
    /// that doesn't answer), or its path isn't one Bookmarks may use (outside the recorded clip folders). The file is
    /// probably fine where it is, so it never counts as missing, and nothing deletes, renames or makes it again.
    /// </summary>
    FolderUnavailable,

    /// <summary>
    /// The clip file's last rename isn't finished ("Rename not finished"): the server stopped between the
    /// file's move and its record's write, or the write failed and the file couldn't be moved back. It is finished from what is
    /// on disk as soon as the clip folder can be reached and the data written (tried after a minute, then less often while nothing changes); until then nothing
    /// deletes, forgets, renames or makes the clip again, and it never counts as missing.
    /// </summary>
    RenameUnfinished
}
