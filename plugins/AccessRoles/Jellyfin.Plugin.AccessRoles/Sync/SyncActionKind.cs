namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>What the library sync does for one user.</summary>
internal enum SyncActionKind
{
    /// <summary>A managed user's library access is written: the resolver's result.</summary>
    Write,

    /// <summary>A user who left library management gets their pending <c>Restore</c> or <c>GrantAll</c> written.</summary>
    Release,

    /// <summary>A pending release is dropped without a write (its baseline no longer matches, or it is already applied).</summary>
    DropRelease,

    /// <summary>A managed user's drifted access is left as it is and reported (<c>DriftPolicy=ReportOnly</c>).</summary>
    Drift,

    /// <summary>Nothing is written for the user, for the reason given (no libraries, the lockout guard, a failed write…).</summary>
    Skip
}
