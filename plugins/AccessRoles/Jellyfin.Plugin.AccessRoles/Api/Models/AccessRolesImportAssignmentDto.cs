using System;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// One user the import assigns, with their role.
/// </summary>
public sealed class AccessRolesImportAssignmentDto
{
    /// <summary>Gets or sets the user's id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the user's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the role's id once saved; <c>null</c> in a dry run.</summary>
    public Guid? RoleId { get; set; }

    /// <summary>Gets or sets the role's name.</summary>
    public string RoleName { get; set; } = string.Empty;
}
