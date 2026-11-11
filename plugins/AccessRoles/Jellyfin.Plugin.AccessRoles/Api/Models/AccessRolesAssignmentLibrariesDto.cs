using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A user's stored library settings (<see cref="AccessRolesAssignmentDto"/>).
/// </summary>
public sealed class AccessRolesAssignmentLibrariesDto
{
    /// <summary>Gets or sets the mode (<c>Inherit</c>, <c>All</c>, <c>Only</c>), or <c>null</c> for one a newer build wrote.</summary>
    public AccessRolesUserLibraryMode? Mode { get; set; }

    /// <summary>Gets or sets the libraries added on top of the starting set (deleted ones included until they are purged).</summary>
    public IReadOnlyList<Guid> Add { get; set; } = [];

    /// <summary>Gets or sets the libraries removed for this user; removing beats every grant.</summary>
    public IReadOnlyList<Guid> Remove { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether the admin confirmed that this user sees no libraries.</summary>
    public bool ConfirmNoLibraries { get; set; }
}
