using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// A pure function: roles, a user's assignment, the ledger and the library catalog give the user's
/// <see cref="EffectiveAccess"/>, every lock and library with its source.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Locks accumulate:</b> a section or surface is locked if any of the user's roles locks it; the user's own
/// <c>Lock</c> or <c>Unlock</c> wins over every role. No lock applies to an administrator, nor to an unmanaged or
/// excluded user.</item>
/// <item><b>Libraries add up:</b> the starting set is every library (user mode <c>All</c>), nothing (<c>Only</c>), the union
/// of the library roles' grants (<c>Inherit</c> with a library role; <c>All</c> and <c>AllExcept</c> expanded against the
/// current libraries), or the user's original access from the ledger (<c>Inherit</c> without a library role but with
/// overrides; the native access while no ledger entry exists). Then <c>(start ∪ Add) − Remove</c>; <c>Remove</c> beats
/// every grant. A start that includes future libraries and an empty <c>Remove</c> is <c>EnableAllFolders=true</c>. Library
/// roles apply to administrators too (<c>Folder.IsVisible</c> has no admin bypass).</item>
/// <item><b>New libraries:</b> <c>All</c> and <c>AllExcept</c> include them by definition, <c>Only</c> never.
/// A library deleted and created again under the same id is new: an <c>Only</c> reference marked orphaned, and a
/// library whose folder is newer than the created time <c>KnownLibraries</c> recorded, aren't granted by a role's
/// <c>Only</c> list, a user's <c>Add</c> list or the original access (hidden as <c>Recreated</c>) until the admin grants it
/// again; an <c>AllExcept</c> list or a <c>Remove</c> keeps leaving the id out.</item>
/// <item><b>No empty result by accident:</b> <see cref="LibraryDecision.NoLibraries"/> marks one, and
/// <see cref="LibraryDecision.NoLibrariesConfirmed"/> the only one that may be written.</item>
/// <item><b>Values a newer build wrote</b>: an unknown state or library mode locks every settings section and
/// blocks every write; unknown lock names and overrides are ignored.</item>
/// </list>
/// </remarks>
internal static class EffectiveRulesResolver
{
    private static readonly TimeSpan _createdTolerance = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Finds a user's assignment.
    /// </summary>
    /// <param name="document">The store document.</param>
    /// <param name="userId">The user id.</param>
    /// <returns>The assignment, or <c>null</c>.</returns>
    public static RoleAssignment? FindAssignment(AccessRolesDocument document, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Assignments.FirstOrDefault(a => a.UserId.Equals(userId));
    }

    /// <summary>
    /// Resolves a user's effective rules from the stored document.
    /// </summary>
    /// <param name="document">The store document (or a proposed one).</param>
    /// <param name="catalog">The libraries now.</param>
    /// <param name="user">The user.</param>
    /// <returns>The effective rules.</returns>
    public static EffectiveAccess Resolve(AccessRolesDocument document, LibraryCatalog catalog, ServerUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Resolve(document, catalog, user, FindAssignment(document, user.Id));
    }

    /// <summary>
    /// Resolves a user's effective rules with a given assignment (the stored one, a proposed one, or none).
    /// </summary>
    /// <param name="document">The store document, for the roles, the ledger and the known libraries.</param>
    /// <param name="catalog">The libraries now.</param>
    /// <param name="user">The user.</param>
    /// <param name="assignment">The assignment, or <c>null</c> for an unmanaged user.</param>
    /// <returns>The effective rules.</returns>
    public static EffectiveAccess Resolve(AccessRolesDocument document, LibraryCatalog catalog, ServerUser user, RoleAssignment? assignment)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(user);

        var roles = RolesOf(document, assignment, out var missingRoles);
        var (unknownValues, blocking) = UnknownValues(assignment, roles);
        var state = StateOf(assignment, roles, user, blocking);
        var locks = ResolveLocks(user.IsAdministrator, assignment, roles, state);
        var libraries = ResolveLibraries(document, catalog, user, assignment, roles, state);

        // A pending release (a Restore or GrantAll for the library sync to write) applies while the libraries aren't managed; a save that
        // manages them again clears it (ChangeAnalysis.KeepLedger).
        var release = !libraries.Managed && document.Ledger.TryGetValue(user.Id, out var entry) ? entry.Release : null;
        var baselineMatches = release is not null && BaselineMatches(user, release);
        var warnings = Warnings(user, state, roles, missingRoles, locks, libraries, catalog, document);
        if (release is not null && !baselineMatches)
        {
            warnings.Add(string.Format(
                CultureInfo.InvariantCulture,
                release.Baseline is null
                    ? "{0} was chosen for this user's libraries without a record of the access it was made against: the library sync drops it without writing."
                    : "{0} was chosen for this user's libraries, and their library access was changed outside Access Roles since: the library sync won't overwrite that change, and drops the choice.",
                release.Choice.Name));
        }

        // Drift: the native access is neither the result nor what Access Roles last wrote, so someone
        // changed it outside Access Roles since the last write. Not for a result of no library nobody chose:
        // the library sync never writes that by accident, so it leaves the access alone (a NoLibraries
        // skip) and there is nothing to keep or revert. A chosen one (Only with nothing, confirmed) is managed like any
        // other result, so a library given natively since is drift.
        var lastWrite = document.Ledger.TryGetValue(user.Id, out var ledger) ? ledger.LastWrite : null;
        var drift = libraries.Writes
            && !(libraries.NoLibraries && !libraries.NoLibrariesConfirmed)
            && lastWrite is not null
            && !user.Native.SameLibraries(libraries.EnableAllFolders, libraries.EnabledFolders)
            && !user.Native.SameLibraries(lastWrite.EnableAllFolders, lastWrite.EnabledFolders);
        if (drift)
        {
            warnings.Add("This user's library access was changed outside Access Roles since its last write.");
        }

        return new EffectiveAccess
        {
            User = user,
            State = state,
            Assignment = assignment,
            Roles = roles,
            MissingRoleIds = missingRoles,
            Locks = locks,
            Libraries = libraries,
            PendingRelease = release,
            PendingReleaseBaselineMatches = baselineMatches,
            LastWrite = lastWrite,
            Drift = drift,
            UnknownValues = unknownValues,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Gets whether a user's native library access still is the one a pending release was computed against (its
    /// <see cref="LedgerRelease.Baseline"/>): the library sync writes the release only then, and drops a release
    /// whose baseline no longer matches, or that has none, without writing.
    /// </summary>
    /// <param name="user">The user, with their native access now.</param>
    /// <param name="release">The release.</param>
    /// <returns><c>true</c> when the release may be written.</returns>
    public static bool BaselineMatches(ServerUser user, LedgerRelease release)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(release);
        return release.Baseline is { } baseline && user.Native.SameLibraries(baseline.EnableAllFolders, baseline.EnabledFolders);
    }

    /// <summary>
    /// Gets the locks that apply to a non-administrator with this assignment, exactly as
    /// <see cref="Resolve(AccessRolesDocument, LibraryCatalog, ServerUser, RoleAssignment?)"/> decides them, from the store
    /// alone: no server user and no library catalog (<c>EffectiveRulesCache</c>: the lock snapshot is built
    /// from the store on the filter's first use and never waits for the catalog). Whether the caller is an administrator is
    /// the filter's to decide, from the role claim; a <c>CannotManage</c> user's locks apply as a managed
    /// user's do, so the native policy isn't needed.
    /// </summary>
    /// <param name="document">The store document.</param>
    /// <param name="assignment">The user's assignment, or <c>null</c> (no lock).</param>
    /// <returns>The locked sections and surfaces.</returns>
    public static IReadOnlySet<AccessRolesLock> LockedForNonAdministrator(AccessRolesDocument document, RoleAssignment? assignment)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (assignment is null)
        {
            return new HashSet<AccessRolesLock>();
        }

        var roles = RolesOf(document, assignment, out _);
        var (_, blocking) = UnknownValues(assignment, roles);
        var state = LockState(assignment, blocking);
        return ResolveLocks(isAdministrator: false, assignment, roles, state).Where(l => l.Value.Locked).Select(l => l.Key).ToHashSet();
    }

    /// <summary>
    /// Gets whether a role grants a library the server lists, as the rules apply it: <c>All</c> every library,
    /// <c>AllExcept</c> every one but the listed, <c>Only</c> a listed one, unless its reference is marked orphaned or the
    /// library was created again since Access Roles knew it (a new library).
    /// </summary>
    /// <param name="document">The store document, for the known libraries' created times.</param>
    /// <param name="catalog">The libraries now.</param>
    /// <param name="role">The role.</param>
    /// <param name="libraryId">The library.</param>
    /// <returns><c>true</c> when the role's members get the library from it.</returns>
    public static bool Grants(AccessRolesDocument document, LibraryCatalog catalog, AccessRole role, Guid libraryId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(role);
        if (!catalog.Contains(libraryId))
        {
            return false;
        }

        return role.Libraries.Mode.Value switch
        {
            AccessRolesRoleLibraryMode.All => true,
            AccessRolesRoleLibraryMode.AllExcept => !role.Libraries.Refs.Any(r => r.Id.Equals(libraryId)),
            AccessRolesRoleLibraryMode.Only => role.Libraries.Refs.Any(r => r.Id.Equals(libraryId) && !r.Orphaned) && !IsRecreated(document, catalog, libraryId),
            _ => false
        };
    }

    /// <summary>
    /// Gets whether an assignment asks for library management: a role that manages libraries, a user mode other than
    /// <c>Inherit</c>, or an <c>Add</c> or <c>Remove</c> entry.
    /// </summary>
    /// <param name="assignment">The assignment.</param>
    /// <param name="roles">Its roles.</param>
    /// <returns><c>true</c> when the user's libraries would be managed.</returns>
    public static bool WantsLibraries(RoleAssignment assignment, IReadOnlyList<AccessRole> roles)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(roles);
        return !assignment.Libraries.Mode.Is(AccessRolesUserLibraryMode.Inherit)
            || assignment.Libraries.Add.Count > 0
            || assignment.Libraries.Remove.Count > 0
            || roles.Any(r => !r.Libraries.Mode.Is(AccessRolesRoleLibraryMode.NotManaged));
    }

    /// <summary>
    /// Gets the libraries of a ledger entry's original access that may be granted from it: the ones the
    /// server still lists, without a library created again since (a newer folder under the same id), which needs the admin.
    /// </summary>
    /// <param name="document">The store document, for the known libraries' created times.</param>
    /// <param name="catalog">The libraries now.</param>
    /// <param name="entry">The ledger entry.</param>
    /// <param name="recreated">The libraries left out because they were created again.</param>
    /// <returns>The library ids, by name.</returns>
    public static IReadOnlyList<Guid> OriginalLibraries(AccessRolesDocument document, LibraryCatalog catalog, LedgerEntry entry, out IReadOnlyList<Guid> recreated)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(entry);
        var existing = entry.OriginalEnabledFolders.Distinct().Where(catalog.Contains).ToList();
        recreated = [.. existing.Where(id => IsRecreated(document, catalog, id))];
        var again = recreated;
        return catalog.Order(existing.Where(id => !again.Contains(id)));
    }

    private static List<AccessRole> RolesOf(AccessRolesDocument document, RoleAssignment? assignment, out List<Guid> missingRoles)
    {
        var roles = new List<AccessRole>();
        missingRoles = [];
        var ids = assignment?.RoleIds.Distinct().ToList() ?? [];

        // An index of the roles only when the lookups would add up (an import of many roles):
        // the first role of each id, as FirstOrDefault finds it.
        Dictionary<Guid, AccessRole>? index = null;
        if (ids.Count > 4 && document.Roles.Count > 16)
        {
            index = [];
            foreach (var r in document.Roles)
            {
                index.TryAdd(r.Id, r);
            }
        }

        foreach (var id in ids)
        {
            if ((index is null ? document.Roles.FirstOrDefault(r => r.Id.Equals(id)) : index.GetValueOrDefault(id)) is { } role)
            {
                roles.Add(role);
            }
            else
            {
                missingRoles.Add(id);
            }
        }

        return roles;
    }

    // The state as far as the locks go: StateOf without the native policy, which only tells CannotManage from Managed, and
    // both get their locks.
    private static AccessRolesUserState LockState(RoleAssignment assignment, bool blockingUnknown)
    {
        if (!assignment.State.IsKnown)
        {
            return AccessRolesUserState.UnknownValue;
        }

        if (assignment.State.Is(AccessRolesAssignmentState.Excluded))
        {
            return AccessRolesUserState.Excluded;
        }

        return blockingUnknown ? AccessRolesUserState.UnknownValue : AccessRolesUserState.Managed;
    }

    private static (IReadOnlyList<string> Values, bool Blocking) UnknownValues(RoleAssignment? assignment, IReadOnlyList<AccessRole> roles)
    {
        var values = new List<string>();
        var blocking = false;
        if (assignment is not null)
        {
            if (!assignment.State.IsKnown)
            {
                values.Add("State=" + assignment.State.Name);
                blocking = true;
            }

            if (!assignment.Libraries.Mode.IsKnown)
            {
                values.Add("Libraries.Mode=" + assignment.Libraries.Mode.Name);
                blocking = true;
            }

            values.AddRange(assignment.LockOverrides.Where(o => !o.Key.IsKnown || !o.Value.IsKnown).Select(o => "LockOverrides." + o.Key.Name + "=" + o.Value.Name));
        }

        foreach (var role in roles)
        {
            if (role.HasUnknownLibraryMode)
            {
                values.Add("Role " + role.Name + ": Libraries.Mode=" + role.Libraries.Mode.Name);
                blocking = true;
            }

            values.AddRange(role.Locks.Where(l => !l.IsKnown).Select(l => "Role " + role.Name + ": Locks=" + l.Name));
        }

        return (values, blocking);
    }

    private static AccessRolesUserState StateOf(RoleAssignment? assignment, IReadOnlyList<AccessRole> roles, ServerUser user, bool blockingUnknown)
    {
        if (assignment is null)
        {
            return AccessRolesUserState.NotManaged;
        }

        if (!assignment.State.IsKnown)
        {
            return AccessRolesUserState.UnknownValue;
        }

        if (assignment.State.Is(AccessRolesAssignmentState.Excluded))
        {
            return AccessRolesUserState.Excluded;
        }

        if (blockingUnknown)
        {
            return AccessRolesUserState.UnknownValue;
        }

        // A legacy BlockedMediaFolders list replaces the allow-list check and can't be written through the API.
        return assignment.State.Is(AccessRolesAssignmentState.CannotManage) || (user.Native.BlockedMediaFolders.Count > 0 && WantsLibraries(assignment, roles))
            ? AccessRolesUserState.CannotManage
            : AccessRolesUserState.Managed;
    }

    private static Dictionary<AccessRolesLock, LockDecision> ResolveLocks(bool isAdministrator, RoleAssignment? assignment, IReadOnlyList<AccessRole> roles, AccessRolesUserState state)
    {
        var locks = new Dictionary<AccessRolesLock, LockDecision>();
        foreach (var name in AccessPresets.AllLocks)
        {
            locks[name] = Decide(name);
        }

        return locks;

        LockDecision Decide(AccessRolesLock name)
        {
            if (isAdministrator)
            {
                return new LockDecision(false, AccessSources.Administrator);
            }

            switch (state)
            {
                case AccessRolesUserState.NotManaged:
                    return new LockDecision(false, AccessSources.NotManaged);
                case AccessRolesUserState.Excluded:
                    return new LockDecision(false, AccessSources.Excluded);
                case AccessRolesUserState.UnknownValue when AccessPresets.SettingsSections.Contains(name):
                    return new LockDecision(true, AccessSources.UnknownValue);
            }

            if (assignment is not null && assignment.LockOverrides.TryGetValue(name, out var choice) && choice.IsKnown && !choice.Is(AccessRolesLockOverride.Inherit))
            {
                return new LockDecision(choice.Is(AccessRolesLockOverride.Lock), AccessSources.UserOverride);
            }

            return roles.FirstOrDefault(r => r.KnownLocks.Contains(name)) is { } role
                ? new LockDecision(true, AccessSources.Role(role.Name))
                : new LockDecision(false, AccessSources.Default);
        }
    }

    private static LibraryDecision ResolveLibraries(
        AccessRolesDocument document,
        LibraryCatalog catalog,
        ServerUser user,
        RoleAssignment? assignment,
        IReadOnlyList<AccessRole> roles,
        AccessRolesUserState state)
    {
        var userMode = assignment?.Libraries.Mode ?? default;
        switch (state)
        {
            case AccessRolesUserState.NotManaged or AccessRolesUserState.Excluded or AccessRolesUserState.CannotManage:
                return Native(catalog, user, userMode, state.ToString(), managed: false);
            case AccessRolesUserState.UnknownValue:
                return Native(catalog, user, userMode, AccessSources.UnknownValue, managed: true);
        }

        if (assignment is null || !WantsLibraries(assignment, roles))
        {
            return Native(catalog, user, userMode, AccessSources.NotManaged, managed: false);
        }

        var sources = new Dictionary<Guid, List<string>>();
        var missing = new List<Guid>();
        var recreated = new List<Guid>();
        var future = false;
        string start;

        if (userMode.Is(AccessRolesUserLibraryMode.All))
        {
            start = AccessSources.AllLibraries;
            future = true;
            GrantAll(AccessSources.AllLibraries);
        }
        else if (userMode.Is(AccessRolesUserLibraryMode.Only))
        {
            start = AccessSources.StartEmpty;
        }
        else if (roles.Where(r => !r.Libraries.Mode.Is(AccessRolesRoleLibraryMode.NotManaged)).ToList() is { Count: > 0 } libraryRoles)
        {
            start = AccessSources.StartRoles;
            foreach (var role in libraryRoles)
            {
                var source = AccessSources.Role(role.Name);
                var refs = role.Libraries.Refs.Select(r => r.Id).ToHashSet();
                missing.AddRange(refs.Where(id => !catalog.Contains(id)));
                switch (role.Libraries.Mode.Value)
                {
                    case AccessRolesRoleLibraryMode.All:
                        future = true;
                        GrantAll(source);
                        break;
                    case AccessRolesRoleLibraryMode.AllExcept:
                        // The listed ids stay left out whatever happened to them (deleted, created again): leaving out wins.
                        foreach (var id in catalog.Ids.Where(id => !refs.Contains(id)))
                        {
                            Grant(id, source);
                        }

                        break;
                    case AccessRolesRoleLibraryMode.Only:
                        // A reference marked orphaned, or a library created again since it was known, names a library that is
                        // new: it isn't granted until the admin grants it again.
                        foreach (var id in refs.Where(catalog.Contains))
                        {
                            if (Grants(document, catalog, role, id))
                            {
                                Grant(id, source);
                            }
                            else
                            {
                                recreated.Add(id);
                            }
                        }

                        break;
                }
            }
        }
        else
        {
            // Inherit, no library role, but overrides: the user's original native access (the fourth case), from the
            // ledger, or the native access the ledger would capture while there is no entry yet. Deleted libraries are
            // dropped; a library created again since (newer folder, same id) isn't granted from it.
            start = AccessSources.Original;
            var entry = document.Ledger.TryGetValue(user.Id, out var stored)
                ? stored
                : new LedgerEntry { OriginalEnableAllFolders = user.Native.EnableAllFolders, OriginalEnabledFolders = user.Native.EnabledFolders };
            if (entry.OriginalEnableAllFolders)
            {
                future = true;
                GrantAll(AccessSources.Original);
            }
            else
            {
                var original = OriginalLibraries(document, catalog, entry, out var again);
                foreach (var id in original)
                {
                    Grant(id, AccessSources.Original);
                }

                recreated.AddRange(again);
            }
        }

        foreach (var id in assignment.Libraries.Add.Distinct())
        {
            if (!catalog.Contains(id))
            {
                missing.Add(id);
            }
            else if (IsRecreated(document, catalog, id))
            {
                // Added for the library that was there before; the one created again under its id is new.
                recreated.Add(id);
            }
            else
            {
                Grant(id, AccessSources.UserAdd);
            }
        }

        var removed = assignment.Libraries.Remove.ToHashSet();
        missing.AddRange(removed.Where(id => !catalog.Contains(id)));
        var visibleIds = sources.Keys.Where(id => !removed.Contains(id)).ToHashSet();
        var enableAll = future && assignment.Libraries.Remove.Count == 0;
        var noLibraries = !enableAll && visibleIds.Count == 0;

        // Created again and granted by nothing else: hidden as Recreated, with a warning.
        var notGrantedAgain = catalog.Order(recreated.Where(id => !visibleIds.Contains(id) && !removed.Contains(id) && !enableAll));

        return new LibraryDecision
        {
            Managed = true,
            UserMode = userMode,
            StartingSet = start,
            IncludesFuture = future,
            EnableAllFolders = enableAll,
            EnabledFolders = enableAll ? [] : catalog.Order(visibleIds),
            Visible = [.. catalog.Libraries.Where(l => visibleIds.Contains(l.Id)).Select(l => new VisibleLibrary(l, sources[l.Id].Distinct().ToList()))],
            Hidden = [.. catalog.Libraries.Where(l => !visibleIds.Contains(l.Id)).Select(l => new HiddenLibrary(
                l,
                removed.Contains(l.Id) ? AccessSources.RemovedForUser : notGrantedAgain.Contains(l.Id) ? AccessSources.Recreated : AccessSources.NotGranted))],
            NoLibraries = noLibraries,
            NoLibrariesConfirmed = noLibraries && userMode.Is(AccessRolesUserLibraryMode.Only) && assignment.Libraries.Add.Count == 0 && assignment.Libraries.ConfirmNoLibraries,
            MissingReferences = [.. missing.Distinct()],
            Recreated = notGrantedAgain
        };

        void Grant(Guid id, string source)
        {
            if (!sources.TryGetValue(id, out var list))
            {
                sources[id] = list = [];
            }

            list.Add(source);
        }

        void GrantAll(string source)
        {
            foreach (var id in catalog.Ids)
            {
                Grant(id, source);
            }
        }
    }

    private static LibraryDecision Native(LibraryCatalog catalog, ServerUser user, LenientEnum<AccessRolesUserLibraryMode> userMode, string start, bool managed)
    {
        var visible = catalog.Expand(user.Native.EnableAllFolders, user.Native.EnabledFolders);
        return new LibraryDecision
        {
            Managed = managed,
            WriteBlocked = managed,
            UserMode = userMode,
            StartingSet = start,
            IncludesFuture = user.Native.EnableAllFolders,
            EnableAllFolders = user.Native.EnableAllFolders,
            EnabledFolders = user.Native.EnableAllFolders ? [] : [.. user.Native.EnabledFolders.Distinct()],
            Visible = [.. catalog.Libraries.Where(l => visible.Contains(l.Id)).Select(l => new VisibleLibrary(l, [AccessSources.Native]))],
            Hidden = [.. catalog.Libraries.Where(l => !visible.Contains(l.Id)).Select(l => new HiddenLibrary(l, AccessSources.NotGranted))]
        };
    }

    /// <summary>
    /// Gets whether a library the server lists was deleted and created again under the same id since Access Roles recorded
    /// it: its folder is newer than the created time <c>KnownLibraries</c> holds
    /// (the folder's creation time, never <c>DateCreated</c>), or the catalog marked it re-created
    /// (<see cref="KnownLibrary.RecreatedUtc"/>: an <c>Orphaned</c> library listed again). Such a library is new until the
    /// admin grants it again.
    /// </summary>
    /// <param name="document">The store document, for the known libraries.</param>
    /// <param name="catalog">The libraries now.</param>
    /// <param name="id">The library.</param>
    /// <returns><c>true</c> for a re-created library.</returns>
    public static bool IsRecreated(AccessRolesDocument document, LibraryCatalog catalog, Guid id)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Find(id) is not { } library || document.KnownLibraries.FirstOrDefault(k => k.Id.Equals(id)) is not { } known)
        {
            return false;
        }

        return known.RecreatedUtc is not null || (known.CreatedUtc is { } recorded && library.CreatedUtc is { } now && now > recorded + _createdTolerance);
    }

    private static List<string> Warnings(
        ServerUser user,
        AccessRolesUserState state,
        IReadOnlyList<AccessRole> roles,
        IReadOnlyList<Guid> missingRoles,
        IReadOnlyDictionary<AccessRolesLock, LockDecision> locks,
        LibraryDecision libraries,
        LibraryCatalog catalog,
        AccessRolesDocument document)
    {
        var warnings = new List<string>();
        if (user.IsAdministrator && roles.Any(r => r.KnownLocks.Count > 0) && state is AccessRolesUserState.Managed or AccessRolesUserState.CannotManage)
        {
            warnings.Add("Administrators are not restricted: the locks of this user's roles don't apply.");
        }

        if (user.IsAdministrator && libraries.Managed)
        {
            warnings.Add("Library roles apply to administrators too. To undo this for an admin, remove the role in Access Roles, set the user to Excluded, or disable the plugin.");
        }

        if (!user.IsAdministrator && user.Native.EnableLiveTvManagement && state != AccessRolesUserState.Excluded)
        {
            warnings.Add(locks[AccessRolesLock.AdminSurfaces].Locked
                ? "EnableLiveTvManagement is on: this user can schedule and delete recordings."
                : "EnableLiveTvManagement is on: this user can schedule and delete recordings, and without the AdminSurfaces lock can change Live TV server settings.");
        }

        switch (state)
        {
            case AccessRolesUserState.CannotManage:
                warnings.Add("This user's policy has a legacy BlockedMediaFolders list, which Jellyfin's API can't write: Access Roles can't manage their libraries. Clear it to manage them.");
                break;
            case AccessRolesUserState.UnknownValue:
                warnings.Add("A newer Access Roles build wrote values this one doesn't know: every settings section is locked and nothing is written to this user's policy until that version is installed.");
                break;
        }

        warnings.AddRange(missingRoles.Select(id => string.Format(CultureInfo.InvariantCulture, "Role {0} no longer exists and is ignored.", id.ToString("N", CultureInfo.InvariantCulture))));
        foreach (var id in libraries.MissingReferences)
        {
            var name = document.Roles.SelectMany(r => r.Libraries.Refs).FirstOrDefault(r => r.Id.Equals(id))?.NameAtSelection
                ?? document.KnownLibraries.FirstOrDefault(k => k.Id.Equals(id))?.Name;
            warnings.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Library {0} no longer exists; its references are kept until they are removed.",
                name is null ? id.ToString("N", CultureInfo.InvariantCulture) : "\"" + name + "\""));
        }

        warnings.AddRange(libraries.Recreated.Select(id => string.Format(
            CultureInfo.InvariantCulture,
            "Library \"{0}\" was deleted and created again since Access Roles recorded it; it is a new library, and the earlier grants of the old one (a role's Only… list, this user's Add list or their original access) don't give it until it is granted again.",
            catalog.Find(id)?.Name)));

        if (libraries.NoLibraries)
        {
            warnings.Add(libraries.NoLibrariesConfirmed
                ? "This user sees no libraries, as chosen (Only… with nothing selected)."
                : user.Name + " would have no libraries.");
        }

        return warnings;
    }
}
