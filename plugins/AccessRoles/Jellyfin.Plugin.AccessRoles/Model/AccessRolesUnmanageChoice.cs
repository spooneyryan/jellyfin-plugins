namespace Jellyfin.Plugin.AccessRoles.Model;

/// <summary>
/// What a user's library access becomes when a change takes them out of library management:
/// the <c>onUnmanage</c> choice every such change needs.
/// </summary>
public enum AccessRolesUnmanageChoice
{
    /// <summary>The user's original access, from the ledger (what it was when Access Roles started managing them).</summary>
    Restore,

    /// <summary>The access as it is now stays, frozen ("library access stays frozen at: Kids").</summary>
    Keep,

    /// <summary>Every library, future ones included.</summary>
    GrantAll,
}
