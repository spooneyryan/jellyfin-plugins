using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// Request body of <c>PUT /AccessRoles/Admin/DefaultRoles</c>: the roles a new user gets. The roles API
/// stores them; giving them to users Jellyfin creates is the library sync's.
/// </summary>
public sealed class AccessRolesDefaultRolesRequest
{
    /// <summary>Gets or sets the roles, each of which must exist (404 otherwise); none clears the list.</summary>
    public IReadOnlyList<Guid>? RoleIds { get; set; }
}
