using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// Request body of <c>POST /AccessRoles/Admin/Preview</c>: a proposed state, compared with the stored
/// one; what it doesn't mention stays as stored. Nothing is saved.
/// </summary>
public sealed class AccessRolesPreviewRequest
{
    /// <summary>Gets or sets proposed roles: each replaces the role of its <c>Id</c>, or is a new role without one.</summary>
    public IReadOnlyList<AccessRolesRoleRequest>? Roles { get; set; }

    /// <summary>Gets or sets roles proposed for deletion; their members lose them.</summary>
    public IReadOnlyList<Guid>? DeleteRoleIds { get; set; }

    /// <summary>Gets or sets proposed assignments: each replaces the assignment of its <c>UserId</c>.</summary>
    public IReadOnlyList<AccessRolesUserRequest>? Assignments { get; set; }

    /// <summary>Gets or sets the proposed default roles, or none to keep the stored ones.</summary>
    public IReadOnlyList<Guid>? DefaultRoleIds { get; set; }

    /// <summary>Gets or sets the <c>onUnmanage</c> choice to preview for users the proposal takes out of library management.</summary>
    public AccessRolesUnmanageChoice? OnUnmanage { get; set; }
}
