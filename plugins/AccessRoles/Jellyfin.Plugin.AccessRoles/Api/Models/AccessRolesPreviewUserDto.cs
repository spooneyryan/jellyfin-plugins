using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// What a change does to one user (<c>Preview</c>'s rows): sections newly locked or unlocked,
/// libraries added or removed (with names), whether a native policy write will follow, and the two blocking warnings.
/// </summary>
public sealed class AccessRolesPreviewUserDto
{
    /// <summary>Gets or sets the user's id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the user's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the user is an administrator (no lock applies to them).</summary>
    public bool IsAdministrator { get; set; }

    /// <summary>Gets or sets the user's state before the change.</summary>
    public AccessRolesUserState StateBefore { get; set; }

    /// <summary>Gets or sets the user's state after the change.</summary>
    public AccessRolesUserState StateAfter { get; set; }

    /// <summary>Gets or sets the sections and surfaces the change locks.</summary>
    public IReadOnlyList<AccessRolesLock> LocksAdded { get; set; } = [];

    /// <summary>Gets or sets the sections and surfaces the change unlocks.</summary>
    public IReadOnlyList<AccessRolesLock> LocksRemoved { get; set; } = [];

    /// <summary>Gets or sets the libraries the user will be able to open, by name.</summary>
    public IReadOnlyList<AccessRolesLibraryNameDto> LibrariesAdded { get; set; } = [];

    /// <summary>Gets or sets the libraries the user won't be able to open any more, by name.</summary>
    public IReadOnlyList<AccessRolesLibraryNameDto> LibrariesRemoved { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the user's native library access differs from the result, so the library
    /// sync will write it.
    /// </summary>
    public bool PolicyWrite { get; set; }

    /// <summary>Gets or sets a value indicating whether the user would have no libraries ("kid2 would have no libraries.").</summary>
    public bool NoLibraries { get; set; }

    /// <summary>Gets or sets a value indicating whether that empty result is refused (no explicit <c>Only…</c> with nothing and its confirmation).</summary>
    public bool NoLibrariesRefused { get; set; }

    /// <summary>Gets or sets the user's departure from library management, or <c>null</c> when the change doesn't take them out.</summary>
    public AccessRolesLeavingDto? LeavesManagement { get; set; }

    /// <summary>Gets or sets the user's warnings after the change.</summary>
    public IReadOnlyList<string> Warnings { get; set; } = [];
}
