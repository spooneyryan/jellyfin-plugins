using System;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A role by id and name.
/// </summary>
public sealed class AccessRolesRoleRefDto
{
    /// <summary>Gets or sets the role's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the role's name.</summary>
    public string Name { get; set; } = string.Empty;
}
