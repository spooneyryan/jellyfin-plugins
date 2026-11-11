using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.Kit.Storage;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// Compares two states of the roles and assignments user by user (the preview), finds the users a
/// change takes out of library management and what their access becomes, the accidental empty results and
/// the lockout case, and keeps the ledger for a change that is saved.
/// </summary>
/// <remarks>
/// "Before" and "after" library access are what the library sync would establish under each state: the resolver's result
/// for a user whose libraries are managed (and not blocked), the native access otherwise, and for a user leaving
/// management the access their <c>onUnmanage</c> choice gives. <see cref="UserChange.PolicyWrite"/> compares the "after"
/// with the native access now: it says what the library sync would write.
/// </remarks>
internal static class ChangeAnalysis
{
    /// <summary>
    /// Analyses a change.
    /// </summary>
    /// <param name="before">The stored document.</param>
    /// <param name="after">The document as the change would leave it.</param>
    /// <param name="catalog">The libraries now.</param>
    /// <param name="users">The server's users.</param>
    /// <param name="choice">The request's <c>onUnmanage</c> choice, if any.</param>
    /// <returns>The report.</returns>
    public static ChangeReport Analyze(AccessRolesDocument before, AccessRolesDocument after, LibraryCatalog catalog, IReadOnlyList<ServerUser> users, AccessRolesUnmanageChoice? choice)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(users);

        var changes = users.Select(user => Analyze(before, after, catalog, user, choice)).ToList();
        var admins = changes.Where(c => c.User.IsAdministrator && !c.User.IsDisabled).ToList();
        var fullBefore = admins.Count(c => HasEveryLibrary(catalog, c.BeforeEnableAllFolders, c.BeforeEnabledFolders));
        var fullAfter = admins.Count(c => HasEveryLibrary(catalog, c.AfterEnableAllFolders, c.AfterEnabledFolders));
        var adminChanged = admins.Any(c => c.BeforeEnableAllFolders != c.AfterEnableAllFolders || !c.BeforeEnabledFolders.ToHashSet().SetEquals(c.AfterEnabledFolders));
        return new ChangeReport
        {
            Users = changes,
            NoFullAdministrator = admins.Count > 0 && fullAfter == 0 && (fullBefore > 0 || adminChanged)
        };
    }

    /// <summary>
    /// Keeps the ledger for a change that is saved: a user whose libraries become managed gets their
    /// original access recorded (captured now, before any write of the plugin), a user managed again loses a pending
    /// departure, and a user leaving management gets the access their choice gives recorded for the sync to write, with the
    /// native access it was computed against (its baseline).
    /// </summary>
    /// <remarks>
    /// <para>The entry stays while the user has an assignment, also when their libraries leave management (<c>Excluded</c>, a
    /// role switched to <c>NotManaged</c>, the last library role removed while another role stays): that is a pause, and a
    /// user managed again must keep their real original access, not the access the plugin wrote meanwhile. Leaving with
    /// <c>Keep</c>, or with an access that already matches, then only clears a pending release. The entry goes when the
    /// assignment goes (<c>Unmanage</c>, the last role removed) and nothing is left to write, and when the user is deleted
    /// (the store's user-data owner).</para>
    /// <para>The entry's <c>LastWrite</c> goes when the user's libraries leave management and again when they are managed
    /// again: while they aren't managed, their native access is the admin's (or a release's), so the
    /// plugin's last write says nothing about drift any more, and the first run after a pause writes the rules' result as
    /// a first write instead of taking the access found for drift. The original access and a pending release are kept as
    /// before.</para>
    /// </remarks>
    /// <param name="after">The document the change leaves.</param>
    /// <param name="report">The change's report.</param>
    /// <param name="now">The time, UTC.</param>
    /// <returns>The document with its ledger kept, and the users whose entry was captured, released or dropped.</returns>
    public static AccessRolesDocument KeepLedger(AccessRolesDocument after, ChangeReport report, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(report);

        var ledger = after.Ledger.ToDictionary(p => p.Key, p => p.Value);
        var changed = false;
        foreach (var change in report.Users)
        {
            var id = change.User.Id;
            var has = ledger.TryGetValue(id, out var entry);
            var assigned = change.After.Assignment is not null;
            if (change.After.Libraries.Managed)
            {
                if (!has)
                {
                    ledger[id] = Capture(change.User.Native, now);
                    changed = true;
                }
                else if (entry!.Release is not null || (!change.Before.Libraries.Managed && entry.LastWrite is not null))
                {
                    // Managed again: a pending departure goes, and so does the last write from before the pause.
                    ledger[id] = entry with { Release = null, LastWrite = change.Before.Libraries.Managed ? entry.LastWrite : null };
                    changed = true;
                }
            }
            else if (change.Leaving is { Choice: { } choice } leaving)
            {
                var native = change.User.Native;
                var noWrite = choice == AccessRolesUnmanageChoice.Keep || native.SameLibraries(leaving.EnableAllFolders, leaving.EnabledFolders);
                if (noWrite && assigned)
                {
                    // A pause: the original access stays recorded for when the user is managed again; the last write goes
                    // (the native access is the admin's from now on).
                    if (has && (entry!.Release is not null || entry.LastWrite is not null))
                    {
                        ledger[id] = entry with { Release = null, LastWrite = null };
                        changed = true;
                    }
                }
                else if (noWrite)
                {
                    changed |= ledger.Remove(id);
                }
                else
                {
                    var release = new LedgerRelease
                    {
                        Choice = choice,
                        RequestedUtc = now,
                        EnableAllFolders = leaving.EnableAllFolders,
                        EnabledFolders = leaving.EnableAllFolders ? [] : leaving.EnabledFolders,
                        Baseline = new LedgerAccess
                        {
                            EnableAllFolders = native.EnableAllFolders,
                            EnabledFolders = native.EnableAllFolders ? [] : [.. native.EnabledFolders.Distinct()]
                        }
                    };
                    ledger[id] = (has ? entry! : Capture(native, now)) with { Release = release, LastWrite = null };
                    changed = true;
                }
            }
            else if (has && !assigned && change.Before.Assignment is not null && entry!.Release is null)
            {
                // The assignment of a user whose libraries weren't managed went (an excluded user unmanaged, their lock-only
                // role removed): nothing is left to write, and the entry has no further use.
                changed |= ledger.Remove(id);
            }
        }

        return changed ? after with { Ledger = ledger } : after;
    }

    /// <summary>
    /// Gets whether an access opens every library there is now.
    /// </summary>
    /// <param name="catalog">The libraries.</param>
    /// <param name="enableAllFolders">Whether every library is granted.</param>
    /// <param name="enabledFolders">The granted libraries otherwise.</param>
    /// <returns><c>true</c> for every library.</returns>
    public static bool HasEveryLibrary(LibraryCatalog catalog, bool enableAllFolders, IReadOnlyList<Guid> enabledFolders)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(enabledFolders);
        return enableAllFolders || catalog.Ids.All(enabledFolders.Contains);
    }

    /// <summary>
    /// Gets whether two assignments say the same, apart from their <c>UpdatedUtc</c>.
    /// </summary>
    /// <param name="a">One assignment, or <c>null</c>.</param>
    /// <param name="b">The other, or <c>null</c>.</param>
    /// <returns><c>true</c> when they are equal.</returns>
    public static bool SameAssignment(RoleAssignment? a, RoleAssignment? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        // The lockout confirmation on record isn't part of what the assignment says.
        return ReferenceEquals(a, b)
            || string.Equals(
                JsonSerializer.Serialize(a with { UpdatedUtc = default, ConfirmedNoFullAdminUtc = null }, JsonStoreFormat.Options),
                JsonSerializer.Serialize(b with { UpdatedUtc = default, ConfirmedNoFullAdminUtc = null }, JsonStoreFormat.Options),
                StringComparison.Ordinal);
    }

    /// <summary>
    /// Records an admin's lockout confirmation (<c>LockoutGuard</c>): when a saved change leaves no enabled
    /// administrator with every library and the request confirmed it (<c>confirmNoFullAdmin=true</c>), every enabled
    /// administrator whose libraries the change manages without every library gets
    /// <see cref="RoleAssignment.ConfirmedNoFullAdminUtc"/>, so the library sync may write that narrower access.
    /// </summary>
    /// <param name="after">The document the change leaves.</param>
    /// <param name="report">The change's report.</param>
    /// <param name="confirmed">Whether the request confirmed it.</param>
    /// <param name="catalog">The libraries now.</param>
    /// <param name="now">The time, UTC.</param>
    /// <returns>The document with the confirmations recorded.</returns>
    public static AccessRolesDocument KeepConfirmations(AccessRolesDocument after, ChangeReport report, bool confirmed, LibraryCatalog catalog, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(catalog);
        if (!confirmed || !report.NoFullAdministrator)
        {
            return after;
        }

        var narrowed = report.Users
            .Where(c => c.User.IsAdministrator && !c.User.IsDisabled && c.After.Libraries.Writes && !HasEveryLibrary(catalog, c.AfterEnableAllFolders, c.AfterEnabledFolders))
            .Select(c => c.User.Id)
            .ToHashSet();
        return narrowed.Count == 0 || !after.Assignments.Any(a => narrowed.Contains(a.UserId))
            ? after
            : after with { Assignments = [.. after.Assignments.Select(a => narrowed.Contains(a.UserId) ? a with { ConfirmedNoFullAdminUtc = now } : a)] };
    }

    private static UserChange Analyze(AccessRolesDocument before, AccessRolesDocument after, LibraryCatalog catalog, ServerUser user, AccessRolesUnmanageChoice? choice)
    {
        var b = EffectiveRulesResolver.Resolve(before, catalog, user);
        var a = EffectiveRulesResolver.Resolve(after, catalog, user);
        var native = user.Native;
        var leaving = b.Libraries.Managed && !a.Libraries.Managed ? Leave(before, catalog, user, choice) : null;

        var (beforeAll, beforeFolders) = b.Libraries.Writes ? (b.Libraries.EnableAllFolders, b.Libraries.EnabledFolders) : (native.EnableAllFolders, native.EnabledFolders);
        var (afterAll, afterFolders) = a.Libraries.Writes
            ? (a.Libraries.EnableAllFolders, a.Libraries.EnabledFolders)
            : leaving is { Choice: not null, NoLedgerEntry: false } ? (leaving.EnableAllFolders, leaving.EnabledFolders) : (native.EnableAllFolders, native.EnabledFolders);
        var visibleBefore = catalog.Expand(beforeAll, beforeFolders);
        var visibleAfter = catalog.Expand(afterAll, afterFolders);

        var lockedBefore = b.Locked;
        var lockedAfter = a.Locked;
        return new UserChange
        {
            User = user,
            Before = b,
            After = a,
            LocksAdded = [.. AccessPresets.AllLocks.Where(l => lockedAfter.Contains(l) && !lockedBefore.Contains(l))],
            LocksRemoved = [.. AccessPresets.AllLocks.Where(l => lockedBefore.Contains(l) && !lockedAfter.Contains(l))],
            LibrariesAdded = [.. catalog.Libraries.Where(l => visibleAfter.Contains(l.Id) && !visibleBefore.Contains(l.Id))],
            LibrariesRemoved = [.. catalog.Libraries.Where(l => visibleBefore.Contains(l.Id) && !visibleAfter.Contains(l.Id))],
            BeforeEnableAllFolders = beforeAll,
            BeforeEnabledFolders = beforeAll ? [] : beforeFolders,
            AfterEnableAllFolders = afterAll,
            AfterEnabledFolders = afterAll ? [] : afterFolders,
            PolicyWrite = !native.SameLibraries(afterAll, afterFolders),
            Leaving = leaving,
            NoLibrariesRefused = a.Libraries.NoLibraries && !a.Libraries.NoLibrariesConfirmed && !(b.Libraries.NoLibraries && !b.Libraries.NoLibrariesConfirmed),
            AssignmentChanged = !SameAssignment(b.Assignment, a.Assignment)
        };
    }

    private static LeavingResult Leave(AccessRolesDocument before, LibraryCatalog catalog, ServerUser user, AccessRolesUnmanageChoice? choice)
    {
        var native = user.Native;
        switch (choice)
        {
            case AccessRolesUnmanageChoice.Restore:
                if (!before.Ledger.TryGetValue(user.Id, out var entry))
                {
                    return new LeavingResult { Choice = choice, NoLedgerEntry = true, EnableAllFolders = native.EnableAllFolders, EnabledFolders = native.EnabledFolders };
                }

                // The original access, without deleted libraries and without one created again since; none
                // for a user created under the default roles (LedgerEntry.DefaultRolesUtc).
                return new LeavingResult
                {
                    Choice = choice,
                    EnableAllFolders = entry.OriginalEnableAllFolders,
                    EnabledFolders = entry.OriginalEnableAllFolders ? [] : EffectiveRulesResolver.OriginalLibraries(before, catalog, entry, out _),
                    CreatedUnderDefaultRoles = entry.DefaultRolesUtc is not null
                };
            case AccessRolesUnmanageChoice.Keep:
                return new LeavingResult
                {
                    Choice = choice,
                    EnableAllFolders = native.EnableAllFolders,
                    EnabledFolders = native.EnableAllFolders ? [] : native.EnabledFolders,
                    FrozenAt = [.. catalog.Libraries.Where(l => catalog.Expand(native.EnableAllFolders, native.EnabledFolders).Contains(l.Id)).Select(l => l.Name)]
                };
            case AccessRolesUnmanageChoice.GrantAll:
                return new LeavingResult { Choice = choice, EnableAllFolders = true };
            default:
                return new LeavingResult { Choice = null, EnableAllFolders = native.EnableAllFolders, EnabledFolders = native.EnabledFolders };
        }
    }

    private static LedgerEntry Capture(NativeAccess native, DateTime now) => new()
    {
        OriginalEnableAllFolders = native.EnableAllFolders,
        OriginalEnabledFolders = native.EnableAllFolders ? [] : [.. native.EnabledFolders.Distinct()],
        OriginalEnableUserPreferenceAccess = native.EnableUserPreferenceAccess,
        CapturedUtc = now
    };
}
