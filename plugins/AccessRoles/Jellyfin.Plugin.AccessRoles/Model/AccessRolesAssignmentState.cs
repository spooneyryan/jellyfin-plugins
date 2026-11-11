namespace Jellyfin.Plugin.AccessRoles.Model;

/// <summary>
/// Whether the plugin manages a user (<c>Assignments[].State</c>).
/// </summary>
public enum AccessRolesAssignmentState
{
    /// <summary>Roles and overrides apply (the default).</summary>
    Managed,

    /// <summary>The user has a legacy <c>BlockedMediaFolders</c> list, which the API can't write.</summary>
    CannotManage,

    /// <summary>The admin paused management for this user; setting it needs an <c>onUnmanage</c> choice.</summary>
    Excluded,
}
