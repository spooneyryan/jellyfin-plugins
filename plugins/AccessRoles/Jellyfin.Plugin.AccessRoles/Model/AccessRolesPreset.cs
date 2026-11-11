namespace Jellyfin.Plugin.AccessRoles.Model;

/// <summary>
/// The starting points of a new role: each fills the locks and the library mode
/// the request leaves out.
/// </summary>
public enum AccessRolesPreset
{
    /// <summary>
    /// Profile, Display (account), Home, Controls, Quick Connect, Jellyfin Enhanced's panel, the Dashboard's data and the
    /// list of other users locked; <c>Only…</c> libraries, at least one picked.
    /// </summary>
    Kid,

    /// <summary>All nine settings sections, the Dashboard's data and the list of other users locked; <c>Only…</c> libraries.</summary>
    Guest,

    /// <summary>No lock; libraries not managed.</summary>
    Standard,
}
