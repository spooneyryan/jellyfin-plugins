using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A role's library grant (<c>Roles[].Libraries</c>).
/// </summary>
public sealed class AccessRolesRoleLibrariesDto
{
    /// <summary>Gets or sets the mode, or <c>null</c> for one a newer build wrote (in the role's <c>UnknownValues</c>).</summary>
    public AccessRolesRoleLibraryMode? Mode { get; set; }

    /// <summary>Gets or sets the libraries <c>Only</c> grants or <c>AllExcept</c> leaves out.</summary>
    public IReadOnlyList<AccessRolesLibraryRefDto> Refs { get; set; } = [];
}
