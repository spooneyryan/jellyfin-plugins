using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// The role editor's visible <i>Dashboard</i> row: non-admins never open the Dashboard, so the row
/// is about the admin data and server calls they can still reach (the <c>AdminSurfaces</c> lock); no administrator is
/// restricted.
/// </summary>
public sealed class AccessRolesDashboardRowDto
{
    /// <summary>
    /// Gets or sets a value indicating whether the role has the <c>AdminSurfaces</c> lock: <b>Block Dashboard data and
    /// server calls</b>.
    /// </summary>
    public bool BlocksAdminSurfaces { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the server enforces <c>AdminSurfaces</c> in this build: <c>false</c> before
    /// 0.9.7, and <see cref="NonAdministrators"/> then says so for a role that has it.
    /// </summary>
    public bool Enforced { get; set; }

    /// <summary>Gets or sets what the row says for the role's non-admin members.</summary>
    public string NonAdministrators { get; set; } = string.Empty;

    /// <summary>Gets or sets what the row says for administrators: they are not restricted.</summary>
    public string Administrators { get; set; } = string.Empty;

    /// <summary>Gets or sets the names of the role's members who are administrators, whom no lock of the role restricts.</summary>
    public IReadOnlyList<string> AdministratorMembers { get; set; } = [];
}
