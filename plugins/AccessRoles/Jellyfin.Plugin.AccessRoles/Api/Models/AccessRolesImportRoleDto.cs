using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A role the import proposes: one distinct library access, named after its libraries ("Movies + Kids"),
/// with no locks.
/// </summary>
public sealed class AccessRolesImportRoleDto
{
    /// <summary>Gets or sets the role's id once saved; <c>null</c> in a dry run.</summary>
    public Guid? Id { get; set; }

    /// <summary>Gets or sets the role's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the library mode: <c>All</c> (for users with every library, future ones too) or <c>Only</c>.</summary>
    public AccessRolesRoleLibraryMode Mode { get; set; }

    /// <summary>Gets or sets the libraries an <c>Only</c> role grants, by name.</summary>
    public IReadOnlyList<AccessRolesLibraryNameDto> Libraries { get; set; } = [];

    /// <summary>Gets or sets the users who get it, by name.</summary>
    public IReadOnlyList<AccessRolesUserRefDto> Members { get; set; } = [];
}
