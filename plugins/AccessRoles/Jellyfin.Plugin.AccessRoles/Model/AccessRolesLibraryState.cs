namespace Jellyfin.Plugin.AccessRoles.Model;

/// <summary>
/// A known library's state (<c>KnownLibraries[].State</c>).
/// </summary>
public enum AccessRolesLibraryState
{
    /// <summary>The library exists (the default).</summary>
    Active,

    /// <summary>Deleted; its references in roles, overrides and the ledger are kept until the admin purges them.</summary>
    Orphaned,
}
