namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What one pass over the clip file renames that weren't finished did (<see cref="ClipFileOps.FinishRenamesAsync"/>):
/// each entry of <see cref="ClipRenameJournal"/> is one of these, and every one but <see cref="Waiting"/> is dropped.
/// </summary>
/// <param name="Written">The record named the new path already, the file there (the server stopped after writing it, before
/// the entry went).</param>
/// <param name="Completed">The file had moved and the record still named its old path: the record now names the new one
/// (and has the name the rename gave it, while nothing changed the record since).</param>
/// <param name="NotMoved">The file was still (or again, after a power cut that lost the rename) at its old path: the record
/// names it.</param>
/// <param name="MovedOn">The record is gone or names another file now (deleted, made again, restored): nothing changed; a
/// renamed file no record names stays where it is, with a Warning.</param>
/// <param name="Lost">Neither path holds the file (deleted by hand meanwhile), in a clip folder recognised as the clips' own:
/// nothing changed; the record keeps its path, which the missing-clip sweep then judges.</param>
/// <param name="Waiting">Its clip folder can't be reached now, or is there without being recognised while the file is at
/// neither path, or the record couldn't be written: kept for the next pass, both paths still held.</param>
internal sealed record ClipRenameRecovery(int Written, int Completed, int NotMoved, int MovedOn, int Lost, int Waiting)
{
    /// <summary>
    /// Gets the pass that found nothing to finish.
    /// </summary>
    public static ClipRenameRecovery None { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>
    /// Gets how many journal entries can't be read: each holds its clip, as one waiting does, until an admin
    /// removes it.
    /// </summary>
    public int Unreadable { get; init; }

    /// <summary>
    /// Gets how many entries the pass looked at, those that can't be read included.
    /// </summary>
    public int Total => Written + Completed + NotMoved + MovedOn + Lost + Waiting + Unreadable;
}
