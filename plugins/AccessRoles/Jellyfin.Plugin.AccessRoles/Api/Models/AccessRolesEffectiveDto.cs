using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A user's effective access with its sources:
/// "Locked by role Kid", "Unlocked for this user", "Movies: granted by role Family", "Horror: removed for this user".
/// </summary>
public sealed class AccessRolesEffectiveDto
{
    /// <summary>Gets or sets the user's id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the user's name.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the user is an administrator: no lock applies; library roles do.</summary>
    public bool IsAdministrator { get; set; }

    /// <summary>Gets or sets the user's state.</summary>
    public AccessRolesUserState State { get; set; }

    /// <summary>Gets or sets the user's roles that exist, in the admin's order.</summary>
    public IReadOnlyList<AccessRolesRoleRefDto> Roles { get; set; } = [];

    /// <summary>
    /// Gets or sets every lock by name, with its state and source: <c>Role:&lt;name&gt;</c>, <c>UserOverride</c>,
    /// <c>Default</c>, <c>Administrator</c>, <c>NotManaged</c>, <c>Excluded</c> or <c>UnknownValue</c>.
    /// </summary>
    public IReadOnlyDictionary<string, AccessRolesLockStateDto> Locks { get; set; } = new Dictionary<string, AccessRolesLockStateDto>();

    /// <summary>Gets or sets the user's library access.</summary>
    public AccessRolesEffectiveLibrariesDto Libraries { get; set; } = new();

    /// <summary>Gets or sets the user's native flags the admin should know about.</summary>
    public AccessRolesNativeFlagsDto NativeFlags { get; set; } = new();

    /// <summary>Gets or sets the warnings, in plain words.</summary>
    public IReadOnlyList<string> Warnings { get; set; } = [];

    /// <summary>Gets or sets the values a newer build wrote, as <c>Field=Name</c>.</summary>
    public IReadOnlyList<string> UnknownValues { get; set; } = [];

    /// <summary>
    /// Gets or sets the assignment's <c>UpdatedUtc</c> (the <c>IfUpdatedAt</c> of a <c>PUT Users/{id}</c>), or <c>null</c>
    /// without one. A dry run answers the stored assignment's, never a time that wasn't saved.
    /// </summary>
    public DateTime? UpdatedUtc { get; set; }

    /// <summary>
    /// Gets or sets the user's assignment as stored (or, for a dry run or <c>simulateRoleIds</c>, as it would be), or
    /// <c>null</c> without one: what the admin page's per-user panel edits and sends back.
    /// </summary>
    public AccessRolesAssignmentDto? Assignment { get; set; }
}
