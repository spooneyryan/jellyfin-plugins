using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// One line of the audit log (<c>GET /AccessRoles/Admin/Audit</c>).
/// </summary>
public sealed class AccessRolesAuditEntryDto
{
    /// <summary>Gets or sets the line format's version.</summary>
    public int V { get; set; }

    /// <summary>Gets or sets the line's number, one more than the line before.</summary>
    public long Seq { get; set; }

    /// <summary>Gets or sets when it happened, UTC.</summary>
    public DateTime Utc { get; set; }

    /// <summary>Gets or sets who did it.</summary>
    public AccessRolesAuditActorDto Actor { get; set; } = new();

    /// <summary>Gets or sets what happened: <c>RoleCreated</c>, <c>AssignmentChanged</c>, <c>LibraryAccessFrozen</c> and the like.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Gets or sets the user it concerns, or <c>null</c>.</summary>
    public Guid? TargetUserId { get; set; }

    /// <summary>Gets or sets the role it concerns, or <c>null</c>.</summary>
    public Guid? TargetRoleId { get; set; }

    /// <summary>Gets or sets the user's or role's name, or <c>null</c>.</summary>
    public string? TargetName { get; set; }

    /// <summary>Gets or sets the details, an object whose fields depend on the action, or <c>null</c>.</summary>
    public IReadOnlyDictionary<string, object?>? Details { get; set; }
}
