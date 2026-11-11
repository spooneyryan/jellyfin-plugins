using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// Request body of <c>POST /AccessRoles/Admin/Roles</c> and <c>PUT /AccessRoles/Admin/Roles/{id}</c>, and one of
/// <c>POST /AccessRoles/Admin/Preview</c>'s proposed roles: the whole role.
/// </summary>
public sealed class AccessRolesRoleRequest
{
    /// <summary>
    /// Gets or sets the role this proposal replaces, in a preview only (none for a new role); a PUT takes it from the route
    /// and refuses another one, a POST refuses one.
    /// </summary>
    public Guid? Id { get; set; }

    /// <summary>
    /// Gets or sets the role's name: 1 to 100 characters after trimming, no control characters, unique among roles ignoring
    /// letter case (409 otherwise).
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the admin's description, up to 500 characters; none clears it.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the locks; left out, a POST with a <see cref="Preset"/> takes the preset's, otherwise none.
    /// </summary>
    public IReadOnlyList<AccessRolesLock>? Locks { get; set; }

    /// <summary>
    /// Gets or sets the library grant; left out (or its mode), a POST with a <see cref="Preset"/> takes the preset's mode,
    /// otherwise <c>NotManaged</c>.
    /// </summary>
    public AccessRolesRoleLibrariesRequest? Libraries { get; set; }

    /// <summary>
    /// Gets or sets the preset a new role starts from (POST and preview only): it fills the locks and the library mode the
    /// request leaves out. <c>Kid</c> and <c>Guest</c> grant <c>Only…</c>, so they need at least one
    /// library (400 <c>EmptyOnlyRole</c>).
    /// </summary>
    public AccessRolesPreset? Preset { get; set; }

    /// <summary>
    /// Gets or sets the role's <c>UpdatedUtc</c> as last read (PUT only, required): a role changed since is left as it is
    /// and the answer is 409.
    /// </summary>
    public DateTime? IfUpdatedAt { get; set; }

    /// <summary>
    /// Gets or sets the role's <c>MembersToken</c> as last read (PUT only, optional): when the role's
    /// members, or their assignments, changed since, the role is left as it is and the answer is 409 <c>Changed</c>.
    /// </summary>
    public string? IfMembers { get; set; }
}
