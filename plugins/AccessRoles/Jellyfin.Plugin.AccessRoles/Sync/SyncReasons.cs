namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// The reasons the library sync gives in its report, its audit lines (<c>Details.Reason</c>) and Diagnostics (every
/// write and skip audited with a reason).
/// </summary>
internal static class SyncReasons
{
    /// <summary>A write: the user's libraries are managed and Access Roles never wrote them.</summary>
    public const string FirstWrite = "FirstWrite";

    /// <summary>A write: the rules give other access than Access Roles last wrote (a role, an override or a library changed).</summary>
    public const string RulesChanged = "RulesChanged";

    /// <summary>A write: the native access was changed outside Access Roles and <c>DriftPolicy=Reapply</c>.</summary>
    public const string Drift = "Drift";

    /// <summary>
    /// A write: a post-write check found the access from before the write enforced again (the 10.11.5 sign-in race).
    /// </summary>
    public const string RevertedAfterWrite = "RevertedAfterWrite";

    /// <summary>
    /// A write, forced: an earlier save of the user's policy failed, so the server's cache may show the access while the
    /// database doesn't hold it (10.11.5, <c>UserManager.cs:719-720</c>).
    /// </summary>
    public const string RewriteAfterFailedSave = "RewriteAfterFailedSave";

    /// <summary>A write: library deletions left the user nothing, and the write only removes ids that are gone or new.</summary>
    public const string NoLibrariesLeft = "NoLibrariesLeft";

    /// <summary>Drift reported, not written: <c>DriftPolicy=ReportOnly</c>.</summary>
    public const string ReportOnly = "ReportOnly";

    /// <summary>A skip: the result is no library at all and wasn't confirmed, while the user still has libraries.</summary>
    public const string NoLibraries = "NoLibraries";

    /// <summary>A skip: the write would leave no enabled administrator with every library, unconfirmed.</summary>
    public const string NoFullAdministrator = "NoFullAdministrator";

    /// <summary>A skip: the user's policy changed while the write was built, twice (<c>WriteAborted</c>).</summary>
    public const string WriteAborted = "WriteAborted";

    /// <summary>A skip: the user's policy lock was busy.</summary>
    public const string LockTimeout = "LockTimeout";

    /// <summary>A skip: the user is gone.</summary>
    public const string UserNotFound = "UserNotFound";

    /// <summary>A skip: reading or writing the policy failed.</summary>
    public const string WriteFailed = "WriteFailed";

    /// <summary>
    /// Nothing written: the user's native library access changed between the run's read and its write (an edit since; the
    /// "expected before" check under the user's lock). Audited <c>DriftDetected</c>; the next run decides.
    /// </summary>
    public const string NativeChanged = "NativeChanged";

    /// <summary>
    /// Not the rules' result: a save of ours failed and the save that came first had changed the library fields too, so the
    /// user's stored policy was written back as it is (the server enforces what its database holds); the next run decides.
    /// </summary>
    public const string ConcurrentSave = "ConcurrentSave";

    /// <summary>A dropped release: the native access isn't the one the choice was computed against.</summary>
    public const string BaselineChanged = "BaselineChanged";

    /// <summary>A dropped release: it carries no baseline (a 0.9.2.0 build wrote it).</summary>
    public const string NoBaseline = "NoBaseline";

    /// <summary>A dropped release: the user already has its access.</summary>
    public const string AlreadyApplied = "AlreadyApplied";

    /// <summary>A dropped release: its choice is a value this build doesn't know.</summary>
    public const string UnknownChoice = "UnknownChoice";

    /// <summary>Paused: <c>data/accessroles/SAFE-MODE</c> exists.</summary>
    public const string SafeMode = "SafeMode";

    /// <summary>Paused: <c>EnforcementEnabled</c> is off.</summary>
    public const string EnforcementDisabled = "EnforcementDisabled";

    /// <summary>Paused: <c>LibrarySyncEnabled</c> is off.</summary>
    public const string LibrarySyncDisabled = "LibrarySyncDisabled";

    /// <summary>Paused: the store is read-only (a newer version's file, or a failed upgrade).</summary>
    public const string StoreReadOnly = "StoreReadOnly";

    /// <summary>
    /// Paused: the store started empty after files with no data and Degraded mode locks users (since 0.9.13 only then).
    /// </summary>
    public const string StoreDegraded = "StoreDegraded";

    /// <summary>
    /// Paused: the store's file was missing or empty at load and the kit restored it from a snapshot
    /// (<c>StoreStatus.RestoredFromSnapshot</c>), so the roles and assignments may be older than the users' native access.
    /// The sync waits for an admin to look at its dry run and confirm (<c>POST Sync?confirmRestore=true</c>), as the design
    /// has it after a restore; the hold outlives a restart (<see cref="RestoreHold"/>).
    /// </summary>
    public const string StoreRestored = "StoreRestored";

    /// <summary>
    /// The sign-in hook found the user still being created by another request: nothing writes a user
    /// before the request that creates them is done (<see cref="LibrarySync.BeginCreation"/>), so their access wasn't
    /// corrected.
    /// </summary>
    public const string BeingCreated = "BeingCreated";

    /// <summary>The pre-merge of an admin's policy save or the sign-in hook threw: what was posted went through unchanged.</summary>
    public const string HookFailed = "HookFailed";

    /// <summary>
    /// A write: a user given the default roles when they were created, whose roles give no library, had nothing an admin
    /// gave them to lose (the server's default access of a new user), so the empty result is written (fail closed).
    /// </summary>
    public const string NewUserNoLibraries = "NewUserNoLibraries";

    /// <summary>
    /// Gets whether a pause reason is the store's doing (the restore hold, a read-only store, Degraded mode), not an
    /// admin's choice (SAFE-MODE, enforcement or the library sync switched off): the decision for the sign-in
    /// hook, which refuses a sign-in broader than the last write only during the store's pauses (an admin's
    /// pause leaves library access untouched).
    /// </summary>
    /// <param name="pause">The pause reason.</param>
    /// <returns><c>true</c> for <see cref="StoreRestored"/>, <see cref="StoreReadOnly"/> and <see cref="StoreDegraded"/>.</returns>
    public static bool IsStoreCaused(string? pause) => pause is StoreRestored or StoreReadOnly or StoreDegraded;
}
