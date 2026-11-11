using System;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A library by id and name.
/// </summary>
public sealed class AccessRolesLibraryNameDto
{
    /// <summary>Gets or sets the library's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the library's name.</summary>
    public string Name { get; set; } = string.Empty;
}
