using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// Request body of <c>POST /AccessRoles/Admin/Users/ApplyPolicy</c>: the unknown user ids Diagnostics lists under
/// <c>Users.OrphanUsers</c>, whose assignments and ledger entries go (Access Roles' deleted-user policy is <c>Delete</c>,
/// always), after a snapshot.
/// </summary>
public sealed class AccessRolesApplyPolicyRequest
{
    /// <summary>Gets or sets the user ids: 1 to 1000, none of them a user of this server.</summary>
    public IReadOnlyList<Guid>? UserIds { get; set; }
}
