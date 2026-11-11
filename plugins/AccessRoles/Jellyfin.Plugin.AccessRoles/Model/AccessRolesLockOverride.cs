namespace Jellyfin.Plugin.AccessRoles.Model;

/// <summary>
/// A user's override of one lock (<c>LockOverrides</c>): per-user overrides win over the roles' locks.
/// </summary>
public enum AccessRolesLockOverride
{
    /// <summary>The roles decide (the default).</summary>
    Inherit,

    /// <summary>Locked for this user whatever the roles say.</summary>
    Lock,

    /// <summary>Unlocked for this user whatever the roles say.</summary>
    Unlock,
}
