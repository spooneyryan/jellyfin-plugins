namespace Jellyfin.Plugin.AccessRoles.Model;

/// <summary>
/// How a role grants libraries (<c>Roles[].Libraries.Mode</c>).
/// </summary>
public enum AccessRolesRoleLibraryMode
{
    /// <summary>The role has no effect on libraries (the default).</summary>
    NotManaged,

    /// <summary>Every library, future ones included (native <c>EnableAllFolders=true</c>).</summary>
    All,

    /// <summary>Every current and future library except the listed ones, expanded at every sync.</summary>
    AllExcept,

    /// <summary>Only the listed libraries, at least one; future libraries not included.</summary>
    Only,
}
