namespace Jellyfin.Plugin.AccessRoles.Model;

/// <summary>
/// A user's own library mode (<c>Assignments[].Libraries.Mode</c>), with the
/// assignment's <c>Add</c> and <c>Remove</c> lists applied on top.
/// </summary>
public enum AccessRolesUserLibraryMode
{
    /// <summary>The roles decide; without a library role, the user's original access from the ledger (the default).</summary>
    Inherit,

    /// <summary>Every library, future ones included.</summary>
    All,

    /// <summary>Only the assignment's <c>Add</c> list.</summary>
    Only,
}
