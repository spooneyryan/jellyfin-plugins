namespace Jellyfin.Plugin.AccessRoles.Audit;

/// <summary>
/// The audit log's actions: the roles API's since 0.9.2, the library sync's and the library lifecycle's
/// since 0.9.3, the user policy hooks', the default roles' and the import's since 0.9.4, and the settings filter's (blocked
/// requests, filter errors) since 0.9.5.
/// </summary>
internal static class AuditActions
{
    /// <summary>
    /// Requests of one user that a rule refused (403 <c>Locked</c>) or merged (a locked value put back), aggregated per
    /// rule, action, outcome and 10-minute window (<c>Enforcement.BlockCounter</c>).
    /// </summary>
    public const string RequestsBlocked = "RequestsBlocked";

    /// <summary>
    /// Requests a settings rule couldn't decide and refused (fail closed), aggregated like
    /// <see cref="RequestsBlocked"/>.
    /// </summary>
    public const string FilterErrors = "FilterErrors";

    /// <summary>
    /// An admin's (or API key's) Dashboard save changed a managed user's library access, and the rules' result was written
    /// instead: what the save added and removed that the result undid.
    /// </summary>
    public const string ManualEditOverridden = "ManualEditOverridden";

    /// <summary>A new user got the default roles.</summary>
    public const string UserCreatedDefaulted = "UserCreatedDefaulted";

    /// <summary>The current setup was imported as roles and assignments (<c>ImportExisting</c>).</summary>
    public const string ImportApplied = "ImportApplied";

    /// <summary>
    /// A managed non-admin's sign-in was refused, because their native library access gave more than the rules and the
    /// sign-in hook couldn't correct it (fail closed). The token the core had issued was revoked.
    /// </summary>
    public const string SignInRefused = "SignInRefused";

    /// <summary>A role was created.</summary>
    public const string RoleCreated = "RoleCreated";

    /// <summary>A role was changed (name, description, locks or libraries).</summary>
    public const string RoleUpdated = "RoleUpdated";

    /// <summary>A role was deleted.</summary>
    public const string RoleDeleted = "RoleDeleted";

    /// <summary>A user's assignment changed: roles, overrides, library mode or state.</summary>
    public const string AssignmentChanged = "AssignmentChanged";

    /// <summary>The default roles of new users changed.</summary>
    public const string DefaultRolesChanged = "DefaultRolesChanged";

    /// <summary>A user left library management with <c>Keep</c>: their access stays as it is.</summary>
    public const string LibraryAccessFrozen = "LibraryAccessFrozen";

    /// <summary>A deleted user's assignment and ledger entry were removed.</summary>
    public const string UserDeleted = "UserDeleted";

    /// <summary>Users' records were moved to new ids by <i>Remap by name</i>.</summary>
    public const string UsersRemapped = "UsersRemapped";

    /// <summary>The library sync wrote a managed user's library access; <c>Details.Reason</c> says why.</summary>
    public const string PolicySynced = "PolicySynced";

    /// <summary>A managed user's library access was changed outside Access Roles and, with <c>DriftPolicy=ReportOnly</c>, left so.</summary>
    public const string DriftDetected = "DriftDetected";

    /// <summary>A policy write was given up because the user's policy changed while it was built, twice.</summary>
    public const string WriteAborted = "WriteAborted";

    /// <summary>The library sync didn't write a user's library access; <c>Details.Reason</c> says why.</summary>
    public const string SyncSkipped = "SyncSkipped";

    /// <summary>The library sync stopped writing (SAFE-MODE, enforcement or the sync off, a read-only store), or resumed.</summary>
    public const string LibrarySyncPaused = "LibrarySyncPaused";

    /// <summary>The library sync writes again after a pause.</summary>
    public const string LibrarySyncResumed = "LibrarySyncResumed";

    /// <summary>A user who left library management got the access their <c>Restore</c> or <c>GrantAll</c> chose.</summary>
    public const string LibraryAccessRestored = "LibraryAccessRestored";

    /// <summary>
    /// A pending <c>Restore</c> or <c>GrantAll</c> was dropped without a write, because the user's access was changed outside
    /// Access Roles since the choice (<c>Storage/LedgerRelease</c>).
    /// </summary>
    public const string ReleaseDropped = "ReleaseDropped";

    /// <summary>A library deletion left managed users with no library.</summary>
    public const string UsersWithoutLibraries = "UsersWithoutLibraries";

    /// <summary>A new library was listed.</summary>
    public const string LibraryAdded = "LibraryAdded";

    /// <summary>A library was renamed; its references were re-pointed to its new id.</summary>
    public const string LibraryRenamed = "LibraryRenamed";

    /// <summary>A library was deleted; its references are kept, orphaned, until purged.</summary>
    public const string LibraryRemoved = "LibraryRemoved";

    /// <summary>A library was deleted and created again under the same id: a new library until granted again.</summary>
    public const string LibraryRecreated = "LibraryRecreated";

    /// <summary>An admin granted a re-created library again (its earlier grants give it again).</summary>
    public const string LibraryGrantedAgain = "LibraryGrantedAgain";

    /// <summary>An admin purged a deleted library's references.</summary>
    public const string LibraryPurged = "LibraryPurged";

    /// <summary>An admin changed Access Roles' settings on the Settings tab: each setting, before and after.</summary>
    public const string SettingsChanged = "SettingsChanged";

    /// <summary>
    /// A start found the data file it can't trust and locked the managed users' settings (Degraded mode): the
    /// store status, the users, where they were read from.
    /// </summary>
    public const string DegradedModeEntered = "DegradedModeEntered";

    /// <summary>
    /// Degraded mode ended: an admin confirmed (<c>POST Degraded/Resolve</c>), or a start's usable data file manages
    /// every user it locked again.
    /// </summary>
    public const string DegradedModeEnded = "DegradedModeEnded";

    /// <summary>
    /// The optional mirror (<c>MirrorPreferenceLockToPolicy</c>) turned a managed non-admin's native
    /// <c>EnableUserPreferenceAccess</c> off: all five server-stored settings sections are locked.
    /// </summary>
    public const string PreferenceAccessMirrored = "PreferenceAccessMirrored";

    /// <summary>
    /// A flag the mirror turned off is on again: written back on (the sections no longer all locked, the setting off,
    /// <c>ReleaseLocks</c>, the uninstall), or found on already; with why.
    /// </summary>
    public const string PreferenceAccessRestored = "PreferenceAccessRestored";

    /// <summary>
    /// An admin released the native locks (<c>POST ReleaseLocks</c>): the users, what was written back,
    /// whether the original library access was restored too.
    /// </summary>
    public const string LocksReleased = "LocksReleased";
}
