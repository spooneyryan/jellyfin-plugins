using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Policy;

namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// What a one-user run of the library sync did for a user (<see cref="LibrarySync.CorrectUserAsync"/>; the sign-in hook,
/// one of the library sync's triggers).
/// </summary>
/// <param name="Managed">Whether the user's libraries are managed (a write would apply): <c>false</c> for a user the sync
/// doesn't write (no assignment, not managed for libraries, the sync paused by an admin's choice), for whom nothing is
/// done.</param>
/// <param name="Result">What was planned and done, or <c>null</c> for a managed user already in sync.</param>
internal sealed record UserCorrection(bool Managed, SyncUserResult? Result)
{
    /// <summary>Gets the result for a user the sync doesn't write.</summary>
    public static UserCorrection NotManaged { get; } = new(false, null);

    /// <summary>
    /// Gets a value indicating whether another request is still creating the user (see
    /// <see cref="LibrarySync.BeginCreation"/>): nothing was written, and <see cref="Result"/> is what the rules would write.
    /// </summary>
    public bool BeingCreated { get; init; }

    /// <summary>
    /// Gets why the sync is paused when the store caused the pause (<c>StoreRestored</c>, <c>StoreReadOnly</c>,
    /// <c>StoreDegraded</c>), or <c>null</c>: nothing is written then, and a sign-in whose
    /// access is more than <see cref="LastWrite"/> is refused.
    /// </summary>
    public string? HeldBecause { get; init; }

    /// <summary>Gets what Access Roles last wrote for the user (the ledger's <c>LastWrite</c>) while the store holds the sync.</summary>
    public LibraryFields? LastWrite { get; init; }

    /// <summary>Gets a value indicating whether the user's native access changed (a write, or the stored row written back).</summary>
    public bool Wrote => Result?.Write?.Outcome is PolicyWriteOutcome.Written or PolicyWriteOutcome.Resynced;

    /// <summary>
    /// Gets the library fields the user may have: the rules' result, or under <c>ReportOnly</c> the safe-direction write;
    /// while the store holds the sync, the last write; <c>null</c> when nothing was to be written.
    /// </summary>
    public LibraryFields? Planned => HeldBecause is not null ? LastWrite : Result switch
    {
        { Kind: SyncActionKind.Drift, DriftDecision.Write: { } safe } => safe,
        { Kind: SyncActionKind.Drift } => null,
        { Reason: SyncReasons.NoLibraries or SyncReasons.NoFullAdministrator } => null,
        { Target: { } target } => target,
        _ => null
    };

    /// <summary>
    /// Gets a value indicating whether a planned write didn't happen for a reason that is an error or a hold, not a
    /// decision (fail closed for the sign-in): the user still being created by another request, the store holding the sync,
    /// the user's lock busy, the read or the write failed. A concurrent admin save (the native access changed meanwhile, the
    /// stored row written back, a write aborted after two changes) is the admin's decision, and a refusal (no libraries, the
    /// lockout guard) the rules'.
    /// </summary>
    public bool Failed => BeingCreated
        || HeldBecause is not null
        || Result is { Kind: SyncActionKind.Skip, Reason: SyncReasons.LockTimeout or SyncReasons.WriteFailed }
        || Result?.Write?.Outcome is PolicyWriteOutcome.Failed or PolicyWriteOutcome.LockTimeout;
}
