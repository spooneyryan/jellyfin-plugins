using System;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A library change the library lifecycle found in a sync run.
/// </summary>
public sealed class AccessRolesSyncLibraryDto
{
    /// <summary>Gets or sets the change: <c>Added</c>, <c>Renamed</c>, <c>Removed</c> or <c>Recreated</c>.</summary>
    public string Change { get; set; } = string.Empty;

    /// <summary>Gets or sets the library's id (the new id for a rename).</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the library's name (the new name for a rename).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a rename's id before it.</summary>
    public Guid? OldId { get; set; }

    /// <summary>Gets or sets a rename's name before it.</summary>
    public string? OldName { get; set; }

    /// <summary>Gets or sets a value indicating whether the library structure filter saw the call, rather than the backstop's diff.</summary>
    public bool SeenCall { get; set; }
}
