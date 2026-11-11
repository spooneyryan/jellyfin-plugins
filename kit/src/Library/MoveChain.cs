using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// Where the move journal says an item id went: the Committed lines followed from <see cref="StartId"/>,
/// each the first line after the previous one (in <c>Seq</c> order) that moves the current id on.
/// </summary>
/// <remarks>
/// Every line is followed at most once and each step has a higher <c>Seq</c> than the one before, so the chain always
/// ends: a move back (A→B, later B→A) returns it to A, where it stops unless a still later line moves A again. This is
/// the journal's cycle stop.
/// </remarks>
/// <param name="StartId">The id the chain started from.</param>
/// <param name="EndId">Where it ends; <see cref="StartId"/> when there are no steps.</param>
/// <param name="Steps">The lines followed, in order.</param>
/// <param name="Revisits">Whether the chain came back to an id it had already passed (a move back).</param>
internal sealed record MoveChain(Guid StartId, Guid EndId, IReadOnlyList<MoveJournalLine> Steps, bool Revisits)
{
    /// <summary>
    /// Gets a value indicating whether the journal has no line from <see cref="StartId"/>.
    /// </summary>
    public bool IsEmpty => Steps.Count == 0;

    /// <summary>
    /// Gets the last line followed, or null.
    /// </summary>
    public MoveJournalLine? Last => Steps.Count == 0 ? null : Steps[^1];
}
