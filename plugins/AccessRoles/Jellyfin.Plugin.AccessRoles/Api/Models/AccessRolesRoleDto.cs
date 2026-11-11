using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A role, with its member count and the role editor's
/// <i>Dashboard</i> row.
/// </summary>
public sealed class AccessRolesRoleDto
{
    /// <summary>Gets or sets the role's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the role's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the admin's description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the locks this build knows, in the enum's order.</summary>
    public IReadOnlyList<AccessRolesLock> Locks { get; set; } = [];

    /// <summary>Gets or sets the library grant.</summary>
    public AccessRolesRoleLibrariesDto Libraries { get; set; } = new();

    /// <summary>Gets or sets the role editor's <i>Dashboard</i> row.</summary>
    public AccessRolesDashboardRowDto Dashboard { get; set; } = new();

    /// <summary>Gets or sets how many users have the role (excluded users included).</summary>
    public int MemberCount { get; set; }

    /// <summary>Gets or sets a value indicating whether new users get the role (<c>DefaultRoleIds</c>).</summary>
    public bool IsDefault { get; set; }

    /// <summary>Gets or sets the values a newer build wrote that this one keeps but doesn't know, as <c>Field=Name</c>.</summary>
    public IReadOnlyList<string> UnknownValues { get; set; } = [];

    /// <summary>Gets or sets when the role was created, UTC.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// Gets or sets when the role was last changed, UTC: the <c>IfUpdatedAt</c> of a PUT. A dry run answers the stored
    /// role's, and <c>null</c> for a role it would create, never a time that wasn't saved.
    /// </summary>
    public DateTime? UpdatedUtc { get; set; }

    /// <summary>
    /// Gets or sets the role's members and their assignments as a token: <c>ifMembers</c> of a
    /// <c>DELETE</c> (and <c>IfMembers</c> of a <c>PUT</c>) refuses with 409 <c>Changed</c> when they changed since.
    /// </summary>
    public string MembersToken { get; set; } = string.Empty;
}
