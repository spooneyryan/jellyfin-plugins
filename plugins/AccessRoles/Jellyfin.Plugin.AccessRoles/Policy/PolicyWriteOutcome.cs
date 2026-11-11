using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Access;

namespace Jellyfin.Plugin.AccessRoles.Policy;

/// <summary>What <see cref="PolicyWriter.WriteAsync"/> did.</summary>
internal enum PolicyWriteOutcome
{
    /// <summary>The full policy was written with the new library fields.</summary>
    Written,

    /// <summary>The user already had the library fields: nothing was written (the write is skipped when nothing differs).</summary>
    Unchanged,

    /// <summary>
    /// Another field changed between the read and the re-read twice (an admin's concurrent edit): nothing was written,
    /// audited <c>WriteAborted</c>; the next sync tries again.
    /// </summary>
    Aborted,

    /// <summary>The user's lock wasn't free in time (an admin's policy save holds it): nothing was written.</summary>
    LockTimeout,

    /// <summary>
    /// The committed row was written back as it is, library fields included, so the server enforces what its database holds
    /// after a failed save of ours when a concurrent save had changed the library fields (the target waits
    /// for the next run's rules), or by <see cref="PolicyWriter.ResyncAsync"/> when the server's cache had reverted to an
    /// access the database no longer holds.
    /// </summary>
    Resynced,

    /// <summary>The server has no user with the id (deleted meanwhile).</summary>
    UserNotFound,

    /// <summary>Reading or writing the policy failed (logged): nothing was written, or the core refused the write.</summary>
    Failed
}
