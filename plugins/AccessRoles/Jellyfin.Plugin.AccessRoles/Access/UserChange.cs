using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// What a change to the roles and assignments does to one user (<c>Preview</c>).
/// </summary>
internal sealed record UserChange
{
    /// <summary>Gets the user.</summary>
    public required ServerUser User { get; init; }

    /// <summary>Gets the user's effective rules before the change.</summary>
    public required EffectiveAccess Before { get; init; }

    /// <summary>Gets the user's effective rules after the change.</summary>
    public required EffectiveAccess After { get; init; }

    /// <summary>Gets the sections and surfaces the change locks.</summary>
    public IReadOnlyList<AccessRolesLock> LocksAdded { get; init; } = [];

    /// <summary>Gets the sections and surfaces the change unlocks.</summary>
    public IReadOnlyList<AccessRolesLock> LocksRemoved { get; init; } = [];

    /// <summary>Gets the libraries the user can open after the change and couldn't before, by name.</summary>
    public IReadOnlyList<CatalogLibrary> LibrariesAdded { get; init; } = [];

    /// <summary>Gets the libraries the user could open before the change and can't after, by name.</summary>
    public IReadOnlyList<CatalogLibrary> LibrariesRemoved { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the user should have every library before the change: the result the library sync
    /// writes for a managed user, the native access otherwise.
    /// </summary>
    public bool BeforeEnableAllFolders { get; init; }

    /// <summary>Gets the libraries the user should have before the change, without <see cref="BeforeEnableAllFolders"/>.</summary>
    public IReadOnlyList<Guid> BeforeEnabledFolders { get; init; } = [];

    /// <summary>Gets a value indicating whether the user should have every library after the change.</summary>
    public bool AfterEnableAllFolders { get; init; }

    /// <summary>Gets the libraries the user should have after the change, without <see cref="AfterEnableAllFolders"/>.</summary>
    public IReadOnlyList<Guid> AfterEnabledFolders { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the user's native library fields differ from the access after the change, so the
    /// library sync would write them.
    /// </summary>
    public bool PolicyWrite { get; init; }

    /// <summary>Gets the user's departure from library management, or <c>null</c> when the change doesn't take them out.</summary>
    public LeavingResult? Leaving { get; init; }

    /// <summary>
    /// Gets a value indicating whether the change would leave the user with no libraries by accident (409 <c>NoLibraries</c>
    /// on a save).
    /// </summary>
    public bool NoLibrariesRefused { get; init; }

    /// <summary>Gets a value indicating whether the user's assignment itself changes (roles, overrides, state).</summary>
    public bool AssignmentChanged { get; init; }

    /// <summary>Gets a value indicating whether the change does anything for the user (the preview lists only those).</summary>
    public bool Affected
        => AssignmentChanged || LocksAdded.Count > 0 || LocksRemoved.Count > 0 || LibrariesAdded.Count > 0 || LibrariesRemoved.Count > 0
            || Before.State != After.State || Before.Libraries.Managed != After.Libraries.Managed || Leaving is not null || NoLibrariesRefused;
}
