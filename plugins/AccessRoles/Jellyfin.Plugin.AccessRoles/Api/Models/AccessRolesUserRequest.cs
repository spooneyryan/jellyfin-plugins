using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// Request body of <c>PUT /AccessRoles/Admin/Users/{userId}</c>, and one of <c>POST /AccessRoles/Admin/Preview</c>'s
/// proposed assignments: the user's whole assignment. An assignment with no role, no override, the
/// <c>Inherit</c> library mode with empty lists and the <c>Managed</c> state is removed: the user is then not managed.
/// </summary>
public sealed class AccessRolesUserRequest
{
    /// <summary>Gets or sets the user, in a preview only (a PUT takes it from the route).</summary>
    public Guid? UserId { get; set; }

    /// <summary>Gets or sets the user's roles, in the admin's order; each must exist (400 otherwise).</summary>
    public IReadOnlyList<Guid>? RoleIds { get; set; }

    /// <summary>
    /// Gets or sets the lock overrides by lock name (<c>Profile</c>, <c>QuickConnect</c>, …): <c>Lock</c> or <c>Unlock</c>
    /// win over every role; <c>Inherit</c> entries are dropped. A name that is no lock is a 400.
    /// </summary>
    public IReadOnlyDictionary<string, AccessRolesLockOverride>? LockOverrides { get; set; }

    /// <summary>Gets or sets the user's own library mode and lists; left out, <c>Inherit</c> with none.</summary>
    public AccessRolesUserLibrariesRequest? Libraries { get; set; }

    /// <summary>
    /// Gets or sets the state: <c>Managed</c> (the default) or <c>Excluded</c> (management paused; it needs an
    /// <c>onUnmanage</c> choice when it takes the user out of library management). <c>CannotManage</c> is the server's to
    /// set (400).
    /// </summary>
    public AccessRolesAssignmentState? State { get; set; }

    /// <summary>
    /// Gets or sets the assignment's <c>UpdatedUtc</c> as last read, or none for a user who had no assignment: anything else
    /// is a 409 and nothing changes.
    /// </summary>
    public DateTime? IfUpdatedAt { get; set; }
}
