using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// Request body of <c>POST /Bookmarks/Admin/Users/ApplyPolicy</c>: the unknown user ids Diagnostics lists
/// under <c>Users.OrphanUsers</c>, whose bookmarks get the deleted-user policy after a snapshot.
/// </summary>
public sealed class BookmarkApplyPolicyRequest
{
    /// <summary>
    /// Gets or sets the user ids: 1 to 1000, none of them a user of this server.
    /// </summary>
    public IReadOnlyList<Guid>? UserIds { get; set; }
}
