namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// The provenance strings of the effective view (<c>AccessRolesEffectiveDto</c>): why a lock is on or
/// off, why a library is visible or hidden, and where a user's starting set of libraries came from.
/// </summary>
internal static class AccessSources
{
    /// <summary>The role prefix: <c>Role:Kid</c>.</summary>
    public const string RolePrefix = "Role:";

    /// <summary>An administrator: no lock applies (administrators are never restricted).</summary>
    public const string Administrator = "Administrator";

    /// <summary>A user without an assignment: Access Roles applies nothing.</summary>
    public const string NotManaged = "NotManaged";

    /// <summary>An excluded user: Access Roles applies nothing.</summary>
    public const string Excluded = "Excluded";

    /// <summary>A value a newer build wrote: the settings sections are locked and nothing is written.</summary>
    public const string UnknownValue = "UnknownValue";

    /// <summary>The user's own override (<c>Lock</c> or <c>Unlock</c>), which wins over every role.</summary>
    public const string UserOverride = "UserOverride";

    /// <summary>No role locks it and no override.</summary>
    public const string Default = "Default";

    /// <summary>A library visible because the user's own mode is <c>All</c>.</summary>
    public const string AllLibraries = "AllLibraries";

    /// <summary>A library in the user's own <c>Add</c> list.</summary>
    public const string UserAdd = "UserAdd";

    /// <summary>A library of the user's original access, from the ledger (the starting set without a library role).</summary>
    public const string Original = "Original";

    /// <summary>A library the user's native policy grants, for users whose libraries Access Roles doesn't manage.</summary>
    public const string Native = "Native";

    /// <summary>A library hidden by the user's own <c>Remove</c> list, which beats every grant.</summary>
    public const string RemovedForUser = "RemovedForUser";

    /// <summary>A library no role and no override grants.</summary>
    public const string NotGranted = "NotGranted";

    /// <summary>
    /// A library of the original access that was deleted and created again (a newer folder under the same id): not
    /// granted again without the admin.
    /// </summary>
    public const string Recreated = "Recreated";

    /// <summary>The starting set: the union of the user's library roles.</summary>
    public const string StartRoles = "Roles";

    /// <summary>The starting set: nothing, the user's own mode is <c>Only</c> (its <c>Add</c> list supplies the libraries).</summary>
    public const string StartEmpty = "Empty";

    /// <summary>The source of a role.</summary>
    /// <param name="name">The role's name.</param>
    /// <returns><c>Role:&lt;name&gt;</c>.</returns>
    public static string Role(string name) => RolePrefix + name;
}
