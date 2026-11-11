using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Audit;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// The audit lines of Degraded mode (<c>DegradedModeEntered</c> and <c>DegradedModeEnded</c>). Ids only, never a user's name:
/// the users of a file that can't be trusted may not be the server's any more.
/// </summary>
internal static class DegradedAudit
{
    /// <summary>
    /// The line of a start that began it.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>The line.</returns>
    public static AuditRecord Entered(DegradedState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new AuditRecord(AuditActor.System, AuditActions.DegradedModeEntered, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Status"] = state.Because,
            ["EveryNonAdmin"] = state.EveryNonAdmin,
            ["Users"] = state.Users.Order().ToList(),
            ["Source"] = state.Source
        });
    }

    /// <summary>
    /// The line of its end.
    /// </summary>
    /// <param name="actor">The admin who confirmed, or the system.</param>
    /// <param name="users">The users it locked.</param>
    /// <param name="reason">Why it ended: <c>AdminConfirmed</c>, or <c>TheDataFileManagesThemAgain</c>.</param>
    /// <param name="everyNonAdmin">Whether it locked every non-admin.</param>
    /// <returns>The line.</returns>
    public static AuditRecord Ended(AuditActor actor, IReadOnlySet<Guid> users, string reason, bool everyNonAdmin = false)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(users);
        return new AuditRecord(actor, AuditActions.DegradedModeEnded, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Reason"] = reason,
            ["EveryNonAdmin"] = everyNonAdmin,
            ["Users"] = users.Order().ToList()
        });
    }
}
