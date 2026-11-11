using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A user's library access (<c>AccessRolesEffectiveDto.Libraries</c>).
/// </summary>
public sealed class AccessRolesEffectiveLibrariesDto
{
    /// <summary>Gets or sets the user's own mode, or <c>null</c> for one a newer build wrote.</summary>
    public AccessRolesUserLibraryMode? UserMode { get; set; }

    /// <summary>
    /// Gets or sets where the starting set came from: <c>Roles</c>, <c>AllLibraries</c>, <c>Empty</c> or <c>Original</c>
    /// (the ledger), or <c>NotManaged</c>, <c>Excluded</c>, <c>CannotManage</c> or <c>UnknownValue</c> when Access Roles leaves
    /// the user's libraries alone.
    /// </summary>
    public string StartingSet { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether Access Roles manages the user's library access.</summary>
    public bool Managed { get; set; }

    /// <summary>Gets or sets a value indicating whether the result is every library, future ones included (<c>EnableAllFolders</c>).</summary>
    public bool EnableAllFolders { get; set; }

    /// <summary>Gets or sets a value indicating whether the result is no library at all.</summary>
    public bool NoLibraries { get; set; }

    /// <summary>Gets or sets a value indicating whether the admin chose that (<c>Only…</c> with nothing, confirmed).</summary>
    public bool NoLibrariesConfirmed { get; set; }

    /// <summary>Gets or sets the libraries the user can open, by name, each with its sources.</summary>
    public IReadOnlyList<AccessRolesEffectiveLibraryDto> Visible { get; set; } = [];

    /// <summary>Gets or sets the libraries the user can't open, by name, each with the reason.</summary>
    public IReadOnlyList<AccessRolesEffectiveLibraryDto> Hidden { get; set; } = [];

    /// <summary>Gets or sets the user's native library fields now.</summary>
    public AccessRolesNativeLibrariesDto Native { get; set; } = new();

    /// <summary>
    /// Gets or sets what the library sync will write: a managed user's result, or a pending release
    /// whose baseline still matches; <c>null</c> when the native fields already match or nothing is to be written.
    /// </summary>
    public AccessRolesPendingWriteDto? PendingWrite { get; set; }

    /// <summary>
    /// Gets or sets the user's departure from library management whose write is still to come (a <c>Restore</c> or
    /// <c>GrantAll</c> for the sync, with the native access it was computed against), or <c>null</c>.
    /// </summary>
    public AccessRolesPendingReleaseDto? PendingRelease { get; set; }

    /// <summary>Gets or sets the library ids the user's roles and overrides refer to that the server no longer lists.</summary>
    public IReadOnlyList<Guid> MissingReferences { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the native access drifted: Access Roles writes these libraries, and the native
    /// access is neither the result nor what it last wrote.
    /// </summary>
    public bool Drift { get; set; }

    /// <summary>Gets or sets the library fields Access Roles last wrote for the user (the ledger's <c>LastWrite</c>), or <c>null</c>.</summary>
    public AccessRolesNativeLibrariesDto? LastWrite { get; set; }

    /// <summary>Gets or sets when Access Roles last wrote them, UTC.</summary>
    public DateTime? LastWriteUtc { get; set; }
}
