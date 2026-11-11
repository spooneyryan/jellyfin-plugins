using System;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A user's assignment as the caller read it (<c>Users/Bulk</c>'s <c>IfUpdatedAt</c>): its
/// <c>UpdatedUtc</c>, or <c>null</c> for a user without one.
/// </summary>
public sealed class AccessRolesUserTokenRequest
{
    /// <summary>Gets or sets the user.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the assignment's <c>UpdatedUtc</c> as read, or <c>null</c> when the user had none.</summary>
    public DateTime? UpdatedUtc { get; set; }
}
