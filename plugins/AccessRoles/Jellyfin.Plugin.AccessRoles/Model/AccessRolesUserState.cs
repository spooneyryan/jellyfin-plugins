namespace Jellyfin.Plugin.AccessRoles.Model;

/// <summary>
/// A user's management state as the API shows it: the stored <see cref="AccessRolesAssignmentState"/>
/// of users with an assignment, and <see cref="NotManaged"/> for users without one.
/// </summary>
public enum AccessRolesUserState
{
    /// <summary>No roles and no overrides: Access Roles applies no lock and writes nothing for the user.</summary>
    NotManaged,

    /// <summary>Roles and overrides apply.</summary>
    Managed,

    /// <summary>The admin paused management; the roles are kept and nothing applies.</summary>
    Excluded,

    /// <summary>
    /// The user's policy has a legacy <c>BlockedMediaFolders</c> list, which the API can't write: locks apply, library
    /// access isn't managed.
    /// </summary>
    CannotManage,

    /// <summary>
    /// The assignment, or one of its roles, holds a value a newer build wrote: every settings section is locked and
    /// nothing is written to the user's policy until that version is installed.
    /// </summary>
    UnknownValue,
}
