namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// One lock as it applies to a user (<c>Locks</c>).
/// </summary>
public sealed class AccessRolesLockStateDto
{
    /// <summary>Gets or sets a value indicating whether the section or surface is locked.</summary>
    public bool Locked { get; set; }

    /// <summary>Gets or sets why: <c>Role:Kid</c>, <c>UserOverride</c>, <c>Default</c>, <c>Administrator</c> and the like.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the server enforces this lock in this build: <c>false</c> for
    /// <c>AdminSurfaces</c> and <c>UserDirectory</c> before 0.9.7 (the admin page says "not enforced
    /// yet"), and always for the hidden-only <c>DisplayDevice</c> and <c>Controls</c>, whose settings live in the browser:
    /// the web hides them, and the server has nothing to enforce.
    /// </summary>
    public bool Enforced { get; set; } = true;
}
