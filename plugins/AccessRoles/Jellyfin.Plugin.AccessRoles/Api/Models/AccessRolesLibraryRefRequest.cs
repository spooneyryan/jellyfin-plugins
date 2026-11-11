using System;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A library a role refers to, in a request: its id (other fields of <see cref="AccessRolesLibraryRefDto"/> sent back are
/// ignored; the server fills them).
/// </summary>
public sealed class AccessRolesLibraryRefRequest
{
    /// <summary>Gets or sets the library's id (<c>GET /AccessRoles/Admin/Libraries</c> lists them).</summary>
    public Guid Id { get; set; }
}
