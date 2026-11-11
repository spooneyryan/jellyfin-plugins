using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// The answer of <see cref="IdentityResolver.Resolve"/>.
/// </summary>
internal sealed record ResolveResult
{
    /// <summary>
    /// Gets what the consumer does: relink, leave detached as ambiguous, or leave detached.
    /// </summary>
    public required ResolveOutcome Outcome { get; init; }

    /// <summary>
    /// Gets how the relink target matched, or for a result that isn't a relink, the strongest candidate's strength.
    /// A consumer clears file-specific fields and shows "relinked, check positions" after a non-Exact relink.
    /// </summary>
    public required MatchStrength Strength { get; init; }

    /// <summary>
    /// Gets which step found the relink target, or the strongest candidate.
    /// </summary>
    public MatchSource Source { get; init; }

    /// <summary>
    /// Gets the item id to relink to; set only when <see cref="Outcome"/> is <see cref="ResolveOutcome.Relink"/>. With
    /// <see cref="IsPending"/> it is a journal <c>NewId</c> the library doesn't have yet.
    /// </summary>
    public Guid? ItemId { get; init; }

    /// <summary>
    /// Gets the item to relink to; null when <see cref="IsPending"/>.
    /// </summary>
    public BaseItem? Item { get; init; }

    /// <summary>
    /// Gets a value indicating whether the relink target is a pending one: a Committed move-journal line leads to an id
    /// that no scan has picked up yet, less than <see cref="MoveJournal.PendingWindow"/> ago. The consumer relinks the
    /// record at once, and its verify sweep leaves it alone while <see cref="MoveJournal.IsPendingRelink"/> says so.
    /// There is no item yet for the resolver's <c>isVisible</c> check, just as for a catch-up move; once the id is in
    /// the library, a record its owner can't see goes through the consumer's "lost access" path like any other.
    /// </summary>
    public bool IsPending { get; init; }

    /// <summary>
    /// Gets why Strong candidates were not applied, when <see cref="Outcome"/> is <see cref="ResolveOutcome.Ambiguous"/>.
    /// </summary>
    public AmbiguousReason Reason { get; init; }

    /// <summary>
    /// Gets the candidates found by the provider-id and name steps: the Strong ones first (at most
    /// <see cref="IdentityResolver.MaxStrongCandidates"/>), then the Weak ones (at most
    /// <see cref="IdentityResolver.MaxWeakCandidates"/>). Empty for an Exact match. With an <c>isVisible</c> check, only the
    /// items it accepts.
    /// </summary>
    public IReadOnlyList<ResolveCandidate> Candidates { get; init; } = [];

    /// <summary>
    /// Gets the move-journal chain the resolver followed from the snapshot's id, when the journal has one; also on a
    /// result that didn't come from the journal (a chain whose end the library doesn't have, past the pending window).
    /// </summary>
    public MoveChain? Chain { get; init; }

    /// <summary>
    /// Gets a value indicating whether the consumer should relink the record (<see cref="ResolveOutcome.Relink"/>).
    /// </summary>
    public bool ShouldRelink => Outcome == ResolveOutcome.Relink;
}
