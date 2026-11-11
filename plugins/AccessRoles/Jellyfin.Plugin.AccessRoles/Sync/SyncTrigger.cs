namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>What started a library sync run, or a write of the user policy hooks.</summary>
internal enum SyncTrigger
{
    /// <summary>The first run after Jellyfin's startup gate (trigger 8).</summary>
    Startup,

    /// <summary>A role, assignment or default-role change was saved (trigger 1), debounced 2 s.</summary>
    StoreChanged,

    /// <summary>A library structure call the filter saw (trigger 5, at once) or a new library's <c>ItemAdded</c> (trigger 6).</summary>
    LibraryChanged,

    /// <summary>The backstop every <c>ReconcileIntervalMinutes</c> (trigger 8).</summary>
    Interval,

    /// <summary>An admin's <c>POST /AccessRoles/Admin/Sync</c>.</summary>
    Manual,

    /// <summary>A run again after a write was aborted, timed out or failed, or a post-write check found other access.</summary>
    Retry,

    /// <summary>An admin's or API key's <c>POST /Users/{id}/Policy</c> of a managed user, pre-merged.</summary>
    PolicySave,

    /// <summary>A managed user's sign-in, corrected before the response, and its runs 5 s and 30 s later.</summary>
    SignIn,

    /// <summary>A new user given the default roles: runs at once, 5 s and 30 s later.</summary>
    UserCreated,

    /// <summary>A <c>UserUpdatedEventArgs</c> on Jellyfin 12.1 and later that wasn't the writer's own write.</summary>
    UserUpdated,

    /// <summary>
    /// A settings save that turned on <c>DriftPolicy=Reapply</c>, <c>LibrarySyncEnabled</c> or <c>EnforcementEnabled</c>,
    /// debounced 2 s (the Settings tab says such a setting "applies at once").
    /// </summary>
    SettingsChanged
}
