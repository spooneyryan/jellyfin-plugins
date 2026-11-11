using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A user's stored assignment as the admin page's per-user panel edits it: what a
/// <c>PUT /AccessRoles/Admin/Users/{userId}</c> replaces, so the panel sends back what it doesn't change. The effective
/// view's sources can't stand in for it: an administrator's or an excluded user's overrides and lists are stored but
/// don't apply. Values a newer build wrote are left out (<c>null</c> or missing) and listed in the effective view's
/// <c>UnknownValues</c>; a <c>PUT</c> that leaves them out keeps them.
/// </summary>
public sealed class AccessRolesAssignmentDto
{
    /// <summary>Gets or sets the role ids, in the assignment's order (roles that no longer exist included).</summary>
    public IReadOnlyList<Guid> RoleIds { get; set; } = [];

    /// <summary>Gets or sets the lock overrides, by lock name: <c>Lock</c> or <c>Unlock</c> (<c>Inherit</c> isn't stored).</summary>
    public IReadOnlyDictionary<string, AccessRolesLockOverride> LockOverrides { get; set; } = new Dictionary<string, AccessRolesLockOverride>();

    /// <summary>Gets or sets the user's own library settings.</summary>
    public AccessRolesAssignmentLibrariesDto Libraries { get; set; } = new();

    /// <summary>Gets or sets the stored state (<c>Managed</c>, <c>Excluded</c>, <c>CannotManage</c>), or <c>null</c> for one a newer build wrote.</summary>
    public AccessRolesAssignmentState? State { get; set; }
}
