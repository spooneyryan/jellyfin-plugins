namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// A clip folder the missing-clip sweep left out of a run (<see cref="MissingClipSweep"/>): how many clips it holds and
/// why. None of those clips was checked, marked, cleared, archived or forgotten. An admin can confirm a folder left out
/// for any reason but <see cref="MarkerNotWritten"/> as the clip folder whose clips are gone
/// (<see cref="ClipFileOps.ConfirmFolderAsync"/>).
/// </summary>
/// <param name="Clips">How many clips with a file it holds.</param>
/// <param name="Reason">Why: <see cref="NotThere"/>, <see cref="NoMarker"/>, <see cref="Unproven"/>,
/// <see cref="MarkerNotWritten"/> or <see cref="AllMissing"/>.</param>
internal sealed record ClipFolderLeftOut(int Clips, string Reason)
{
    /// <summary>The folder isn't there (an unplugged drive, a folder moved or removed, a share that can't be reached), for one
    /// of its clips or when the run looked again after its files.</summary>
    public const string NotThere = "NotThere";

    /// <summary>A folder is there, but neither its marker file (<see cref="ClipRootMarker"/>, with the token recorded for it)
    /// nor any of its clips: not recognised as the folder they were made in (an empty mount point, a disk or share that isn't
    /// mounted, another drive under the same letter, a folder made again while the real one is away).</summary>
    public const string NoMarker = "NoMarker";

    /// <summary>A folder is there without its marker file (with the token recorded for it), with some of its clips but not all
    /// of them: not proven to be their folder, since a folder in its place can hold a clip written there while the real
    /// one was away (Bookmarks 1.1's clip worker made the folder again), or an older copy of the folder holds only its
    /// older clips. The backfill writes a marker only when none of a folder's clips is gone.</summary>
    public const string Unproven = "Unproven";

    /// <summary>The folder is there with its clips and none gone, but its missing marker file couldn't be written (no
    /// permission, a read-only share): not recognised either, until it can be.</summary>
    public const string MarkerNotWritten = "MarkerNotWritten";

    /// <summary>The folder is there with its marker file, but every one of its clips is missing at once, at least
    /// <see cref="MissingClipSweep.AllMissingThreshold"/> of them there before (not marked missing): a folder emptied, or
    /// put back from an old copy, rather than files deleted one by one.</summary>
    public const string AllMissing = "AllMissing";
}
