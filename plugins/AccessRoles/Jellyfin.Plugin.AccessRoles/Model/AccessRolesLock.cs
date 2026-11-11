namespace Jellyfin.Plugin.AccessRoles.Model;

/// <summary>
/// What a role can lock (<c>Locks</c>): the sections of each user's own Settings menu, the admin
/// data and server calls non-admins can still reach, and the list of other users.
/// </summary>
/// <remarks>
/// Stored by name. A name a newer build wrote and this one doesn't know is kept in the file and ignored. The
/// enum carries the DTO prefix (the kit's naming rule) because the roles API shows these names.
/// </remarks>
public enum AccessRolesLock
{
    /// <summary>The Profile page: password, user image and the user's name.</summary>
    Profile,

    /// <summary>The account's display settings stored on the server (missing episodes, episode images, dashboard theme).</summary>
    DisplayAccount,

    /// <summary>The Display page as a whole: this device's display settings, which can only be hidden.</summary>
    DisplayDevice,

    /// <summary>The Home screen page: sections, order, latest and My Media exclusions.</summary>
    Home,

    /// <summary>The Playback page: audio language, autoplay, remembered selections, skip lengths.</summary>
    Playback,

    /// <summary>The Subtitles page: subtitle language and mode.</summary>
    Subtitles,

    /// <summary>The Controls page, kept in the browser only, so hidden only.</summary>
    Controls,

    /// <summary>Quick Connect: authorising another device to sign in as this user.</summary>
    QuickConnect,

    /// <summary>Jellyfin Enhanced's settings panel, when that plugin is installed.</summary>
    EnhancedPanel,

    /// <summary>The admin data and server calls non-admins can still reach ("Block Dashboard data and server calls").</summary>
    AdminSurfaces,

    /// <summary>The list of other users: <c>GET /Users</c> shows only the caller.</summary>
    UserDirectory,
}
