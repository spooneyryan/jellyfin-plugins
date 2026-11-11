using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A role's library grant in a request.
/// </summary>
public sealed class AccessRolesRoleLibrariesRequest
{
    /// <summary>
    /// Gets or sets the mode: <c>NotManaged</c>, <c>All</c> (future libraries included), <c>AllExcept</c> (every current and
    /// future library but the listed ones) or <c>Only</c> (the listed ones, at least one).
    /// </summary>
    public AccessRolesRoleLibraryMode? Mode { get; set; }

    /// <summary>
    /// Gets or sets the libraries <c>Only</c> grants or <c>AllExcept</c> leaves out: each a library the server lists, or one
    /// the stored role already refers to (a deleted library's reference is kept until it is purged). <c>NotManaged</c> and
    /// <c>All</c> take none.
    /// </summary>
    public IReadOnlyList<AccessRolesLibraryRefRequest>? Refs { get; set; }
}
