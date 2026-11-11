using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// The body of <c>POST /UserRatings/Admin/Users/ApplyPolicy</c>: the unknown user ids
/// Diagnostics lists under <c>Users.OrphanUsers</c>, whose ratings get <c>DeletedUserPolicy</c> after a snapshot:
/// <c>{"UserIds": ["…"]}</c>.
/// </summary>
public sealed class UserRatingsApplyPolicyRequest
{
    /// <summary>
    /// Gets or sets the user ids: 1 to 1000, none of them empty or a user of this server; anything else is a 400.
    /// </summary>
    public IReadOnlyList<Guid>? UserIds { get; set; }
}
