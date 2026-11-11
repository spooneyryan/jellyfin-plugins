using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A warning of the import: a user it leaves out and why, or something the admin should know about one it
/// imports.
/// </summary>
public sealed class AccessRolesImportWarningDto
{
    /// <summary>
    /// Gets or sets the code: <c>Administrator</c> (not imported: no lock applies to administrators, and library roles
    /// aren't proposed for them), <c>CannotManage</c> (not imported: a legacy <c>BlockedMediaFolders</c> list),
    /// <c>NoLibraries</c> (not imported: no library to express as a role), <c>AccessWouldChange</c> (not imported: no role
    /// gives exactly the user's access, a library created again for instance), <c>DeletedLibraries</c> (imported: the sync
    /// will drop ids of libraries that no longer exist from the user's access, which changes nothing they can open),
    /// <c>ShownOnLoginScreen</c> (imported: the user is listed on the login screen); and, one warning for several
    /// users, <c>EveryLibrary</c> (not imported: users who see every library, left unmanaged unless
    /// <c>manageUsersWithEveryLibrary=true</c>), <c>EveryLibraryManaged</c> (imported, opted in: the <c>All libraries</c>
    /// role) and <c>LiveTvManagement</c> (imported: the users can change Live TV settings and schedule and delete
    /// recordings; on for every new user before 10.11.7).
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets the user's id, or <c>null</c> for a warning about several users (<see cref="Users"/>).</summary>
    public Guid? UserId { get; set; }

    /// <summary>Gets or sets the user's name, or <c>null</c> for a warning about several users.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the users the warning is about (the one user, or all of them), by name.</summary>
    public IReadOnlyList<AccessRolesUserRefDto> Users { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether the import assigns the users.</summary>
    public bool Imported { get; set; }

    /// <summary>Gets or sets the warning in plain words.</summary>
    public string Message { get; set; } = string.Empty;
}
