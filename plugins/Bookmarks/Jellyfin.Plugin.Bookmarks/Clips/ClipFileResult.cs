namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What a <see cref="ClipFileOps"/> operation did.
/// </summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Check">The guard's verdict on the path (on the destination, for a move refused there).</param>
/// <param name="Attempts">How many times the file system was asked (0 when the guard refused, or nothing was there).</param>
/// <param name="Error">Why it failed, without the file's path or name (it holds the bookmark's name or note), for logs and
/// the job's error.</param>
/// <param name="Queued">Whether an automatic delete that couldn't delete the file listed it in <c>PendingFileDeletes</c>
/// (<see cref="ClipFileOps.DeleteOrQueueAsync"/>).</param>
/// <param name="WorthRetrying">Whether another attempt may succeed: the file was in use, or access was denied (which
/// Windows also answers while another handle keeps a deleted file).</param>
/// <param name="HResult">The failed file system call's <c>HResult</c>, when a move failed on one: the clip worker maps a full
/// disk (<c>0x80070070</c>, <c>0x80070027</c>) to the message "The clip drive is full".</param>
/// <param name="NameTaken">Whether a rename found its new name taken just then (a file there that wasn't when the name was
/// chosen): the renamer chooses again.</param>
internal sealed record ClipFileResult(ClipFileOutcome Outcome, ClipPathCheck Check, int Attempts = 0, string? Error = null, bool Queued = false, bool WorthRetrying = false, int? HResult = null, bool NameTaken = false)
{
    /// <summary>
    /// Gets a value indicating whether the file is gone: deleted now, or it wasn't there.
    /// </summary>
    public bool IsGone => Outcome is ClipFileOutcome.Done or ClipFileOutcome.Missing or ClipFileOutcome.NamedByAnotherRecord;
}
