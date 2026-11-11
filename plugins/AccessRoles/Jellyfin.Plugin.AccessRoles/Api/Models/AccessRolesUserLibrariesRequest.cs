using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A user's own library settings in a request:
/// <c>effective = (starting set ∪ Add) − Remove</c>.
/// </summary>
public sealed class AccessRolesUserLibrariesRequest
{
    /// <summary>
    /// Gets or sets the mode: <c>Inherit</c> (the roles decide; without a library role, the user's original access),
    /// <c>All</c> (every library, future ones included) or <c>Only</c> (the <see cref="Add"/> list).
    /// </summary>
    public AccessRolesUserLibraryMode? Mode { get; set; }

    /// <summary>Gets or sets the libraries added for this user; each a library the server lists, or one already stored.</summary>
    public IReadOnlyList<Guid>? Add { get; set; }

    /// <summary>Gets or sets the libraries removed for this user (a removal beats every grant); none may also be in <see cref="Add"/>.</summary>
    public IReadOnlyList<Guid>? Remove { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the admin really wants this user to see no libraries: only with
    /// <c>Only</c> and nothing added (<c>confirmNoLibraries</c>).
    /// </summary>
    public bool ConfirmNoLibraries { get; set; }
}
