using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// The users a degraded start locks (Degraded mode): every server-enforced settings section and both
/// surfaces, on top of what the document gives them, for the users listed as managed in the newest readable copy of the
/// data, or for every non-admin when no copy is readable.
/// </summary>
/// <param name="Because">The store status that made a start degraded (this one's, or the earlier start's when
/// <paramref name="FromEarlierStart"/>).</param>
/// <param name="EveryNonAdmin">Whether every non-admin is locked: nothing listing the managed users could be read.</param>
/// <param name="Users">The users locked.</param>
/// <param name="Source">The file the users were read from, relative to the data folder, or <c>null</c>.</param>
/// <param name="SinceUtc">When the first degraded start of this run of them began.</param>
/// <param name="FromEarlierStart">Whether the store is usable now and the users it doesn't manage stay locked until an admin
/// confirms: an earlier degraded start's marker (<see cref="DegradedMode.MarkerFileName"/>) lists them, or the copy a restore
/// at this start moved aside as unreadable does.</param>
internal sealed record DegradedState(string Because, bool EveryNonAdmin, IReadOnlySet<Guid> Users, string? Source, DateTime SinceUtc, bool FromEarlierStart)
{
    /// <summary>
    /// Gets whether a user is locked by it.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns><c>true</c> for a listed user, or anyone while every non-admin is locked.</returns>
    public bool Applies(Guid userId) => EveryNonAdmin || Users.Contains(userId);
}
