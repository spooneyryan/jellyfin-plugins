using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.Kit.Storage;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// How an export's document combines with the stored one (the Data tab's import): the configuration an admin chose
/// (roles, assignments with their overrides, default roles) comes from the export, Merge by id and newest copy or Replace
/// as a whole; this server's bookkeeping stays this server's. Pure: it runs for the dry run on the stored document and
/// again inside the store's writer for the save, so the save does what its dry run showed (the import's
/// <c>PreviewToken</c>). A restore of a snapshot is the same Replace, from the snapshot's document (a restore
/// replaces roles and assignments as a whole).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Users</b> are the kit's <see cref="ImportUserMap"/>: by id or by name, never a user deleted on this server or
/// the exporting one by name; the records of a user it can't resolve are left out and reported, so nothing is written
/// for them.</item>
/// <item><b>Libraries</b> (mapped by id, then by <c>Locations</c>, then by name): an id this server lists or
/// remembers (an orphaned one too) is that library; otherwise the export's known library (or the reference's own name and
/// folders) is looked for among the libraries listed here, by the same folders, then by the same name, ignoring case, and
/// only one library may match, and only for one export id. A reference that matches nothing is left out and reported,
/// never guessed. A reference the export had orphaned, or to a library this server has orphaned, stays orphaned, and this
/// server's known libraries are kept, so a library deleted and created again here is still not granted until an admin
/// grants it again (new libraries never reach <c>Only</c> lists).</item>
/// <item><b>Bookkeeping</b>: the known libraries, the renames waiting, the mirror's records (only the mirror writes those:
/// a record names a flag it turned off here) and the document's unknown fields are this server's in both modes. The
/// ledger's entries too, except that an export or snapshot <i>of this server</i> fills in the original access of a user
/// it manages who has no entry here (the data lost, then imported or restored: the native access is then Access Roles'
/// last write, not the original), its library ids mapped, never its last write or a pending release. An assignment's
/// lockout confirmation (<see cref="RoleAssignment.ConfirmedNoFullAdminUtc"/>) is this server's admin's, so the stored one is
/// kept and the data's never taken.</item>
/// </list>
/// </remarks>
internal static class AccessRolesDataImport
{
    /// <summary>A role skipped because another role here has its name.</summary>
    public const string NameTaken = "NameTaken";

    /// <summary>A reference left out because no library here matches.</summary>
    public const string NoSuchLibrary = "NoSuchLibrary";

    /// <summary>A reference left out because more than one library here, or more than one export id, matches.</summary>
    public const string Ambiguous = "Ambiguous";

    /// <summary>A role id an assignment or the default roles name that isn't in the result.</summary>
    public const string RoleNotImported = "RoleNotImported";

    /// <summary>An imported role whose name the roles API refuses: empty, over 100 characters, or with control characters.</summary>
    public const string InvalidName = "InvalidName";

    /// <summary>An imported role whose description the roles API refuses: over 500 characters, or with control characters.</summary>
    public const string InvalidDescription = "InvalidDescription";

    /// <summary>
    /// An imported role whose library references its mode doesn't take: <c>NotManaged</c> or <c>All</c> with references,
    /// <c>AllExcept</c> without (an <c>Only</c> role without any is <c>EmptyOnlyRole</c>, the roles API's code).
    /// </summary>
    public const string InvalidLibraries = "InvalidLibraries";

    /// <summary>
    /// A role left out because libraries it names aren't found here and, left out of the role, would widen it: an
    /// <i>All except…</i> role (or a mode a newer build wrote) would grant the libraries it leaves out, and an <i>Only…</i>
    /// role would have none left. The skipped record names them.
    /// </summary>
    public const string LibrariesUnmatched = "LibrariesUnmatched";

    /// <summary>
    /// A user's roles and overrides (their assignment) left out because libraries their own <c>Remove</c> list leaves out
    /// aren't found here (since 1.0.1): without them the user would see more than the export had
    /// them see (in <c>All</c> or <c>Inherit</c> mode, every library or every one their roles give). The skipped record
    /// names them. Its <c>Add</c> list losing a library only narrows, so that is imported, the library reported.
    /// </summary>
    public const string UserLibrariesUnmatched = "UserLibrariesUnmatched";

    /// <summary>The most roles (and default roles) an import takes (the plugin is designed for about 20).</summary>
    public const int MaxRoles = 1_000;

    /// <summary>The most records of each per-user kind an import takes: assignments, ledger entries, mirror records, users.</summary>
    public const int MaxUserRecords = 10_000;

    /// <summary>The most libraries an import's records may name (distinct ids), and its known libraries.</summary>
    public const int MaxLibraries = 1_000;

    /// <summary>The most ids an import's records may list in all: library references, override lists, role ids, ledger folders.</summary>
    public const int MaxReferences = 100_000;

    /// <summary>
    /// Refuses an import larger than this plugin is built for (400 <c>TooManyRecords</c>), before
    /// anything is mapped: the plugin is designed for 200 users, 50 libraries and 20 roles, and the limits are 50 times
    /// that or more, so a real export never meets them and a crafted one is answered at once.
    /// </summary>
    /// <param name="incoming">The export's document.</param>
    /// <param name="users">The export's <c>Users</c> entries.</param>
    /// <exception cref="AccessRolesException">400 <c>TooManyRecords</c>.</exception>
    public static void EnsureWithinLimits(AccessRolesDocument incoming, int users)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        static void Limit(string what, long count, long max)
        {
            if (count > max)
            {
                throw new AccessRolesException(
                    Jellyfin.Plugin.Kit.Api.PluginErrorKind.Invalid,
                    AccessRolesException.TooManyRecords,
                    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The export has {count} {what}; Access Roles imports at most {max}. Nothing was imported."));
            }
        }

        Limit("roles", incoming.Roles.Count, MaxRoles);
        Limit("default roles", incoming.DefaultRoleIds.Count, MaxRoles);
        Limit("assignments", incoming.Assignments.Count, MaxUserRecords);
        Limit("ledger entries", incoming.Ledger.Count, MaxUserRecords);
        Limit("mirror records", incoming.PreferenceMirror.Count, MaxUserRecords);
        Limit("users", users, MaxUserRecords);
        Limit("known libraries", incoming.KnownLibraries.Count, MaxLibraries);

        long references = 0;
        var libraries = new HashSet<Guid>();
        void Count(IEnumerable<Guid>? ids, bool library)
        {
            foreach (var id in ids ?? [])
            {
                references++;
                if (library)
                {
                    libraries.Add(id);
                }
            }

            Limit("ids in its records", references, MaxReferences);
        }

        foreach (var role in incoming.Roles.Where(r => r is not null))
        {
            Count(role.Libraries?.Refs?.Where(r => r is not null).Select(r => r.Id), library: true);
        }

        foreach (var assignment in incoming.Assignments.Where(a => a is not null))
        {
            Count(assignment.RoleIds, library: false);
            Count(assignment.Libraries?.Add, library: true);
            Count(assignment.Libraries?.Remove, library: true);
        }

        foreach (var entry in incoming.Ledger.Values.Where(e => e is not null))
        {
            Count(entry.OriginalEnabledFolders, library: true);
        }

        Limit("libraries named in its records", libraries.Count, MaxLibraries);
    }

    /// <summary>
    /// Counts the export's records per user id: assignments and ledger entries (the user map's input).
    /// </summary>
    /// <param name="incoming">The export's document.</param>
    /// <returns>User id to records.</returns>
    public static IReadOnlyDictionary<Guid, int> CountRecordsByUser(AccessRolesDocument incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        return incoming.Assignments.Select(a => a.UserId).Concat(incoming.Ledger.Keys)
            .Where(id => !id.Equals(Guid.Empty))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Maps the export's library ids to this server's.
    /// </summary>
    /// <param name="current">The stored document (its known libraries).</param>
    /// <param name="incoming">The export's document.</param>
    /// <param name="catalog">The libraries this server lists.</param>
    /// <returns>The map.</returns>
    public static LibraryMap MapLibraries(AccessRolesDocument current, AccessRolesDocument incoming, LibraryCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(catalog);

        // Each id the export's records name, with the export's name and folders for it. Every lookup is a dictionary's, so
        // the map is linear in the file (it runs again inside the store's writer for the save).
        var incomingKnown = FirstById(incoming.KnownLibraries, l => l.Id);
        var currentKnown = FirstById(current.KnownLibraries, l => l.Id);
        var wanted = new Dictionary<Guid, (string? Name, IReadOnlyList<string> Locations)>();
        void Want(Guid id, string? name, IReadOnlyList<string>? locations)
        {
            if (id.Equals(Guid.Empty))
            {
                return;
            }

            var known = incomingKnown.GetValueOrDefault(id);
            var had = wanted.GetValueOrDefault(id);
            wanted[id] = (
                known?.Name is { Length: > 0 } knownName ? knownName : had.Name ?? name,
                known is { Locations.Count: > 0 } ? known.Locations : had.Locations is { Count: > 0 } ? had.Locations : locations ?? []);
        }

        foreach (var role in incoming.Roles)
        {
            foreach (var reference in role.Libraries.Refs)
            {
                Want(reference.Id, reference.NameAtSelection, reference.Locations);
            }
        }

        foreach (var assignment in incoming.Assignments)
        {
            foreach (var id in assignment.Libraries.Add.Concat(assignment.Libraries.Remove))
            {
                Want(id, null, null);
            }
        }

        foreach (var entry in incoming.Ledger.Values)
        {
            foreach (var id in entry.OriginalEnabledFolders)
            {
                Want(id, null, null);
            }
        }

        var mapped = new Dictionary<Guid, MappedLibrary>();
        var unmatched = new Dictionary<Guid, UnmatchedLibrary>();
        foreach (var (id, info) in wanted)
        {
            if (catalog.Contains(id) || currentKnown.ContainsKey(id))
            {
                mapped[id] = new MappedLibrary(id, id, catalog.Find(id)?.Name ?? currentKnown[id].Name, "Id");
            }
        }

        var byId = mapped.Values.Select(m => m.Id).ToHashSet();
        var candidates = catalog.Libraries.Where(l => !byId.Contains(l.Id)).Select(l => (Library: l, Folders: Folders(l.Locations), Name: l.Name.Trim())).ToList();
        var fallback = new Dictionary<Guid, MappedLibrary>();
        foreach (var (id, info) in wanted.Where(w => !mapped.ContainsKey(w.Key)))
        {
            List<CatalogLibrary> found = [];
            var by = "Locations";
            var folders = Folders(info.Locations);
            if (folders.Count > 0)
            {
                found = [.. candidates.Where(c => c.Folders.Count > 0 && c.Folders.SetEquals(folders)).Select(c => c.Library)];
            }

            if (found.Count == 0 && !string.IsNullOrWhiteSpace(info.Name))
            {
                by = "Name";
                var name = info.Name.Trim();
                found = [.. candidates.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).Select(c => c.Library)];
            }

            if (found.Count == 1)
            {
                fallback[id] = new MappedLibrary(id, found[0].Id, found[0].Name, by) { Locations = found[0].Locations };
            }
            else
            {
                unmatched[id] = new UnmatchedLibrary(id, info.Name, info.Locations, found.Count > 1 ? Ambiguous : NoSuchLibrary);
            }
        }

        // One library here for one export id: two export ids found the same library, so neither is it.
        foreach (var group in fallback.Values.GroupBy(m => m.Id).Where(g => g.Count() > 1))
        {
            foreach (var m in group)
            {
                var info = wanted[m.ExportId];
                unmatched[m.ExportId] = new UnmatchedLibrary(m.ExportId, info.Name, info.Locations, Ambiguous);
                fallback.Remove(m.ExportId);
            }
        }

        foreach (var (id, m) in fallback)
        {
            mapped[id] = m;
        }

        return new LibraryMap(mapped, unmatched);
    }

    /// <summary>
    /// Combines the stored document with the export's.
    /// </summary>
    /// <param name="current">The stored document.</param>
    /// <param name="incoming">The export's document, at the current schema version.</param>
    /// <param name="mode">Merge or Replace.</param>
    /// <param name="users">The export's users found here.</param>
    /// <param name="libraries">The export's libraries found here (<see cref="MapLibraries"/>).</param>
    /// <param name="sameServer">Whether the export is this server's own (its ledger may fill in missing entries).</param>
    /// <param name="checkRoles">Whether each role is checked as the roles API checks a request (an
    /// import): one it would refuse is left out with the reason. A restore passes <c>false</c>: a snapshot is this
    /// server's own data, which may hold an <i>All except…</i> role a purge emptied, restored as it was.</param>
    /// <returns>The document to store, and what was done.</returns>
    public static Combined Combine(AccessRolesDocument current, AccessRolesDocument incoming, ImportMode mode, ImportUserMap users, LibraryMap libraries, bool sameServer, bool checkRoles = true)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(libraries);

        var skipped = new List<SkippedRecord>();
        var orphanedHere = current.KnownLibraries.Where(l => l.State.Is(AccessRolesLibraryState.Orphaned)).Select(l => l.Id).ToHashSet();
        var comparerRole = ImportMerge.JsonContent<AccessRole>();
        var comparerAssignment = ImportMerge.JsonContent<RoleAssignment>();

        // The roles, their references mapped; a role whose name another role of the export already took is skipped. Names
        // and ids are looked up in sets and dictionaries: nothing here is quadratic in the file.
        var incomingRoles = new List<AccessRole>();
        var incomingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storedRoles = FirstById(current.Roles, r => r.Id);
        var storedNames = current.Roles.Select(r => NameKey(r.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var given in incoming.Roles.Where(r => r is not null && !r.Id.Equals(Guid.Empty)))
        {
            var role = given;
            if (checkRoles)
            {
                if (AccessRolesRequests.CheckImported(given, out var reason, out var detail) is not { } valid)
                {
                    skipped.Add(new SkippedRecord("Role", given.Id, given.Name, reason!, null) { Detail = detail });
                    continue;
                }

                role = valid;
            }

            if (incomingNames.Contains(NameKey(role.Name)))
            {
                skipped.Add(new SkippedRecord("Role", role.Id, role.Name, NameTaken, null));
                continue;
            }

            if (MapRole(role, libraries, orphanedHere, skipped) is { } mapped)
            {
                incomingRoles.Add(mapped);
                incomingNames.Add(NameKey(mapped.Name));
            }
        }

        MergeResult<AccessRole> roles;
        if (mode == ImportMode.Replace)
        {
            roles = ImportMerge.Replace(current.Roles, incomingRoles, r => r.Id, comparerRole);
        }
        else
        {
            // A new role whose name a stored role has is someone else's role: skipped, never matched by name.
            var taken = incomingRoles.Where(r => !storedRoles.ContainsKey(r.Id) && storedNames.Contains(NameKey(r.Name))).ToList();
            skipped.AddRange(taken.Select(r => new SkippedRecord("Role", r.Id, r.Name, NameTaken, null)));
            roles = ImportMerge.Merge(current.Roles, incomingRoles.Except(taken), r => r.Id, r => r.UpdatedUtc, comparerRole);

            // A newer copy renamed to another stored role's name keeps the stored copy.
            // The merged records are one per id, so a name two of them have is shared with another record.
            var list = roles.Records.ToList();
            var shared = list.GroupBy(r => NameKey(r.Name), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).SelectMany(g => g).Select(r => r.Id).ToHashSet();
            var renamedOnto = new List<AccessRole>();
            for (var i = 0; i < list.Count; i++)
            {
                var role = list[i];
                if (shared.Contains(role.Id) && storedRoles.TryGetValue(role.Id, out var stored) && !ReferenceEquals(role, stored))
                {
                    renamedOnto.Add(role);
                    list[i] = stored;
                    skipped.Add(new SkippedRecord("Role", role.Id, role.Name, NameTaken, null));
                }
            }

            roles = renamedOnto.Count == 0 ? roles : new MergeResult<AccessRole>(list, roles.Counts with { Updated = roles.Counts.Updated - renamedOnto.Count, KeptNewer = roles.Counts.KeptNewer + renamedOnto.Count });
        }

        var roleIds = roles.Records.Select(r => r.Id).ToHashSet();

        // The assignments of the users found here, their ids this server's, their libraries mapped and their roles those of
        // the result; one whose Remove list names a library not found here is left out (UserLibrariesUnmatched).
        var incomingAssignments = new List<RoleAssignment>();
        var storedAssignments = FirstById(current.Assignments, a => a.UserId);
        var names = new Dictionary<Guid, string>();
        foreach (var user in users.Mapped)
        {
            names.TryAdd(user.NewId, user.Name);
        }

        foreach (var assignment in incoming.Assignments)
        {
            if (!users.TryMap(assignment.UserId, out var userId)
                || MapAssignment(assignment, userId, names.GetValueOrDefault(userId), libraries, roleIds, skipped) is not { } mapped)
            {
                continue;
            }

            incomingAssignments.Add(mapped with
            {
                // A lockout confirmation is this server's admin's, for this server's admins: never the data's.
                ConfirmedNoFullAdminUtc = storedAssignments.GetValueOrDefault(userId)?.ConfirmedNoFullAdminUtc
            });
        }

        var assignments = mode == ImportMode.Replace
            ? ImportMerge.Replace(current.Assignments, incomingAssignments, a => a.UserId, comparerAssignment)
            : ImportMerge.Merge(current.Assignments, incomingAssignments, a => a.UserId, a => a.UpdatedUtc, comparerAssignment);

        // A stored assignment Merge kept may name a role Replace can't have removed; Merge removes none.
        var defaults = mode == ImportMode.Replace
            ? incoming.DefaultRoleIds.Distinct().ToList()
            : current.DefaultRoleIds.Concat(incoming.DefaultRoleIds.Where(id => !current.DefaultRoleIds.Contains(id))).Distinct().ToList();
        var incomingById = FirstById(incoming.Roles.Where(r => r is not null), r => r.Id);
        foreach (var id in defaults.Where(id => !roleIds.Contains(id)))
        {
            skipped.Add(new SkippedRecord("DefaultRole", id, incomingById.GetValueOrDefault(id)?.Name, RoleNotImported, null));
        }

        defaults = [.. defaults.Where(roleIds.Contains)];

        // The ledger: this server's, but an export of this server fills in the original access of a user it manages that
        // has no entry here.
        var ledger = current.Ledger.ToDictionary(p => p.Key, p => p.Value);
        var assigned = assignments.Records.Select(a => a.UserId).ToHashSet();
        var ledgerAdded = 0;
        var ledgerIncoming = 0;
        foreach (var (exportId, entry) in incoming.Ledger)
        {
            if (!users.TryMap(exportId, out var userId))
            {
                continue;
            }

            ledgerIncoming++;
            if (!sameServer || ledger.ContainsKey(userId) || !assigned.Contains(userId))
            {
                continue;
            }

            ledger[userId] = new LedgerEntry
            {
                OriginalEnableAllFolders = entry.OriginalEnableAllFolders,
                OriginalEnabledFolders = [.. entry.OriginalEnabledFolders.Select(id => libraries.TryMap(id, out var mappedId) ? mappedId : Guid.Empty).Where(id => !id.Equals(Guid.Empty)).Distinct()],
                OriginalEnableUserPreferenceAccess = entry.OriginalEnableUserPreferenceAccess,
                CapturedUtc = entry.CapturedUtc,
                DefaultRolesUtc = entry.DefaultRolesUtc
            };
            ledgerAdded++;
        }

        // The names of the users the result holds assignments of, as this server knows them (an export's Users[] next).
        var known = current.KnownUsers;
        foreach (var user in users.Mapped.Where(m => assigned.Contains(m.NewId)))
        {
            known = known.With(user.NewId, user.Name);
        }

        var after = current with
        {
            Roles = roles.Records,
            Assignments = assignments.Records,
            DefaultRoleIds = defaults,
            Ledger = ledger,
            KnownUsers = known
        };
        var unchanged = roles.Counts.Added == 0 && roles.Counts.Updated == 0 && roles.Counts.Removed == 0
            && assignments.Counts.Added == 0 && assignments.Counts.Updated == 0 && assignments.Counts.Removed == 0
            && ledgerAdded == 0 && defaults.SequenceEqual(current.DefaultRoleIds);
        return new Combined(
            unchanged ? current : after,
            roles.Counts,
            assignments.Counts,
            new LedgerCounts(ledgerIncoming, ledgerAdded, sameServer),
            incoming.PreferenceMirror.Count,
            skipped);
    }

    // A role's references mapped to this server's libraries, or null when the role is left out: a reference not found here
    // only narrows an Only… role that keeps another library (reported, the role imported), and would widen any other (an
    // All except… role then grants what it left out, the Only… role has none left, a newer build's mode is unknown here),
    // so that role is left out whole, never imported wider than the export had it (fail closed).
    private static AccessRole? MapRole(AccessRole role, LibraryMap libraries, IReadOnlySet<Guid> orphanedHere, List<SkippedRecord> skipped)
    {
        var refs = new List<LibraryReference>();
        var seen = new HashSet<Guid>();
        var unmatched = new List<UnmatchedLibrary>();
        foreach (var reference in role.Libraries.Refs)
        {
            if (!libraries.TryMap(reference.Id, out var id))
            {
                unmatched.Add(libraries.Unmatched.GetValueOrDefault(reference.Id) ?? new UnmatchedLibrary(reference.Id, reference.NameAtSelection, reference.Locations, NoSuchLibrary));
                continue;
            }

            if (!seen.Add(id))
            {
                continue;
            }

            var byFallback = !id.Equals(reference.Id);
            var here = libraries.Mapped[reference.Id];
            refs.Add(reference with
            {
                Id = id,
                NameAtSelection = byFallback ? here.Name : reference.NameAtSelection,
                Locations = byFallback ? here.Locations : reference.Locations,
                Orphaned = reference.Orphaned || orphanedHere.Contains(id)
            });
        }

        if (unmatched.Count > 0 && (!role.Libraries.Mode.Is(AccessRolesRoleLibraryMode.Only) || refs.Count == 0))
        {
            skipped.Add(new SkippedRecord("Role", role.Id, role.Name, LibrariesUnmatched, null) { Libraries = unmatched });
            return null;
        }

        foreach (var library in unmatched)
        {
            skipped.Add(new SkippedRecord("RoleLibrary", library.ExportId, library.Name ?? role.Libraries.Refs.FirstOrDefault(r => r.Id.Equals(library.ExportId))?.NameAtSelection, library.Reason, role.Name));
        }

        return role with { Libraries = role.Libraries with { Refs = refs } };
    }

    // A user's assignment, its libraries mapped and its roles those of the result, or null when it is left out: a library
    // of its Remove list not found here would widen the user (every library, or every one their roles give, in All or
    // Inherit mode), so the assignment is left out whole, as an All except… role is.
    // A library of its Add list not found here only narrows: that list is imported without it, the library reported.
    private static RoleAssignment? MapAssignment(RoleAssignment assignment, Guid userId, string? name, LibraryMap libraries, IReadOnlySet<Guid> roleIds, List<SkippedRecord> skipped)
    {
        var unmatchedRemoved = assignment.Libraries.Remove.Distinct()
            .Where(id => !libraries.TryMap(id, out _))
            .Select(id => libraries.Unmatched.GetValueOrDefault(id) ?? new UnmatchedLibrary(id, null, [], NoSuchLibrary))
            .ToList();
        if (unmatchedRemoved.Count > 0)
        {
            skipped.Add(new SkippedRecord("Assignment", userId, name, UserLibrariesUnmatched, null) { Libraries = unmatchedRemoved });
            return null;
        }

        List<Guid> Map(IReadOnlyList<Guid> ids)
        {
            var result = new List<Guid>();
            var seen = new HashSet<Guid>();
            foreach (var id in ids)
            {
                if (libraries.TryMap(id, out var mapped))
                {
                    if (seen.Add(mapped))
                    {
                        result.Add(mapped);
                    }
                }
                else
                {
                    skipped.Add(new SkippedRecord("UserLibrary", id, libraries.Unmatched.GetValueOrDefault(id)?.Name, libraries.Unmatched.GetValueOrDefault(id)?.Reason ?? NoSuchLibrary, userId.ToString("N")));
                }
            }

            return result;
        }

        foreach (var id in assignment.RoleIds.Where(id => !roleIds.Contains(id)))
        {
            skipped.Add(new SkippedRecord("AssignmentRole", id, null, RoleNotImported, userId.ToString("N")));
        }

        return assignment with
        {
            UserId = userId,
            RoleIds = [.. assignment.RoleIds.Where(roleIds.Contains).Distinct()],
            Libraries = assignment.Libraries with { Add = Map(assignment.Libraries.Add), Remove = Map(assignment.Libraries.Remove) }
        };
    }

    // A role's name as names are compared: trimmed, letter case ignored by the sets' comparer.
    private static string NameKey(string? name) => name?.Trim() ?? string.Empty;

    // The first record of each id, as FirstOrDefault would find it.
    private static Dictionary<Guid, T> FirstById<T>(IEnumerable<T> records, Func<T, Guid> id)
    {
        var result = new Dictionary<Guid, T>();
        foreach (var record in records)
        {
            result.TryAdd(id(record), record);
        }

        return result;
    }

    // A library's folders as a set, to compare: letter case and a trailing separator don't count (Windows paths).
    private static HashSet<string> Folders(IEnumerable<string>? paths) => (paths ?? [])
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => p.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>An export's library found here.</summary>
    /// <param name="ExportId">The export's id.</param>
    /// <param name="Id">This server's id.</param>
    /// <param name="Name">This server's name for it.</param>
    /// <param name="By">How it was found: <c>Id</c>, <c>Locations</c> or <c>Name</c>.</param>
    internal sealed record MappedLibrary(Guid ExportId, Guid Id, string Name, string By)
    {
        /// <summary>Gets this server's folders of it, for a reference found by its folders or name.</summary>
        public IReadOnlyList<string> Locations { get; init; } = [];
    }

    /// <summary>An export's library no library here matches.</summary>
    /// <param name="ExportId">The export's id.</param>
    /// <param name="Name">The export's name for it.</param>
    /// <param name="Locations">The export's folders of it.</param>
    /// <param name="Reason"><see cref="NoSuchLibrary"/> or <see cref="Ambiguous"/>.</param>
    internal sealed record UnmatchedLibrary(Guid ExportId, string? Name, IReadOnlyList<string> Locations, string Reason);

    /// <summary>A record, or part of one, the import left out.</summary>
    /// <param name="Kind"><c>Role</c>, <c>RoleLibrary</c>, <c>Assignment</c> (1.0.1), <c>UserLibrary</c>, <c>AssignmentRole</c> or
    /// <c>DefaultRole</c>.</param>
    /// <param name="Id">The export's id of what was left out (an assignment's: this server's id of the user).</param>
    /// <param name="Name">Its name, when known.</param>
    /// <param name="Reason">Why.</param>
    /// <param name="Of">The role's name or the user's id it was on.</param>
    internal sealed record SkippedRecord(string Kind, Guid Id, string? Name, string Reason, string? Of)
    {
        /// <summary>
        /// Gets, for a role left out as <see cref="LibrariesUnmatched"/>, the libraries it names that aren't found here; for an
        /// assignment left out as <see cref="UserLibrariesUnmatched"/>, those of its <c>Remove</c> list.
        /// </summary>
        public IReadOnlyList<UnmatchedLibrary>? Libraries { get; init; }

        /// <summary>Gets what the roles API would answer, for a role left out because it would refuse it.</summary>
        public string? Detail { get; init; }
    }

    /// <summary>The ledger's part of an import.</summary>
    /// <param name="Incoming">The export's entries of users found here.</param>
    /// <param name="Added">The entries filled in.</param>
    /// <param name="SameServer">Whether the export was this server's (otherwise none is taken).</param>
    internal sealed record LedgerCounts(int Incoming, int Added, bool SameServer);

    /// <summary>An import's document and what it did.</summary>
    /// <param name="Document">The document to store (the stored instance when nothing changes).</param>
    /// <param name="Roles">The roles' counts.</param>
    /// <param name="Assignments">The assignments' counts.</param>
    /// <param name="Ledger">The ledger's.</param>
    /// <param name="MirrorRecords">The export's mirror records, never imported.</param>
    /// <param name="Skipped">What was left out.</param>
    internal sealed record Combined(AccessRolesDocument Document, ImportCounts Roles, ImportCounts Assignments, LedgerCounts Ledger, int MirrorRecords, IReadOnlyList<SkippedRecord> Skipped);

    /// <summary>The export's library ids and this server's.</summary>
    internal sealed class LibraryMap
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="LibraryMap"/> class.
        /// </summary>
        /// <param name="mapped">The libraries found.</param>
        /// <param name="unmatched">The ones not found.</param>
        public LibraryMap(IReadOnlyDictionary<Guid, MappedLibrary> mapped, IReadOnlyDictionary<Guid, UnmatchedLibrary> unmatched)
        {
            Mapped = mapped;
            Unmatched = unmatched;
        }

        /// <summary>Gets the libraries found, by export id.</summary>
        public IReadOnlyDictionary<Guid, MappedLibrary> Mapped { get; }

        /// <summary>Gets the ones not found, by export id.</summary>
        public IReadOnlyDictionary<Guid, UnmatchedLibrary> Unmatched { get; }

        /// <summary>
        /// Gets this server's id of an export's library.
        /// </summary>
        /// <param name="exportId">The export's id.</param>
        /// <param name="id">This server's.</param>
        /// <returns><c>true</c> when found.</returns>
        public bool TryMap(Guid exportId, out Guid id)
        {
            if (Mapped.TryGetValue(exportId, out var found))
            {
                id = found.Id;
                return true;
            }

            id = Guid.Empty;
            return false;
        }
    }
}
