using System;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A user by id and name.
/// </summary>
public sealed class AccessRolesUserRefDto
{
    /// <summary>Gets or sets the user's id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the user's name.</summary>
    public string Name { get; set; } = string.Empty;
}
