using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// A user's library access as the rules make it.
/// </summary>
internal sealed record LibraryDecision
{
    /// <summary>
    /// Gets a value indicating whether Access Roles owns the user's <c>EnableAllFolders</c> and <c>EnabledFolders</c>
    /// (Access Roles owns the library fields only then): a library role, a user mode other than <c>Inherit</c>, or a library override.
    /// </summary>
    public bool Managed { get; init; }

    /// <summary>
    /// Gets a value indicating whether nothing may be written although the user is managed: a value a newer build wrote
    /// <see cref="EnableAllFolders"/> and <see cref="EnabledFolders"/> are then the native ones.
    /// </summary>
    public bool WriteBlocked { get; init; }

    /// <summary>Gets the user's own library mode (<c>Inherit</c> without an assignment).</summary>
    public LenientEnum<AccessRolesUserLibraryMode> UserMode { get; init; }

    /// <summary>
    /// Gets where the starting set came from: <c>Roles</c>, <c>AllLibraries</c>, <c>Empty</c> or <c>Original</c> (the ledger)
    /// for managed users, else the state that leaves libraries alone (<c>NotManaged</c>, <c>Excluded</c>, <c>CannotManage</c>,
    /// <c>UnknownValue</c>).
    /// </summary>
    public string StartingSet { get; init; } = AccessSources.NotManaged;

    /// <summary>Gets a value indicating whether the starting set includes future libraries.</summary>
    public bool IncludesFuture { get; init; }

    /// <summary>
    /// Gets a value indicating whether the user should have every library (<c>EnableAllFolders</c>): what the library sync
    /// will write for a managed user, the native value otherwise.
    /// </summary>
    public bool EnableAllFolders { get; init; }

    /// <summary>Gets the <c>EnabledFolders</c> the user should have (empty with <see cref="EnableAllFolders"/>).</summary>
    public IReadOnlyList<Guid> EnabledFolders { get; init; } = [];

    /// <summary>Gets the libraries the user can open, with their sources, by name.</summary>
    public IReadOnlyList<VisibleLibrary> Visible { get; init; } = [];

    /// <summary>Gets the libraries the user can't open, with the reason, by name.</summary>
    public IReadOnlyList<HiddenLibrary> Hidden { get; init; } = [];

    /// <summary>Gets a value indicating whether a managed user's result is no library at all.</summary>
    public bool NoLibraries { get; init; }

    /// <summary>
    /// Gets a value indicating whether the admin chose no libraries: mode <c>Only</c>, nothing added, and
    /// <c>ConfirmNoLibraries</c> (the only empty result that is written).
    /// </summary>
    public bool NoLibrariesConfirmed { get; init; }

    /// <summary>Gets the library ids the user's roles and overrides refer to that the server doesn't list (deleted).</summary>
    public IReadOnlyList<Guid> MissingReferences { get; init; } = [];

    /// <summary>Gets the libraries of the original access that were created again since, not granted from it.</summary>
    public IReadOnlyList<Guid> Recreated { get; init; } = [];

    /// <summary>Gets a value indicating whether the library sync would write this result (managed, not blocked).</summary>
    public bool Writes => Managed && !WriteBlocked;
}
