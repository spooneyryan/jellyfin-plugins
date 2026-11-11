using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Access;

namespace Jellyfin.Plugin.AccessRoles.Policy;

/// <summary>What a post-write check found.</summary>
internal enum VerificationOutcome
{
    /// <summary>The server enforces the written fields.</summary>
    Holds,

    /// <summary>The server enforces the fields from before the write again: the 10.11.5 sign-in race.</summary>
    Reverted,

    /// <summary>The server enforces other fields (someone changed them since): the sync's drift rules decide.</summary>
    Changed,

    /// <summary>The user is gone.</summary>
    UserNotFound
}
