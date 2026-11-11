using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A row of the Libraries tab (<c>GET /AccessRoles/Admin/Libraries</c>): every library the server lists, and
/// every library the roles, overrides and ledger still refer to that it no longer lists.
/// </summary>
public sealed class AccessRolesLibraryDto
{
    /// <summary>Gets or sets the library's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the library's name (its name when picked, for one the server no longer lists).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the native "Enable the library" switch is on.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the library's state: <c>Active</c>, or <c>Orphaned</c> for one the server no longer lists.</summary>
    public AccessRolesLibraryState State { get; set; }

    /// <summary>Gets or sets the creation time of the library's folder, UTC, or <c>null</c> when unknown.</summary>
    public DateTime? CreatedUtc { get; set; }

    /// <summary>Gets or sets the library's type (<c>movies</c>, <c>tvshows</c>, <c>boxsets</c>…), or <c>null</c>.</summary>
    public string? CollectionType { get; set; }

    /// <summary>Gets or sets the roles that grant it (<c>All</c>, <c>AllExcept</c> not excluding it, <c>Only</c> listing it).</summary>
    public IReadOnlyList<Guid> RoleIds { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether no role grants it, so only users with every library see it ("New library
    /// Anime is in no role").
    /// </summary>
    public bool InNoRole { get; set; }

    /// <summary>Gets or sets the library's folders.</summary>
    public IReadOnlyList<string> Locations { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the library was deleted and created again under the same id since Access
    /// Roles recorded it ("Library Kids was re-created. It is a new library: grant it again?"): until
    /// the admin grants it again (<c>POST Libraries/{id}/GrantAgain</c>), no role's <c>Only</c> list, user's <c>Add</c> or
    /// original access gives it (a re-created library is a new library).
    /// </summary>
    public bool Recreated { get; set; }

    /// <summary>Gets or sets a value indicating whether the library sync's catalog has recorded the library (<c>KnownLibraries</c>).</summary>
    public bool Known { get; set; }

    /// <summary>
    /// Gets or sets the users who can open the library, and why, by name: their native access, the access the
    /// server enforces now, each marked where it isn't what the rules give
    /// (<see cref="AccessRolesLibraryUserDto.Mismatch"/>); empty for a deleted library.
    /// </summary>
    public IReadOnlyList<AccessRolesLibraryUserDto> Users { get; set; } = [];

    /// <summary>
    /// Gets or sets the managed users the rules give the library to who can't open it yet, by name, each with why
    /// (<see cref="AccessRolesLibraryUserDto.Mismatch"/>).
    /// </summary>
    public IReadOnlyList<AccessRolesLibraryUserDto> NotOpenedBy { get; set; } = [];

    /// <summary>Gets or sets the users whose own library overrides add the library ("1 user override referred to it").</summary>
    public IReadOnlyList<AccessRolesUserRefDto> AddedFor { get; set; } = [];

    /// <summary>Gets or sets the users whose own library overrides remove the library.</summary>
    public IReadOnlyList<AccessRolesUserRefDto> RemovedFor { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether <c>POST Libraries/{id}/Purge</c> has something to remove, for a library the
    /// server no longer lists (1.0.1): a grant of it (an <i>Only…</i> role, a user's <c>Add</c>, original access) or a
    /// known library no exclusion names. One only exclusions name (an <i>All except…</i> role, a user's <c>Remove</c>) has
    /// nothing: they keep leaving it out. <c>false</c> for a library the server lists.
    /// </summary>
    public bool Purgeable { get; set; }
}
