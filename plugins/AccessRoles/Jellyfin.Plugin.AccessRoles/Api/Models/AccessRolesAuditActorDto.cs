using System;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// Who did what an audit line records (<c>Actor</c>).
/// </summary>
public sealed class AccessRolesAuditActorDto
{
    /// <summary>Gets or sets <c>Admin</c>, <c>ApiKey</c> or <c>System</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the administrator's id, or <c>null</c>.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Gets or sets the administrator's name, or <c>null</c>.</summary>
    public string? Name { get; set; }
}
