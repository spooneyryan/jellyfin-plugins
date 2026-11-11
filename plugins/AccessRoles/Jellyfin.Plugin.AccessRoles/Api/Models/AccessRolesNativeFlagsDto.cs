using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A user's native policy flags the effective view reports (<c>NativeFlags</c>); Access Roles doesn't change them.
/// </summary>
public sealed class AccessRolesNativeFlagsDto
{
    /// <summary>Gets or sets a value indicating whether the native preference switch is on.</summary>
    public bool EnableUserPreferenceAccess { get; set; }

    /// <summary>Gets or sets a value indicating whether the user may manage Live TV recordings (and its settings).</summary>
    public bool EnableLiveTvManagement { get; set; }

    /// <summary>Gets or sets a value indicating whether the user is hidden from the login screen.</summary>
    public bool IsHidden { get; set; }

    /// <summary>Gets or sets a value indicating whether the account is disabled.</summary>
    public bool IsDisabled { get; set; }

    /// <summary>Gets or sets the legacy block list; non-empty means Access Roles can't manage the user's libraries.</summary>
    public IReadOnlyList<Guid> BlockedMediaFolders { get; set; } = [];
}
