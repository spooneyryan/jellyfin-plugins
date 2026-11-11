using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A user's native library fields (<c>UserPolicy.EnableAllFolders</c> and <c>EnabledFolders</c>).
/// </summary>
public sealed class AccessRolesNativeLibrariesDto
{
    /// <summary>Gets or sets a value indicating whether every library is granted, future ones included.</summary>
    public bool EnableAllFolders { get; set; }

    /// <summary>Gets or sets the granted libraries otherwise.</summary>
    public IReadOnlyList<Guid> EnabledFolders { get; set; } = [];
}
