using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// The presets of a new role and the lock groups the rules refer to.
/// </summary>
internal static class AccessPresets
{
    /// <summary>
    /// Gets the nine sections of each user's own Settings menu: what <c>Guest</c> locks, and what a user with a
    /// value a newer build wrote gets locked.
    /// </summary>
    public static IReadOnlyList<AccessRolesLock> SettingsSections { get; } =
    [
        AccessRolesLock.Profile, AccessRolesLock.DisplayAccount, AccessRolesLock.DisplayDevice, AccessRolesLock.Home, AccessRolesLock.Playback,
        AccessRolesLock.Subtitles, AccessRolesLock.Controls, AccessRolesLock.QuickConnect, AccessRolesLock.EnhancedPanel
    ];

    /// <summary>Gets every lock, in the enum's order (the order the API lists them in).</summary>
    public static IReadOnlyList<AccessRolesLock> AllLocks { get; } = Enum.GetValues<AccessRolesLock>();

    /// <summary>
    /// Gets a preset's locks.
    /// </summary>
    /// <param name="preset">The preset.</param>
    /// <returns>The locks.</returns>
    public static IReadOnlyList<AccessRolesLock> Locks(AccessRolesPreset preset) => preset switch
    {
        AccessRolesPreset.Kid =>
        [
            AccessRolesLock.Profile, AccessRolesLock.DisplayAccount, AccessRolesLock.Home, AccessRolesLock.Controls, AccessRolesLock.QuickConnect,
            AccessRolesLock.EnhancedPanel, AccessRolesLock.AdminSurfaces, AccessRolesLock.UserDirectory
        ],
        AccessRolesPreset.Guest => [.. SettingsSections, AccessRolesLock.AdminSurfaces, AccessRolesLock.UserDirectory],
        AccessRolesPreset.Standard => [],
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown preset.")
    };

    /// <summary>
    /// Gets a preset's library mode: <c>Only…</c> for Kid and Guest (the admin picks at least one library), not managed
    /// for Standard.
    /// </summary>
    /// <param name="preset">The preset.</param>
    /// <returns>The mode.</returns>
    public static AccessRolesRoleLibraryMode LibraryMode(AccessRolesPreset preset) => preset switch
    {
        AccessRolesPreset.Kid or AccessRolesPreset.Guest => AccessRolesRoleLibraryMode.Only,
        AccessRolesPreset.Standard => AccessRolesRoleLibraryMode.NotManaged,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown preset.")
    };
}
