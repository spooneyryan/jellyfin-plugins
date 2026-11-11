using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A row of the Users tab (<c>GET /AccessRoles/Admin/Users</c>): every user of the server, managed or
/// not.
/// </summary>
public sealed class AccessRolesUserDto
{
    /// <summary>Gets or sets the user's id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the user's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the user is an administrator (no lock applies to them).</summary>
    public bool IsAdministrator { get; set; }

    /// <summary>Gets or sets a value indicating whether the account is disabled.</summary>
    public bool IsDisabled { get; set; }

    /// <summary>Gets or sets a value indicating whether the user is hidden from the login screen.</summary>
    public bool IsHidden { get; set; }

    /// <summary>Gets or sets the user's roles, in the admin's order.</summary>
    public IReadOnlyList<Guid> RoleIds { get; set; } = [];

    /// <summary>Gets or sets the user's state.</summary>
    public AccessRolesUserState State { get; set; }

    /// <summary>Gets or sets a value indicating whether Access Roles manages the user's library access.</summary>
    public bool LibrariesManaged { get; set; }

    /// <summary>Gets or sets how many sections and surfaces are locked for the user.</summary>
    public int LockedCount { get; set; }

    /// <summary>Gets or sets a value indicating whether the user's result is no library at all.</summary>
    public bool NoLibraries { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether that result was chosen (<c>Only</c> with nothing, confirmed): the library
    /// sync then writes it and manages it like any other; one nobody chose it leaves alone, and it is never
    /// drift.
    /// </summary>
    public bool NoLibrariesConfirmed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user's native library access differs from the result, so the library
    /// sync will write it.
    /// </summary>
    public bool PendingWrite { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the native access was changed outside Access Roles since its last write
    /// (always false until the library sync writes).
    /// </summary>
    public bool Drift { get; set; }

    /// <summary>Gets or sets a one-line summary: the roles, the lock count and the libraries.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Gets or sets the assignment's <c>UpdatedUtc</c> (the <c>IfUpdatedAt</c> of a PUT), or <c>null</c> without one.</summary>
    public DateTime? UpdatedUtc { get; set; }
}
