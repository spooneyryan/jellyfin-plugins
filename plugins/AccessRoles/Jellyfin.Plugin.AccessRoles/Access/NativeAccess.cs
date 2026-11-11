using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// The fields of a user's native policy Access Roles reads: the library access it will own for managed users,
/// and the flags the effective view reports.
/// </summary>
/// <param name="EnableAllFolders">Whether every library is granted, future ones included.</param>
/// <param name="EnabledFolders">The granted libraries otherwise.</param>
/// <param name="EnableUserPreferenceAccess">The native preference switch.</param>
/// <param name="EnableLiveTvManagement">Whether the user may manage Live TV recordings and settings.</param>
/// <param name="BlockedMediaFolders">The legacy block list; non-empty means the user can't be managed.</param>
internal sealed record NativeAccess(
    bool EnableAllFolders,
    IReadOnlyList<Guid> EnabledFolders,
    bool EnableUserPreferenceAccess,
    bool EnableLiveTvManagement,
    IReadOnlyList<Guid> BlockedMediaFolders)
{
    /// <summary>Gets the access of a new user with Jellyfin's defaults (every library).</summary>
    public static NativeAccess Default { get; } = new(true, [], true, false, []);

    /// <summary>Gets the two library fields Access Roles owns for managed users.</summary>
    public LibraryFields Libraries => LibraryFields.Of(EnableAllFolders, EnabledFolders);

    /// <summary>
    /// Gets whether this access writes the same library fields as another: both every library, or both the same list.
    /// </summary>
    /// <param name="enableAllFolders">The other's <c>EnableAllFolders</c>.</param>
    /// <param name="enabledFolders">The other's <c>EnabledFolders</c>.</param>
    /// <returns><c>true</c> when a write of the other would change nothing.</returns>
    public bool SameLibraries(bool enableAllFolders, IEnumerable<Guid> enabledFolders)
    {
        ArgumentNullException.ThrowIfNull(enabledFolders);
        return enableAllFolders == EnableAllFolders && (enableAllFolders || enabledFolders.ToHashSet().SetEquals(EnabledFolders));
    }
}
