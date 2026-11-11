using System;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What <see cref="ClipFileGuard.Check"/> found.
/// </summary>
/// <param name="Verdict">The verdict.</param>
/// <param name="FullPath">The full path, when the path is one.</param>
/// <param name="Root">The clip folder it lies under, when it lies under one.</param>
/// <param name="Error">Why it couldn't be inspected (<see cref="ClipPathVerdict.Unreadable"/>).</param>
internal sealed record ClipPathCheck(ClipPathVerdict Verdict, string? FullPath, string? Root, Exception? Error = null)
{
    /// <summary>
    /// Gets a value indicating whether the path is a clip file's in a clip folder that is there, which may be touched: it
    /// exists or it doesn't.
    /// </summary>
    public bool IsInClipFolders => Verdict is ClipPathVerdict.Exists or ClipPathVerdict.Missing;

    /// <summary>
    /// Gets a value indicating whether the path is a clip file's in a clip folder that can't be reached now
    /// (<see cref="ClipPathVerdict.Unreadable"/>, <see cref="ClipPathVerdict.FolderUnavailable"/>): nothing is touched, a
    /// request answers 503, and the file counts neither as missing nor as deleted.
    /// </summary>
    public bool IsUnreachable => Verdict is ClipPathVerdict.Unreadable or ClipPathVerdict.FolderUnavailable;

    /// <summary>
    /// Gets a value indicating whether the path is refused: nothing may touch it.
    /// </summary>
    public bool IsRefused => !IsInClipFolders && !IsUnreachable;

    /// <summary>
    /// Gets a value indicating whether the path is <see cref="ClipPathVerdict.Missing"/> in a clip folder that is there but
    /// isn't recognised as the one its clips were made in: without a marker of its own (<see cref="ClipRootMarker"/>,
    /// <see cref="ClipRootState.Unmarked"/>), as an empty mount point, a share whose disk isn't mounted, another drive
    /// under the same letter or a folder made in place of the real one is. Such a file is not taken for gone: the My
    /// Clips list shows it as its folder offline (<see cref="ClipStateCache"/>), and the clip routes' deletes and
    /// Forget leave it as it is (<see cref="ClipFileOps.Unrecognised"/>, 503). The guard itself never sets it; they do,
    /// after one look at the folder's marker.
    /// </summary>
    public bool RootUnrecognised { get; init; }

    /// <summary>
    /// Gets a value indicating whether the path is one end of a rename of its clip file that is under way or wasn't finished
    /// (<see cref="ClipRenameJournal"/>): the file is being moved within its folder, or was moved and its record
    /// couldn't be written yet (a crash, a write that failed). <see cref="ClipFileOps.Check"/> reads such a path as
    /// <see cref="ClipPathVerdict.Unreadable"/> with this set, so nothing deletes, forgets or moves either end until the rename
    /// is finished (<see cref="ClipFileOps.FinishRenamesAsync"/>): a request answers 503 with
    /// <see cref="ClipFileOps.RenameUnfinishedMessage"/>, an automatic delete keeps it listed, the missing-clip sweep leaves it
    /// unmarked. The guard itself never sets it.
    /// </summary>
    public bool RenameUnfinished { get; init; }
}
