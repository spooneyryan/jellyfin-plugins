using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// Request body of <c>POST /AccessRoles/Admin/Users/Bulk</c> (the Users tab's Assign roles and Remove roles).
/// </summary>
public sealed class AccessRolesBulkRequest
{
    /// <summary>Gets or sets the users: 1 to 1000 ids of the server's users (404 for one it doesn't have).</summary>
    public IReadOnlyList<Guid>? UserIds { get; set; }

    /// <summary>Gets or sets the roles every one of them gets; each must exist.</summary>
    public IReadOnlyList<Guid>? AddRoleIds { get; set; }

    /// <summary>Gets or sets the roles every one of them loses; each must exist.</summary>
    public IReadOnlyList<Guid>? RemoveRoleIds { get; set; }

    /// <summary>
    /// Gets or sets what the library access of users this takes out of library management becomes;
    /// required when there are such users (400 <c>UnmanageChoiceRequired</c>). The query's <c>onUnmanage</c> counts too.
    /// </summary>
    public AccessRolesUnmanageChoice? OnUnmanage { get; set; }

    /// <summary>
    /// Gets or sets each user's assignment as the caller read it: when given it names every user of
    /// <see cref="UserIds"/> once (400 otherwise), and a user whose assignment isn't the one named (changed, created or
    /// removed since) makes the whole request a 409 <c>Changed</c> that names them; nothing is written. The admin page always
    /// sends it, so what it previewed is what is saved.
    /// </summary>
    public IReadOnlyList<AccessRolesUserTokenRequest>? IfUpdatedAt { get; set; }
}
