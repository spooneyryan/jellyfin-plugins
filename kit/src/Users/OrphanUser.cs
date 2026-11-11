using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Users;

/// <summary>
/// A stored user id the server doesn't know (Diagnostics <c>OrphanUsers:[{UserId, LastKnownName, Records}]</c>).
/// </summary>
/// <param name="UserId">The user id.</param>
/// <param name="LastKnownName">The last seen name (<see cref="KnownUsers"/>), or <c>null</c>.</param>
/// <param name="Records">How many records it owns, over every <see cref="IUserDataOwner"/>.</param>
/// <param name="Mark">Its orphan mark; <c>null</c> while the reconciler's guard holds it back.</param>
internal sealed record OrphanUser(Guid UserId, string? LastKnownName, int Records, OrphanMark? Mark)
{
    /// <summary>
    /// Describes the user with plain values (for admins only: it holds a name).
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["UserId"] = KnownUsers.FormatId(UserId),
        ["LastKnownName"] = LastKnownName,
        ["Records"] = Records,
        ["Marked"] = Mark is not null,
        ["MarkedUtc"] = Mark?.MarkedUtc,
        ["Source"] = Mark?.Source.ToString(),
        ["PolicyPending"] = Mark?.PolicyPending
    };
}
