using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Kit.Users;

/// <summary>
/// A user id whose records are orphans: admin-visible, excluded from
/// user views and aggregates. Stored by the kit in <c>kit-users.json</c>, so the reconciler's guard counts only ids that
/// would <i>become</i> orphaned in a run, across restarts.
/// </summary>
internal sealed record OrphanMark
{
    /// <summary>Gets the user id.</summary>
    public Guid UserId { get; init; }

    /// <summary>Gets when the id was marked.</summary>
    public DateTime MarkedUtc { get; init; }

    /// <summary>Gets why.</summary>
    public OrphanMarkSource Source { get; init; }

    /// <summary>
    /// Gets the owners (<see cref="IUserDataOwner.Name"/>) whose deleted-user policy failed for this user when Jellyfin
    /// reported the deletion (the owner couldn't count or change its records then, say its archive couldn't be read),
    /// so the daily reconciliation applies it again until it succeeds; null when none.
    /// </summary>
    public IReadOnlyList<string>? PolicyPending { get; init; }

    /// <summary>Gets fields of a newer kit, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
