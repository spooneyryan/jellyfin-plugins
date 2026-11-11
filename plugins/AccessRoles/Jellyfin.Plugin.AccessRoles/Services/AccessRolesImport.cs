using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// <i>Import current setup</i> (<c>ImportExisting</c>; the goal: "import the current setup without
/// changing anyone's access"): one role per distinct library access among the non-admin users, named after its
/// libraries ("Movies + Kids"), with no locks, and the membership. A pure function of the stored document, the libraries
/// listed now and the server's users.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Users with every library (<c>EnableAllFolders</c>) are left unmanaged by default, with one warning listing them
/// (<c>EveryLibrary</c>; managing them takes their library access away from the stock page,
/// whose later edits would be overridden). Opted in (<c>manageUsersWithEveryLibrary=true</c>, the wizard's <i>Also manage
/// users who see every library</i>), they get an <c>All libraries</c> role (mode <c>All</c>, future libraries included, as
/// they have now), with one warning saying so (<c>EveryLibraryManaged</c>). Any other user gets a role <c>Only</c> their
/// libraries.</item>
/// <item>Not imported, with a warning: administrators (no lock applies to them; library roles do, so proposing one would
/// manage an admin's access), users with a legacy <c>BlockedMediaFolders</c> list (<c>CannotManage</c>),
/// users with no library (no role can say "nothing"; the explicit per-user <c>Only…</c> with nothing is the admin's
/// choice), and users whose access no role would give exactly as the sync resolves it (a library created again under the
/// same id, a rename waiting: <c>AccessWouldChange</c>).</item>
/// <item>Imported, with a warning: ids of libraries that no longer exist in a user's access (the sync drops them, which
/// changes nothing they can open), <c>EnableLiveTvManagement</c> (one warning listing them: Jellyfin
/// gives it to every new user before 10.11.7), and users shown on the login screen.</item>
/// </list>
/// </remarks>
internal static class AccessRolesImport
{
    /// <summary>The name of the role for users with every library.</summary>
    public const string AllLibrariesName = "All libraries";

    /// <summary>The warning codes (<see cref="AccessRolesImportWarningDto.Code"/>).</summary>
    public const string Administrator = "Administrator";

    /// <summary>A user with a legacy <c>BlockedMediaFolders</c> list.</summary>
    public const string CannotManage = "CannotManage";

    /// <summary>A user with no library.</summary>
    public const string NoLibraries = "NoLibraries";

    /// <summary>A user whose access no role gives exactly.</summary>
    public const string AccessWouldChange = "AccessWouldChange";

    /// <summary>A user whose access has ids of libraries that are gone.</summary>
    public const string DeletedLibraries = "DeletedLibraries";

    /// <summary>The imported users with <c>EnableLiveTvManagement</c>, in one warning.</summary>
    public const string LiveTvManagement = "LiveTvManagement";

    /// <summary>The users with every library the import leaves unmanaged (not opted in), in one warning.</summary>
    public const string EveryLibrary = "EveryLibrary";

    /// <summary>The users with every library the import gives the <c>All libraries</c> role (opted in), in one warning.</summary>
    public const string EveryLibraryManaged = "EveryLibraryManaged";

    /// <summary>A user listed on the login screen.</summary>
    public const string ShownOnLoginScreen = "ShownOnLoginScreen";

    private const int MaxName = 100;

    /// <summary>
    /// Plans the import.
    /// </summary>
    /// <param name="document">The stored document (its roles' names are taken).</param>
    /// <param name="listed">The libraries the server lists now.</param>
    /// <param name="rules">Resolves a document's rules catalog, as the sync does (a pending rename's library withheld).</param>
    /// <param name="users">The server's users.</param>
    /// <param name="now">The time, UTC.</param>
    /// <param name="manageEveryLibrary">Whether users with every library get the <c>All libraries</c> role; by default they
    /// are left unmanaged.</param>
    /// <returns>The roles, the assignments and the warnings.</returns>
    public static ImportPlan Plan(AccessRolesDocument document, LibraryCatalog listed, Func<AccessRolesDocument, LibraryCatalog> rules, IReadOnlyList<ServerUser> users, DateTime now, bool manageEveryLibrary = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(users);

        var warnings = new List<AccessRolesImportWarningDto>();
        var everyLibrary = new List<ServerUser>();
        var groups = new Dictionary<string, (bool All, IReadOnlyList<Guid> Libraries, List<ServerUser> Members)>(StringComparer.Ordinal);
        foreach (var user in users.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ThenBy(u => u.Id))
        {
            if (user.IsAdministrator)
            {
                warnings.Add(Warning(Administrator, user, false, user.Name + " is an administrator and isn't imported: no lock ever applies to administrators, and their library access stays as it is."));
                continue;
            }

            if (user.Native.BlockedMediaFolders.Count > 0)
            {
                warnings.Add(Warning(CannotManage, user, false, user.Name + " has a legacy blocked-folders list, which Access Roles can't manage; they aren't imported."));
                continue;
            }

            var access = user.Native.Libraries;
            IReadOnlyList<Guid> existing = access.EnableAllFolders ? [] : listed.Order(access.EnabledFolders.Where(listed.Contains));
            if (!access.EnableAllFolders && existing.Count == 0)
            {
                warnings.Add(Warning(NoLibraries, user, false, user.Name + " can open no library, which no role can express; they aren't imported (an explicit per-user Only with nothing can say it later)."));
                continue;
            }

            if (access.EnableAllFolders && !manageEveryLibrary)
            {
                everyLibrary.Add(user);
                continue;
            }

            var key = access.EnableAllFolders ? "*" : string.Join(",", existing.Select(id => id.ToString("N", CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal));
            if (!groups.TryGetValue(key, out var group))
            {
                group = (access.EnableAllFolders, existing, []);
                groups[key] = group;
            }

            group.Members.Add(user);
        }

        // One role per distinct access, by name; then every member checked as the sync resolves them: whoever would get
        // anything else isn't imported (fail closed: never a change of access).
        var taken = new HashSet<string>(document.Roles.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        var roles = new List<(AccessRole Role, List<ServerUser> Members)>();
        foreach (var group in groups.Values.OrderBy(g => g.All ? 0 : 1).ThenBy(g => NameOf(g.All, g.Libraries, listed), StringComparer.OrdinalIgnoreCase))
        {
            var name = Unique(NameOf(group.All, group.Libraries, listed), taken);
            var request = new AccessRolesRoleRequest
            {
                Name = name,
                Description = "Imported on " + now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " from the library access its members had.",
                Locks = [],
                Libraries = new AccessRolesRoleLibrariesRequest
                {
                    Mode = group.All ? AccessRolesRoleLibraryMode.All : AccessRolesRoleLibraryMode.Only,
                    Refs = group.All ? [] : [.. group.Libraries.Select(id => new AccessRolesLibraryRefRequest { Id = id })]
                }
            };
            roles.Add((AccessRolesRequests.BuildRole(request, null, Guid.NewGuid(), listed, now), group.Members));
        }

        if (everyLibrary.Count > 0)
        {
            warnings.Add(Group(EveryLibrary, everyLibrary, false, Count(everyLibrary) + (everyLibrary.Count == 1 ? " sees every library and isn't" : " see every library and aren't") + " imported, so their library access stays the stock page's: " + string.Join(", ", Sorted(everyLibrary).Select(u => u.Name)) + ". Tick \"Also manage users who see every library\" to give them an \"" + AllLibrariesName + "\" role."));
        }

        var after = With(document, roles);
        var catalog = rules(after);
        var kept = new List<(AccessRole Role, List<ServerUser> Members)>();
        var liveTv = new List<ServerUser>();
        var managedEvery = new List<ServerUser>();
        foreach (var (role, members) in roles)
        {
            var stays = new List<ServerUser>();
            foreach (var member in members)
            {
                var result = EffectiveRulesResolver.Resolve(after, catalog, member).Libraries;
                var before = listed.Expand(member.Native.EnableAllFolders, member.Native.EnabledFolders);
                var then = listed.Expand(result.EnableAllFolders, result.EnabledFolders);
                if (!result.Writes || !before.SetEquals(then) || (member.Native.EnableAllFolders && !result.EnableAllFolders))
                {
                    warnings.Add(Warning(AccessWouldChange, member, false, member.Name + " isn't imported: role \"" + role.Name + "\" wouldn't give them exactly the libraries they can open now (a library deleted and created again, or a rename still waiting); assign them by hand."));
                    continue;
                }

                stays.Add(member);
                List<Guid> gone = member.Native.EnableAllFolders ? [] : [.. member.Native.EnabledFolders.Where(id => !listed.Contains(id))];
                if (gone.Count > 0)
                {
                    warnings.Add(Warning(DeletedLibraries, member, true, member.Name + "'s library access names " + gone.Count.ToString(CultureInfo.InvariantCulture) + " libraries that no longer exist; the library sync removes them from it, which changes nothing they can open."));
                }

                if (member.Native.EnableLiveTvManagement)
                {
                    liveTv.Add(member);
                }

                if (member.Native.EnableAllFolders)
                {
                    managedEvery.Add(member);
                }

                if (!member.IsHidden)
                {
                    warnings.Add(Warning(ShownOnLoginScreen, member, true, member.Name + " is shown on the login screen."));
                }
            }

            if (stays.Count > 0)
            {
                kept.Add((role, stays));
            }
        }

        if (managedEvery.Count > 0)
        {
            warnings.Add(Group(EveryLibraryManaged, managedEvery, true, Count(managedEvery) + (managedEvery.Count == 1 ? " who sees every library gets" : " who see every library get") + " the \"" + AllLibrariesName + "\" role: " + string.Join(", ", Sorted(managedEvery).Select(u => u.Name)) + ". From now on Access Roles manages their library access, and a later edit on the stock Library access page is overridden."));
        }

        if (liveTv.Count > 0)
        {
            warnings.Add(Group(LiveTvManagement, liveTv, true, Count(liveTv) + " the import assigns may manage Live TV (they can change Live TV server settings and schedule and delete recordings): " + string.Join(", ", Sorted(liveTv).Select(u => u.Name)) + ". Jellyfin turns this on for every new user before 10.11.7; clear it on the stock Dashboard page where it isn't meant."));
        }

        return new ImportPlan(kept, warnings, now);
    }

    /// <summary>
    /// Gets a document with a plan's roles and assignments added.
    /// </summary>
    /// <param name="document">The stored document.</param>
    /// <param name="roles">The roles and their members.</param>
    /// <returns>The document with the roles and their members' assignments.</returns>
    public static AccessRolesDocument With(AccessRolesDocument document, IReadOnlyList<(AccessRole Role, List<ServerUser> Members)> roles)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(roles);
        return document with
        {
            Roles = [.. document.Roles, .. roles.Select(r => r.Role)],
            Assignments = [.. document.Assignments, .. roles.SelectMany(r => r.Members.Select(m => new RoleAssignment { UserId = m.Id, RoleIds = [r.Role.Id], UpdatedUtc = r.Role.UpdatedUtc }))]
        };
    }

    private static string NameOf(bool all, IReadOnlyList<Guid> libraries, LibraryCatalog listed)
    {
        if (all)
        {
            return AllLibrariesName;
        }

        var names = libraries.Select(id => listed.Find(id)?.Name ?? id.ToString("N", CultureInfo.InvariantCulture)).ToList();
        var joined = string.Join(" + ", names);
        if (joined.Length <= MaxName - 6)
        {
            return joined;
        }

        // "A + B and 7 more", within the role name's 100 characters (with room for a " (2)").
        for (var shown = names.Count - 1; shown >= 1; shown--)
        {
            var candidate = string.Join(" + ", names.Take(shown)) + " and " + (names.Count - shown).ToString(CultureInfo.InvariantCulture) + " more";
            if (candidate.Length <= MaxName - 6)
            {
                return candidate;
            }
        }

        return names[0][..Math.Min(names[0].Length, MaxName - 20)] + " and " + (names.Count - 1).ToString(CultureInfo.InvariantCulture) + " more";
    }

    private static string Unique(string name, HashSet<string> taken)
    {
        var candidate = name;
        for (var n = 2; !taken.Add(candidate); n++)
        {
            candidate = name + " (" + n.ToString(CultureInfo.InvariantCulture) + ")";
        }

        return candidate;
    }

    private static AccessRolesImportWarningDto Warning(string code, ServerUser user, bool imported, string message)
        => new() { Code = code, UserId = user.Id, Name = user.Name, Users = [AccessRolesMapper.UserRef(user)], Imported = imported, Message = message };

    // One warning about several users: no UserId, every user in Users, by name.
    private static AccessRolesImportWarningDto Group(string code, List<ServerUser> users, bool imported, string message)
        => new() { Code = code, Users = [.. Sorted(users).Select(AccessRolesMapper.UserRef)], Imported = imported, Message = message };

    private static IEnumerable<ServerUser> Sorted(IEnumerable<ServerUser> users) => users.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ThenBy(u => u.Id);

    private static string Count(IReadOnlyList<ServerUser> users)
        => users.Count == 1 ? "1 user" : users.Count.ToString(CultureInfo.InvariantCulture) + " users";
}
