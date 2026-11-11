using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// What a library sync run did, or would do, for one user (<c>{UserId, Action, Diff}</c>).
/// </summary>
public sealed class AccessRolesSyncUserDto
{
    /// <summary>Gets or sets the user's id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the user's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the user is an administrator (library roles apply to them too).</summary>
    public bool IsAdministrator { get; set; }

    /// <summary>
    /// Gets or sets the action: <c>Write</c> (a managed user's result), <c>Release</c> (a pending <c>Restore</c> or
    /// <c>GrantAll</c>), <c>DropRelease</c>, <c>Drift</c> (reported, <c>DriftPolicy=ReportOnly</c>) or <c>Skip</c>.
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets why: for a write <c>FirstWrite</c>, <c>RulesChanged</c>, <c>Drift</c> or <c>NoLibrariesLeft</c>; for a
    /// release its choice; for a drop <c>BaselineChanged</c>, <c>NoBaseline</c> or <c>AlreadyApplied</c>; for a skip
    /// <c>NoLibraries</c>, <c>NoFullAdministrator</c>, <c>WriteAborted</c>, <c>LockTimeout</c>, <c>UserNotFound</c> or
    /// <c>WriteFailed</c>.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets the user's native library fields when the run read them.</summary>
    public AccessRolesNativeLibrariesDto Native { get; set; } = new();

    /// <summary>
    /// Gets or sets the library fields written, or to be written; <c>null</c> when nothing is. For a drifted user under
    /// <c>DriftPolicy=ReportOnly</c>, the access with the changes in the safe direction applied.
    /// </summary>
    public AccessRolesNativeLibrariesDto? Target { get; set; }

    /// <summary>
    /// Gets or sets the rules' result for a drifted user under <c>DriftPolicy=ReportOnly</c> (what <c>reapplyDrift=true</c>
    /// would write); <c>null</c> otherwise.
    /// </summary>
    public AccessRolesNativeLibrariesDto? Intended { get; set; }

    /// <summary>
    /// Gets or sets, for a drifted user under <c>DriftPolicy=ReportOnly</c>, the libraries the admin's edit added that stay
    /// (the ReportOnly choice).
    /// </summary>
    public IReadOnlyList<AccessRolesLibraryNameDto> Kept { get; set; } = [];

    /// <summary>
    /// Gets or sets, for a drifted user under <c>DriftPolicy=ReportOnly</c>, the libraries the rules give since Access Roles'
    /// last write, reported and not applied (adding grants on top of an admin's edit was considered and not chosen).
    /// </summary>
    public IReadOnlyList<AccessRolesLibraryNameDto> NotApplied { get; set; } = [];

    /// <summary>Gets or sets the libraries the write gives the user, by name.</summary>
    public IReadOnlyList<AccessRolesLibraryNameDto> Added { get; set; } = [];

    /// <summary>Gets or sets the libraries the write takes from the user, by name.</summary>
    public IReadOnlyList<AccessRolesLibraryNameDto> Removed { get; set; } = [];

    /// <summary>
    /// Gets or sets what the write did (<c>Written</c>, <c>Unchanged</c>, <c>Aborted</c>, <c>LockTimeout</c>,
    /// <c>Resynced</c>, <c>UserNotFound</c>, <c>Failed</c>); <c>null</c> in a dry run, a paused run, or without a write.
    /// </summary>
    public string? Outcome { get; set; }

    /// <summary>Gets or sets the policy fields an aborted or rebuilt write found changed while it was built.</summary>
    public IReadOnlyList<string> ChangedFields { get; set; } = [];

    /// <summary>
    /// Gets or sets the policy fields the write took from the user's committed row instead of the server's cache: after a
    /// failed save, the ones a concurrent save (an admin's) had changed; otherwise the ones the cache and the database
    /// disagreed on.
    /// </summary>
    public IReadOnlyList<string> CommittedFields { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether a save failed and the write was made again (from the committed row).</summary>
    public bool SaveFailed { get; set; }
}
