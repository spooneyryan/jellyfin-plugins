using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// What the library sync will write to a managed user's native library fields (<c>PendingWrite</c>).
/// </summary>
public sealed class AccessRolesPendingWriteDto
{
    /// <summary>Gets or sets a value indicating whether it will write <c>EnableAllFolders</c> on.</summary>
    public bool EnableAllFolders { get; set; }

    /// <summary>Gets or sets the libraries it will add to <c>EnabledFolders</c>.</summary>
    public IReadOnlyList<Guid> Add { get; set; } = [];

    /// <summary>Gets or sets the libraries it will remove from <c>EnabledFolders</c>.</summary>
    public IReadOnlyList<Guid> Remove { get; set; } = [];
}
