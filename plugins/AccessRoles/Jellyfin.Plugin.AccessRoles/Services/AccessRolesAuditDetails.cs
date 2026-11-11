using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// The <c>Details</c> of the audit lines the roles API writes: plain values, ids in Jellyfin's <c>N</c> form,
/// library names next to their ids (<c>LibraryNames</c>).
/// </summary>
internal static class AccessRolesAuditDetails
{
    /// <summary>
    /// Builds the lines of a saved change: the proposal's own (a role created, changed or deleted, the default roles), then
    /// one <c>AssignmentChanged</c> per user whose assignment changed or who left library management, and a
    /// <c>LibraryAccessFrozen</c> per user who left it with <c>Keep</c>.
    /// </summary>
    /// <param name="own">The proposal's own lines.</param>
    /// <param name="report">The change's report.</param>
    /// <param name="reason">Why the assignments changed: <c>Edit</c>, <c>Bulk</c>, <c>Unmanage</c>, <c>RoleDeleted</c>.</param>
    /// <param name="actor">Who did it.</param>
    /// <param name="catalog">The libraries now, for names.</param>
    /// <returns>The lines.</returns>
    public static IReadOnlyList<AuditRecord> Lines(IEnumerable<AuditRecord> own, ChangeReport report, string reason, AuditActor actor, LibraryCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(catalog);
        var lines = own.ToList();
        foreach (var change in report.Users.Where(c => c.AssignmentChanged || c.Leaving is not null))
        {
            var details = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Reason"] = reason,
                ["StateBefore"] = change.Before.State.ToString(),
                ["StateAfter"] = change.After.State.ToString(),
                ["Before"] = Assignment(change.Before.Assignment),
                ["After"] = Assignment(change.After.Assignment)
            };
            AddEffect(details, change, catalog);
            if (change.Leaving is { Choice: { } choice } leaving)
            {
                details["LeavesManagement"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Choice"] = choice.ToString(),
                    ["EnableAllFolders"] = leaving.EnableAllFolders,
                    ["EnabledFolders"] = Ids(leaving.EnabledFolders),
                    ["LibraryNames"] = Names(leaving.EnabledFolders, catalog),
                    ["WritePending"] = choice != AccessRolesUnmanageChoice.Keep && !change.User.Native.SameLibraries(leaving.EnableAllFolders, leaving.EnabledFolders),
                    ["CreatedUnderDefaultRoles"] = leaving.CreatedUnderDefaultRoles
                };
            }

            lines.Add(new AuditRecord(actor, AuditActions.AssignmentChanged, change.User.Id, null, change.User.Name, details));
            if (change.Leaving is { Choice: AccessRolesUnmanageChoice.Keep } kept)
            {
                lines.Add(new AuditRecord(actor, AuditActions.LibraryAccessFrozen, change.User.Id, null, change.User.Name, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["EnableAllFolders"] = kept.EnableAllFolders,
                    ["EnabledFolders"] = Ids(kept.EnabledFolders),
                    ["LibraryNames"] = Names(kept.EnabledFolders, catalog),
                    ["FrozenAt"] = kept.FrozenAt
                }));
            }
        }

        return lines;
    }

    /// <summary>
    /// Describes a role.
    /// </summary>
    /// <param name="role">The role.</param>
    /// <returns>Its name, description, locks and library grant.</returns>
    public static Dictionary<string, object?> Role(AccessRole role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Name"] = role.Name,
            ["Description"] = role.Description,
            ["Locks"] = role.Locks.Select(l => l.Name).ToList(),
            ["Libraries"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Mode"] = role.Libraries.Mode.Name,
                ["Refs"] = Ids(role.Libraries.Refs.Select(r => r.Id)),
                ["LibraryNames"] = role.Libraries.Refs.ToDictionary(r => Id(r.Id), r => (object?)r.NameAtSelection, StringComparer.Ordinal)
            }
        };
    }

    /// <summary>
    /// Lists what a change does to users whose assignment it doesn't change (the members of a changed role).
    /// </summary>
    /// <param name="report">The change's report.</param>
    /// <param name="catalog">The libraries now, for names.</param>
    /// <returns>One entry per such user.</returns>
    public static List<Dictionary<string, object?>> Affected(ChangeReport report, LibraryCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(report);
        return [.. report.Affected.Where(c => !c.AssignmentChanged && c.Leaving is null).Select(c =>
        {
            var entry = new Dictionary<string, object?>(StringComparer.Ordinal) { ["UserId"] = Id(c.User.Id), ["Name"] = c.User.Name };
            AddEffect(entry, c, catalog);
            return entry;
        })];
    }

    /// <summary>
    /// Formats an id as Jellyfin writes it.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The <c>N</c> form.</returns>
    public static string Id(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats ids as Jellyfin writes them.
    /// </summary>
    /// <param name="ids">The ids.</param>
    /// <returns>The <c>N</c> forms.</returns>
    public static List<string> Ids(IEnumerable<Guid> ids) => [.. (ids ?? []).Select(Id)];

    private static void AddEffect(Dictionary<string, object?> details, UserChange change, LibraryCatalog catalog)
    {
        details["LocksAdded"] = change.LocksAdded.Select(l => l.ToString()).ToList();
        details["LocksRemoved"] = change.LocksRemoved.Select(l => l.ToString()).ToList();
        details["LibrariesAdded"] = Names(change.LibrariesAdded.Select(l => l.Id), catalog);
        details["LibrariesRemoved"] = Names(change.LibrariesRemoved.Select(l => l.Id), catalog);
        details["PolicyWritePending"] = change.PolicyWrite;
    }

    private static Dictionary<string, object?>? Assignment(RoleAssignment? assignment)
        => assignment is null
            ? null
            : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["RoleIds"] = Ids(assignment.RoleIds),
                ["LockOverrides"] = assignment.LockOverrides.ToDictionary(o => o.Key.Name, o => (object?)o.Value.Name, StringComparer.Ordinal),
                ["Libraries"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Mode"] = assignment.Libraries.Mode.Name,
                    ["Add"] = Ids(assignment.Libraries.Add),
                    ["Remove"] = Ids(assignment.Libraries.Remove),
                    ["ConfirmNoLibraries"] = assignment.Libraries.ConfirmNoLibraries
                },
                ["State"] = assignment.State.Name
            };

    private static Dictionary<string, object?> Names(IEnumerable<Guid> ids, LibraryCatalog catalog)
        => ids.Distinct().ToDictionary(Id, id => (object?)catalog.Find(id)?.Name, StringComparer.Ordinal);
}
