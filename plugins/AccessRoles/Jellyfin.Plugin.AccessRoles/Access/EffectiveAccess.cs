using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// A user's effective rules with their sources (served as <c>AccessRolesEffectiveDto</c>).
/// </summary>
internal sealed record EffectiveAccess
{
    /// <summary>Gets the user.</summary>
    public required ServerUser User { get; init; }

    /// <summary>Gets the user's management state.</summary>
    public AccessRolesUserState State { get; init; }

    /// <summary>Gets the user's assignment, or <c>null</c>.</summary>
    public RoleAssignment? Assignment { get; init; }

    /// <summary>Gets the user's roles that exist, in the assignment's order.</summary>
    public IReadOnlyList<AccessRole> Roles { get; init; } = [];

    /// <summary>Gets the assignment's role ids that no role has.</summary>
    public IReadOnlyList<Guid> MissingRoleIds { get; init; } = [];

    /// <summary>Gets every lock as it applies, in the enum's order.</summary>
    public required IReadOnlyDictionary<AccessRolesLock, LockDecision> Locks { get; init; }

    /// <summary>Gets the library access.</summary>
    public required LibraryDecision Libraries { get; init; }

    /// <summary>
    /// Gets the user's pending release from the ledger (a <c>Restore</c> or <c>GrantAll</c> the library sync is still
    /// to write) while their libraries aren't managed, or <c>null</c>.
    /// </summary>
    public LedgerRelease? PendingRelease { get; init; }

    /// <summary>
    /// Gets a value indicating whether the user's native access still is the pending release's baseline, so the sync would
    /// write it (<see cref="EffectiveRulesResolver.BaselineMatches"/>).
    /// </summary>
    public bool PendingReleaseBaselineMatches { get; init; }

    /// <summary>Gets the plugin's last write of the user's library fields (the ledger's <c>LastWrite</c>), or <c>null</c>.</summary>
    public LedgerWrite? LastWrite { get; init; }

    /// <summary>
    /// Gets a value indicating whether the user's native library access drifted: the libraries are
    /// written by Access Roles, and the native access is neither the result nor <see cref="LastWrite"/>. With
    /// <c>DriftPolicy=Reapply</c> the library sync writes the result again; with <c>ReportOnly</c> it only reports.
    /// </summary>
    public bool Drift { get; init; }

    /// <summary>Gets the warnings for the admin, in plain words.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Gets the values a newer build wrote, as <c>Field=Name</c>.</summary>
    public IReadOnlyList<string> UnknownValues { get; init; } = [];

    /// <summary>Gets the locked sections and surfaces.</summary>
    public IReadOnlySet<AccessRolesLock> Locked => Locks.Where(l => l.Value.Locked).Select(l => l.Key).ToHashSet();
}
