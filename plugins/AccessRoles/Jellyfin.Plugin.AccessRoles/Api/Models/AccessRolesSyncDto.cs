using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// The answer of <c>POST /AccessRoles/Admin/Sync</c>: what one library sync run found and did, or with
/// <c>dryRun=true</c> would do, per user it does something for, and what the library lifecycle found.
/// </summary>
public sealed class AccessRolesSyncDto
{
    /// <summary>Gets or sets a value indicating whether this was a dry run: nothing was written or saved.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Gets or sets why the sync writes nothing now (<c>SafeMode</c>, <c>EnforcementDisabled</c>, <c>LibrarySyncDisabled</c>,
    /// <c>StoreReadOnly</c>, <c>StoreDegraded</c>), or <c>null</c> when it writes.
    /// </summary>
    public string? Paused { get; set; }

    /// <summary>Gets or sets what started the run (<c>Manual</c> here).</summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>Gets or sets when the run started, UTC.</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>Gets or sets how long it took, in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Gets or sets the drift policy in effect (<c>Reapply</c> or <c>ReportOnly</c>).</summary>
    public string DriftPolicy { get; set; } = string.Empty;

    /// <summary>Gets or sets how many users' libraries the rules manage.</summary>
    public int ManagedUsers { get; set; }

    /// <summary>Gets or sets how many users' access was written.</summary>
    public int Written { get; set; }

    /// <summary>Gets or sets how many users were skipped.</summary>
    public int Skipped { get; set; }

    /// <summary>Gets or sets the users the run did something for (every user not already in sync), by name.</summary>
    public IReadOnlyList<AccessRolesSyncUserDto> Users { get; set; } = [];

    /// <summary>
    /// Gets or sets what the run writes (a dry run: would write), the users and their library access, as a token: a real
    /// run given it as <c>ifPlan</c> writes only that.
    /// </summary>
    public string PlanToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the one user the run was for (<c>userId</c>; the per-user <i>Revert</i>), or <c>null</c> for everyone.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Gets or sets what the library lifecycle found (libraries added, renamed, removed, re-created).</summary>
    public IReadOnlyList<AccessRolesSyncLibraryDto> Libraries { get; set; } = [];

    /// <summary>Gets or sets the libraries no longer listed that wait for a rename to resolve.</summary>
    public IReadOnlyList<Guid> WaitingLibraries { get; set; } = [];

    /// <summary>
    /// Gets or sets the listed libraries a waiting rename waits for (its new name's): no rule grants them until the rename is
    /// re-pointed.
    /// </summary>
    public IReadOnlyList<Guid> WithheldLibraries { get; set; } = [];

    /// <summary>
    /// Gets or sets the users the run left alone because the server is creating them, or has just created them: nothing
    /// writes a user before the request creating them is done.
    /// </summary>
    public IReadOnlyList<Guid> UsersBeingCreated { get; set; } = [];
}
