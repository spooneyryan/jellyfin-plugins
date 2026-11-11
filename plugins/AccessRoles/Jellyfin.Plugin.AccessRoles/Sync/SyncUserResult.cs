using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Policy;

namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// What the library sync planned, and did, for one user.
/// </summary>
/// <param name="User">The user, with their native access when the run read it.</param>
/// <param name="Kind">What the sync does.</param>
/// <param name="Reason">Why (<see cref="SyncReasons"/>, or a release's choice).</param>
/// <param name="Native">The user's library fields as the server enforced them when the run read them.</param>
/// <param name="Target">The library fields to write; <c>null</c> when nothing is to be written.</param>
internal sealed record SyncUserResult(ServerUser User, SyncActionKind Kind, string Reason, LibraryFields Native, LibraryFields? Target)
{
    /// <summary>Gets what the write did, once it ran; <c>null</c> in a plan, a dry run or a paused run.</summary>
    public PolicyWriteOutcome? Outcome { get; init; }

    /// <summary>Gets the fields an aborted or rebuilt write found changed.</summary>
    public IReadOnlyList<string> Differences { get; init; } = [];

    /// <summary>Gets a value indicating whether the write is made even if the user manager's object already shows it.</summary>
    public bool Force { get; init; }

    /// <summary>Gets the effective rules the action came from.</summary>
    public EffectiveAccess? Effective { get; init; }

    /// <summary>Gets what the policy writer reported, once the write ran.</summary>
    public PolicyWriteResult? Write { get; init; }

    /// <summary>
    /// Gets what the sync keeps, applies and reports for a drifted user under <c>DriftPolicy=ReportOnly</c>; its
    /// <see cref="DriftDecision.Write"/> is what such a user gets written.
    /// </summary>
    public DriftDecision? DriftDecision { get; init; }

    /// <summary>Gets a value indicating whether the action changed the user's native access.</summary>
    public bool Wrote => Outcome == PolicyWriteOutcome.Written;
}
