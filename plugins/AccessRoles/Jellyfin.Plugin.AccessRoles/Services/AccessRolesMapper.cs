using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// Maps the store's records and the resolver's results to the API's DTOs (records never cross the API boundary).
/// </summary>
internal static class AccessRolesMapper
{
    /// <summary>The <i>Dashboard</i> row for non-admin members of a role with <c>AdminSurfaces</c>.</summary>
    public const string DashboardBlocked = "The Dashboard is always blocked by Jellyfin. This role also blocks the admin data and server calls non-admins can still reach.";

    /// <summary>The <i>Dashboard</i> row for non-admin members of a role without <c>AdminSurfaces</c>.</summary>
    public const string DashboardOpen = "The Dashboard is always blocked by Jellyfin. This role doesn't block the admin data and server calls non-admins can still reach.";

    /// <summary>
    /// What the <i>Dashboard</i> row adds for a role with <c>AdminSurfaces</c> while the server doesn't enforce it
    /// (before 0.9.7, which enforces it).
    /// </summary>
    public const string NotEnforcedYet = " Not enforced yet: this build stores the lock, and the server doesn't apply it.";

    /// <summary>
    /// The preview's note for <c>Restore</c> of a user created under the default roles: their original
    /// access is none.
    /// </summary>
    public const string RestoresToNoLibraries = "Restores to no libraries: created under default roles.";

    /// <summary>The <i>Dashboard</i> row for administrators (administrators are never restricted).</summary>
    public const string DashboardAdministrators = "Administrators are not restricted.";

    /// <summary>
    /// Maps a role.
    /// </summary>
    /// <param name="role">The role.</param>
    /// <param name="document">The document, for members and default roles.</param>
    /// <param name="catalog">The libraries now.</param>
    /// <param name="users">The server's users, for the administrators among the members.</param>
    /// <returns>The DTO.</returns>
    public static AccessRolesRoleDto Role(AccessRole role, AccessRolesDocument document, LibraryCatalog catalog, IReadOnlyList<ServerUser> users)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(users);

        var members = document.Assignments.Where(a => a.RoleIds.Contains(role.Id)).ToList();
        var known = role.KnownLocks;
        var unknown = role.Locks.Where(l => !l.IsKnown).Select(l => "Locks=" + l.Name).ToList();
        if (role.HasUnknownLibraryMode)
        {
            unknown.Add("Libraries.Mode=" + role.Libraries.Mode.Name);
        }

        var blocks = known.Contains(AccessRolesLock.AdminSurfaces);
        var enforced = SettingsSections.IsEnforced(AccessRolesLock.AdminSurfaces);
        return new AccessRolesRoleDto
        {
            Id = role.Id,
            Name = role.Name,
            Description = role.Description,
            Locks = [.. AccessPresets.AllLocks.Where(known.Contains)],
            Libraries = new AccessRolesRoleLibrariesDto { Mode = role.Libraries.Mode.Value, Refs = [.. role.Libraries.Refs.Select(r => Ref(r, catalog))] },
            Dashboard = new AccessRolesDashboardRowDto
            {
                BlocksAdminSurfaces = blocks,
                Enforced = enforced,
                NonAdministrators = blocks ? DashboardBlocked + (enforced ? string.Empty : NotEnforcedYet) : DashboardOpen,
                Administrators = DashboardAdministrators,
                AdministratorMembers = [.. members.Select(m => users.FirstOrDefault(u => u.Id.Equals(m.UserId))).Where(u => u is { IsAdministrator: true }).Select(u => u!.Name).Order(StringComparer.OrdinalIgnoreCase)]
            },
            MemberCount = members.Count,
            IsDefault = document.DefaultRoleIds.Contains(role.Id),
            UnknownValues = unknown,
            CreatedUtc = role.CreatedUtc,
            UpdatedUtc = role.UpdatedUtc,
            MembersToken = AccessRolesTokens.Members(document, role.Id)
        };
    }

    /// <summary>
    /// Maps a library reference.
    /// </summary>
    /// <param name="reference">The reference.</param>
    /// <param name="catalog">The libraries now.</param>
    /// <returns>The DTO.</returns>
    public static AccessRolesLibraryRefDto Ref(LibraryReference reference, LibraryCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(catalog);
        var library = catalog.Find(reference.Id);
        return new AccessRolesLibraryRefDto
        {
            Id = reference.Id,
            Name = library?.Name ?? reference.NameAtSelection,
            NameAtSelection = reference.NameAtSelection,
            Locations = reference.Locations,
            Orphaned = reference.Orphaned || library is null,
            Enabled = library?.Enabled
        };
    }

    /// <summary>
    /// Maps a user's effective access (<c>AccessRolesEffectiveDto</c>).
    /// </summary>
    /// <param name="effective">The resolver's result.</param>
    /// <returns>The DTO.</returns>
    public static AccessRolesEffectiveDto Effective(EffectiveAccess effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        var user = effective.User;
        var libraries = effective.Libraries;
        return new AccessRolesEffectiveDto
        {
            UserId = user.Id,
            UserName = user.Name,
            IsAdministrator = user.IsAdministrator,
            State = effective.State,
            Roles = [.. effective.Roles.Select(r => new AccessRolesRoleRefDto { Id = r.Id, Name = r.Name })],
            Locks = effective.Locks.ToDictionary(l => l.Key.ToString(), l => new AccessRolesLockStateDto { Locked = l.Value.Locked, Source = l.Value.Source, Enforced = SettingsSections.IsEnforced(l.Key) }, StringComparer.Ordinal),
            Libraries = new AccessRolesEffectiveLibrariesDto
            {
                UserMode = libraries.UserMode.Value,
                StartingSet = libraries.StartingSet,
                Managed = libraries.Managed,
                EnableAllFolders = libraries.EnableAllFolders,
                NoLibraries = libraries.NoLibraries,
                NoLibrariesConfirmed = libraries.NoLibrariesConfirmed,
                Visible = [.. libraries.Visible.Select(v => new AccessRolesEffectiveLibraryDto { Id = v.Library.Id, Name = v.Library.Name, Enabled = v.Library.Enabled, Sources = v.Sources })],
                Hidden = [.. libraries.Hidden.Select(h => new AccessRolesEffectiveLibraryDto { Id = h.Library.Id, Name = h.Library.Name, Enabled = h.Library.Enabled, Reason = h.Reason })],
                Native = Native(user.Native.EnableAllFolders, user.Native.EnabledFolders),
                PendingWrite = PendingWrite(effective),
                PendingRelease = effective.PendingRelease is { } release
                    ? new AccessRolesPendingReleaseDto
                    {
                        Choice = release.Choice.Value,
                        RequestedUtc = release.RequestedUtc,
                        EnableAllFolders = release.EnableAllFolders,
                        EnabledFolders = release.EnableAllFolders ? [] : release.EnabledFolders,
                        Baseline = release.Baseline is { } baseline ? Native(baseline.EnableAllFolders, baseline.EnabledFolders) : null,
                        BaselineMatches = effective.PendingReleaseBaselineMatches
                    }
                    : null,
                MissingReferences = libraries.MissingReferences,
                Drift = effective.Drift,
                LastWrite = effective.LastWrite is { } last ? Native(last.EnableAllFolders, last.EnabledFolders) : null,
                LastWriteUtc = effective.LastWrite?.Utc
            },
            NativeFlags = new AccessRolesNativeFlagsDto
            {
                EnableUserPreferenceAccess = user.Native.EnableUserPreferenceAccess,
                EnableLiveTvManagement = user.Native.EnableLiveTvManagement,
                IsHidden = user.IsHidden,
                IsDisabled = user.IsDisabled,
                BlockedMediaFolders = user.Native.BlockedMediaFolders
            },
            Warnings = effective.Warnings,
            UnknownValues = effective.UnknownValues,
            UpdatedUtc = effective.Assignment?.UpdatedUtc,
            Assignment = effective.Assignment is { } assignment ? Assignment(assignment) : null
        };
    }

    /// <summary>
    /// Maps a stored assignment for the per-user panel: only the values this build knows; a <c>PUT</c> that
    /// leaves the others out keeps them (<see cref="AccessRolesRequests"/>).
    /// </summary>
    /// <param name="assignment">The assignment.</param>
    /// <returns>The DTO.</returns>
    public static AccessRolesAssignmentDto Assignment(RoleAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return new AccessRolesAssignmentDto
        {
            RoleIds = assignment.RoleIds,
            LockOverrides = AccessPresets.AllLocks
                .Where(l => assignment.LockOverrides.TryGetValue(l, out var choice) && choice.IsKnown && !choice.Is(AccessRolesLockOverride.Inherit))
                .ToDictionary(l => l.ToString(), l => assignment.LockOverrides[l].Value!.Value, StringComparer.Ordinal),
            Libraries = new AccessRolesAssignmentLibrariesDto
            {
                Mode = assignment.Libraries.Mode.Value,
                Add = assignment.Libraries.Add,
                Remove = assignment.Libraries.Remove,
                ConfirmNoLibraries = assignment.Libraries.ConfirmNoLibraries
            },
            State = assignment.State.Value
        };
    }

    /// <summary>
    /// Maps a row of the Users tab.
    /// </summary>
    /// <param name="effective">The user's effective access.</param>
    /// <returns>The DTO.</returns>
    public static AccessRolesUserDto User(EffectiveAccess effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        var user = effective.User;
        return new AccessRolesUserDto
        {
            UserId = user.Id,
            Name = user.Name,
            IsAdministrator = user.IsAdministrator,
            IsDisabled = user.IsDisabled,
            IsHidden = user.IsHidden,
            RoleIds = effective.Assignment?.RoleIds ?? [],
            State = effective.State,
            LibrariesManaged = effective.Libraries.Managed,
            LockedCount = effective.Locked.Count,
            NoLibraries = effective.Libraries.NoLibraries,
            NoLibrariesConfirmed = effective.Libraries.NoLibrariesConfirmed,
            PendingWrite = PendingWrite(effective) is not null,
            Drift = effective.Drift,
            Summary = Summary(effective),
            UpdatedUtc = effective.Assignment?.UpdatedUtc
        };
    }

    /// <summary>
    /// Maps a library sync run's report (<c>POST Sync</c>).
    /// </summary>
    /// <param name="report">The report.</param>
    /// <param name="driftPolicy">The drift policy in effect.</param>
    /// <param name="catalog">The libraries now, for names.</param>
    /// <returns>The DTO.</returns>
    public static AccessRolesSyncDto Sync(SyncReport report, string driftPolicy, LibraryCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(catalog);
        return new AccessRolesSyncDto
        {
            DryRun = report.DryRun,
            Paused = report.Paused,
            Trigger = report.Trigger.ToString(),
            StartedUtc = report.StartedUtc,
            DurationMs = (long)report.Duration.TotalMilliseconds,
            DriftPolicy = driftPolicy,
            ManagedUsers = report.ManagedUsers,
            Written = report.Written,
            Skipped = report.Skipped,
            Users = [.. report.Users.OrderBy(u => u.User.Name, StringComparer.OrdinalIgnoreCase).ThenBy(u => u.User.Id).Select(u => SyncUser(u, catalog))],
            PlanToken = SyncPlanToken.Of(report.Users, report.Scope),
            UserId = report.Scope,
            Libraries = [.. report.Libraries.Select(e => new AccessRolesSyncLibraryDto
            {
                Change = e.Kind.ToString(),
                Id = e.Id,
                Name = e.Name,
                OldId = e.OldId,
                OldName = e.OldName,
                SeenCall = e.Exact
            })],
            WaitingLibraries = [.. report.WaitingLibraries],
            WithheldLibraries = [.. report.WithheldLibraries],
            UsersBeingCreated = [.. report.BeingCreated]
        };
    }

    /// <summary>
    /// Maps a change's report (<c>Preview</c>).
    /// </summary>
    /// <param name="report">The report.</param>
    /// <returns>The DTO.</returns>
    public static AccessRolesPreviewDto Preview(ChangeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new AccessRolesPreviewDto
        {
            Users = [.. report.Affected.Select(PreviewUser)],
            UnmanageChoiceRequired = [.. report.ChoiceRequired.Select(c => UserRef(c.User))],
            NoLibraries = [.. report.NoLibraries.Select(c => UserRef(c.User))],
            NoLedgerEntry = [.. report.NoLedgerEntry.Select(c => UserRef(c.User))],
            NoFullAdministrator = report.NoFullAdministrator,
            PreviewToken = AccessRolesTokens.Preview(report)
        };
    }

    /// <summary>
    /// Maps a user to its reference.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The DTO.</returns>
    public static AccessRolesUserRefDto UserRef(ServerUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new AccessRolesUserRefDto { UserId = user.Id, Name = user.Name };
    }

    /// <summary>
    /// Maps an audit line.
    /// </summary>
    /// <param name="entry">The line.</param>
    /// <returns>The DTO.</returns>
    public static AccessRolesAuditEntryDto Audit(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new AccessRolesAuditEntryDto
        {
            V = entry.V,
            Seq = entry.Seq,
            Utc = entry.Utc,
            Actor = new AccessRolesAuditActorDto { Kind = entry.Actor.Kind, UserId = entry.Actor.UserId, Name = entry.Actor.Name },
            Action = entry.Action,
            TargetUserId = entry.TargetUserId,
            TargetRoleId = entry.TargetRoleId,
            TargetName = entry.TargetName,
            Details = entry.Details
        };
    }

    /// <summary>
    /// Maps native library fields.
    /// </summary>
    /// <param name="enableAllFolders">Whether every library is granted.</param>
    /// <param name="enabledFolders">The granted libraries otherwise.</param>
    /// <returns>The DTO.</returns>
    public static AccessRolesNativeLibrariesDto Native(bool enableAllFolders, IReadOnlyList<Guid> enabledFolders)
        => new() { EnableAllFolders = enableAllFolders, EnabledFolders = enableAllFolders ? [] : enabledFolders };

    private static AccessRolesSyncUserDto SyncUser(SyncUserResult result, LibraryCatalog catalog)
    {
        // A drifted user under ReportOnly gets only the changes in the safe direction: those are the target.
        var drift = result.Kind == SyncActionKind.Drift ? result.DriftDecision : null;
        var target = drift is null ? result.Target : drift.Write;
        var before = catalog.Expand(result.Native.EnableAllFolders, result.Native.EnabledFolders);
        var after = target is { } fields ? catalog.Expand(fields.EnableAllFolders, fields.EnabledFolders) : before;
        return new AccessRolesSyncUserDto
        {
            UserId = result.User.Id,
            Name = result.User.Name,
            IsAdministrator = result.User.IsAdministrator,
            Action = result.Kind.ToString(),
            Reason = result.Reason,
            Native = Native(result.Native.EnableAllFolders, result.Native.EnabledFolders),
            Target = target is { } written ? Native(written.EnableAllFolders, written.EnabledFolders) : null,
            Intended = drift is not null && result.Target is { } intended ? Native(intended.EnableAllFolders, intended.EnabledFolders) : null,
            Kept = drift is null ? [] : LibraryNames(drift.KeptAdded, catalog),
            NotApplied = drift is null ? [] : LibraryNames(drift.NotApplied, catalog),
            Added = [.. catalog.Libraries.Where(l => after.Contains(l.Id) && !before.Contains(l.Id)).Select(l => new AccessRolesLibraryNameDto { Id = l.Id, Name = l.Name })],
            Removed = [.. catalog.Libraries.Where(l => before.Contains(l.Id) && !after.Contains(l.Id)).Select(l => new AccessRolesLibraryNameDto { Id = l.Id, Name = l.Name })],
            Outcome = result.Outcome?.ToString(),
            ChangedFields = result.Differences,
            CommittedFields = result.Write?.CommittedFields ?? [],
            SaveFailed = result.Write is { } write && (write.SaveFailed || write.RetriedAfterFailedSave)
        };
    }

    private static IReadOnlyList<AccessRolesLibraryNameDto> LibraryNames(IReadOnlyList<Guid> ids, LibraryCatalog catalog)
        => [.. catalog.Libraries.Where(l => ids.Contains(l.Id)).Select(l => new AccessRolesLibraryNameDto { Id = l.Id, Name = l.Name })];

    private static AccessRolesPreviewUserDto PreviewUser(UserChange change) => new()
    {
        UserId = change.User.Id,
        Name = change.User.Name,
        IsAdministrator = change.User.IsAdministrator,
        StateBefore = change.Before.State,
        StateAfter = change.After.State,
        LocksAdded = change.LocksAdded,
        LocksRemoved = change.LocksRemoved,
        LibrariesAdded = [.. change.LibrariesAdded.Select(l => new AccessRolesLibraryNameDto { Id = l.Id, Name = l.Name })],
        LibrariesRemoved = [.. change.LibrariesRemoved.Select(l => new AccessRolesLibraryNameDto { Id = l.Id, Name = l.Name })],
        PolicyWrite = change.PolicyWrite,
        NoLibraries = change.After.Libraries.NoLibraries,
        NoLibrariesRefused = change.NoLibrariesRefused,
        LeavesManagement = change.Leaving is { } leaving
            ? new AccessRolesLeavingDto
            {
                Choice = leaving.Choice,
                FrozenAt = leaving.FrozenAt,
                After = leaving.Choice is null || leaving.NoLedgerEntry ? null : Native(leaving.EnableAllFolders, leaving.EnabledFolders),
                CreatedUnderDefaultRoles = leaving.CreatedUnderDefaultRoles,
                Note = leaving.CreatedUnderDefaultRoles ? RestoresToNoLibraries : null
            }
            : null,
        Warnings = change.After.Warnings
    };

    // What the library sync will write: a managed user's result, or a pending release while its baseline still matches
    // the native access (LedgerRelease's contract); null when the native fields already are that, or nothing is to be written.
    private static AccessRolesPendingWriteDto? PendingWrite(EffectiveAccess effective)
    {
        var libraries = effective.Libraries;
        var (write, enableAll, folders) = libraries.Writes
            ? (true, libraries.EnableAllFolders, libraries.EnabledFolders)
            : effective.PendingRelease is { } release && effective.PendingReleaseBaselineMatches
                ? (true, release.EnableAllFolders, release.EnableAllFolders ? [] : release.EnabledFolders)
                : (false, false, (IReadOnlyList<Guid>)[]);
        var native = effective.User.Native;
        if (!write || native.SameLibraries(enableAll, folders))
        {
            return null;
        }

        var intended = folders.ToHashSet();
        var current = native.EnableAllFolders ? [] : native.EnabledFolders.ToHashSet();
        return new AccessRolesPendingWriteDto
        {
            EnableAllFolders = enableAll,
            Add = enableAll ? [] : [.. folders.Where(id => !current.Contains(id)).Distinct()],
            Remove = [.. native.EnabledFolders.Where(id => enableAll || !intended.Contains(id)).Distinct()]
        };
    }

    private static string Summary(EffectiveAccess effective)
    {
        var roles = effective.Roles.Count == 0 ? "no role" : (effective.Roles.Count == 1 ? "role " : "roles ") + string.Join(", ", effective.Roles.Select(r => r.Name));
        var libraries = effective.Libraries;
        var access = !libraries.Managed
            ? "libraries not managed"
            : libraries.EnableAllFolders
                ? "every library"
                : libraries.NoLibraries
                    ? "no library"
                    : "libraries: " + string.Join(", ", libraries.Visible.Select(v => v.Library.Name));
        var release = effective.PendingRelease is { } pending && effective.PendingReleaseBaselineMatches
            ? "; " + pending.Choice.Name + " waiting for the library sync"
            : string.Empty;
        return effective.State switch
        {
            AccessRolesUserState.NotManaged => "Not managed" + release,
            AccessRolesUserState.Excluded => "Excluded (" + roles + " kept)" + release,
            _ when effective.User.IsAdministrator => string.Format(CultureInfo.InvariantCulture, "{0}; administrator, so no lock applies; {1}", Capitalize(roles), access),
            _ => string.Format(CultureInfo.InvariantCulture, "{0}; {1} locked; {2}", Capitalize(roles), effective.Locked.Count, access)
        };
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
