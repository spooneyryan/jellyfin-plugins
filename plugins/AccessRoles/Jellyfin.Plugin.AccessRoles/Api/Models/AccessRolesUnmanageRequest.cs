using System;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// Request body of <c>POST /AccessRoles/Admin/Users/{userId}/Unmanage</c>: the user's assignment
/// goes, and their library access becomes what the choice says.
/// </summary>
public sealed class AccessRolesUnmanageRequest
{
    /// <summary>
    /// Gets or sets the choice: <c>Restore</c> (the original access, from the ledger; 409 without an entry), <c>Keep</c> or
    /// <c>GrantAll</c>. Required (400).
    /// </summary>
    public AccessRolesUnmanageChoice? Choice { get; set; }

    /// <summary>
    /// Gets or sets the assignment's <c>UpdatedUtc</c> as the caller read it, if given: an assignment
    /// changed since, or none, is left as it is and the answer is 409 <c>Changed</c>.
    /// </summary>
    public DateTime? IfUpdatedAt { get; set; }
}
