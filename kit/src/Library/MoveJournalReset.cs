namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// Why a consumer's journal position was reset to 0 and the whole journal replayed.
/// </summary>
internal enum MoveJournalReset
{
    /// <summary>No reset: only the lines after the stored <c>LastMoveSeq</c> are new.</summary>
    None,

    /// <summary>
    /// The journal's <c>JournalId</c> differs from the stored one: Library Maintenance was reinstalled, its data folder
    /// restored, or the journal rebuilt by an import.
    /// </summary>
    NewJournalId,

    /// <summary>
    /// The journal's last <c>Seq</c> is below the stored <c>LastMoveSeq</c> under the same <c>JournalId</c>: its data folder
    /// was restored from an older backup.
    /// </summary>
    SeqBelowStored
}
