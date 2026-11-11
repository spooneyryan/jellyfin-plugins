using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// The move-journal lines a consumer hasn't applied yet, and the position to store once it has
/// (<see cref="MoveJournalReader.CatchUp(Guid?, long)"/>).
/// </summary>
/// <remarks>
/// The consumer applies <see cref="Lines"/> in order, each only to records whose item id (or container id) still equals
/// the line's <c>OldId</c>, which makes a replay idempotent, then stores <see cref="JournalId"/> and
/// <see cref="LastMoveSeq"/> with its data. A relinked record whose new id the library doesn't have yet is a pending
/// relink: its verify sweep leaves it alone while <see cref="MoveJournal.IsPendingRelink"/> says so.
/// </remarks>
/// <param name="Journal">The journal as read.</param>
/// <param name="Lines">The Committed lines to apply, in <c>Seq</c> order.</param>
/// <param name="JournalId">The journal id to store (the stored one when the journal isn't <see cref="MoveJournalStatus.Ok"/>).</param>
/// <param name="LastMoveSeq">The position to store.</param>
/// <param name="Reset">Why the stored position was reset and the journal replayed from the start, if it was.</param>
internal sealed record MoveJournalCatchUp(
    MoveJournal Journal,
    IReadOnlyList<MoveJournalLine> Lines,
    Guid? JournalId,
    long LastMoveSeq,
    MoveJournalReset Reset)
{
    /// <summary>
    /// Checks whether applying the catch-up changes anything the consumer stores: new lines, a reset or a first journal.
    /// </summary>
    /// <param name="storedJournalId">The consumer's stored <c>MoveJournalId</c>.</param>
    /// <param name="storedLastMoveSeq">The consumer's stored <c>LastMoveSeq</c>.</param>
    /// <returns>Whether the consumer has lines to apply or a position to store.</returns>
    public bool HasChanges(Guid? storedJournalId, long storedLastMoveSeq)
        => JournalId != storedJournalId || LastMoveSeq != storedLastMoveSeq || Lines.Count > 0;
}
