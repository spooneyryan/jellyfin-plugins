using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// The body of <c>POST /AccessRoles/Admin/ReleaseLocks</c>.
/// </summary>
public sealed class AccessRolesReleaseLocksRequest
{
    /// <summary>
    /// Gets or sets the users whose native locks to release; empty or left out: everyone with a native lock to release
    /// (a preference flag the mirror turned off, and with <c>restoreLibraries=true</c> a ledger entry).
    /// </summary>
    public IReadOnlyList<Guid>? UserIds { get; set; }
}
