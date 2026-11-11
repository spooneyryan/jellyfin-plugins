using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.Users;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// Which of an export's users exist on this server, and under which id (<c>mapUsersBy=Id|Name</c>). Users that
/// don't are reported with a reason and their records skipped; a plugin's import adapter rewrites the user ids of the
/// records it keeps with <see cref="TryMap"/>.
/// </summary>
/// <remarks>
/// By name, a user deleted on this server (the kit's marks) or on the exporting server (<see cref="ExportUser.Deleted"/>)
/// is never matched: a user who took the old name is someone else, the rule <i>Remap by name</i> follows too. Nor is a
/// name that two of the export's users with records share. By id, the same id is the same account, so it is matched
/// whenever this server knows it.
/// </remarks>
internal sealed class ImportUserMap
{
    private readonly Dictionary<Guid, Guid> _map;

    private ImportUserMap(MapUsersBy by, Dictionary<Guid, Guid> map, IReadOnlyList<ImportUserMapping> mapped, IReadOnlyList<ImportUnknownUser> unknown)
    {
        By = by;
        _map = map;
        Mapped = mapped;
        Unknown = unknown;
    }

    /// <summary>Gets how users were matched.</summary>
    public MapUsersBy By { get; }

    /// <summary>Gets the export's user id to this server's, for every user found.</summary>
    public IReadOnlyDictionary<Guid, Guid> Map => _map;

    /// <summary>Gets the users found, ordered by name.</summary>
    public IReadOnlyList<ImportUserMapping> Mapped { get; }

    /// <summary>Gets the users not found, ordered by name, whose records are skipped.</summary>
    public IReadOnlyList<ImportUnknownUser> Unknown { get; }

    /// <summary>
    /// Matches the users of an export's data with this server's users.
    /// </summary>
    /// <param name="dataUsers">The user ids the export's records belong to, with each one's number of records.</param>
    /// <param name="exportUsers">The export's <c>Users[]</c>, which give the names.</param>
    /// <param name="serverUsers">This server's users, id to name (<see cref="KitUsers.GetAll"/>).</param>
    /// <param name="deletedUsers">The users deleted on this server (<see cref="UserReconciler.DeletedUserIds"/>).</param>
    /// <param name="by">How to match: the same id, or the same name ignoring case.</param>
    /// <returns>The map.</returns>
    public static ImportUserMap Build(
        IReadOnlyDictionary<Guid, int> dataUsers,
        IReadOnlyList<ExportUser> exportUsers,
        IReadOnlyDictionary<Guid, string> serverUsers,
        IReadOnlySet<Guid> deletedUsers,
        MapUsersBy by)
    {
        ArgumentNullException.ThrowIfNull(dataUsers);
        ArgumentNullException.ThrowIfNull(exportUsers);
        ArgumentNullException.ThrowIfNull(serverUsers);
        ArgumentNullException.ThrowIfNull(deletedUsers);

        var exportNames = new Dictionary<Guid, string>();
        var deletedThere = new HashSet<Guid>();
        foreach (var user in exportUsers)
        {
            if (!string.IsNullOrWhiteSpace(user.Name))
            {
                exportNames[user.Id] = user.Name.Trim();
            }

            if (user.Deleted)
            {
                deletedThere.Add(user.Id);
            }
        }

        // By name, the names of the export's users that could be matched at all: two of them with one name can't be told apart.
        var namesTaken = dataUsers.Keys
            .Where(id => !id.Equals(Guid.Empty) && !deletedUsers.Contains(id) && !deletedThere.Contains(id) && exportNames.ContainsKey(id))
            .GroupBy(id => exportNames[id], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var byName = serverUsers
            .Where(u => !string.IsNullOrWhiteSpace(u.Value))
            .GroupBy(u => u.Value.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);

        var map = new Dictionary<Guid, Guid>();
        var mapped = new List<ImportUserMapping>();
        var unknown = new List<ImportUnknownUser>();
        foreach (var (id, records) in dataUsers)
        {
            if (id.Equals(Guid.Empty))
            {
                continue;
            }

            var exportName = exportNames.GetValueOrDefault(id);
            KeyValuePair<Guid, string>? match = null;
            string reason;
            if (by == MapUsersBy.Id)
            {
                if (serverUsers.TryGetValue(id, out var name))
                {
                    match = new KeyValuePair<Guid, string>(id, name);
                }

                reason = deletedUsers.Contains(id) ? ImportUnknownUser.DeletedOnThisServer
                    : deletedThere.Contains(id) ? ImportUnknownUser.DeletedOnExportingServer
                    : ImportUnknownUser.NotOnThisServer;
            }
            else if (deletedUsers.Contains(id))
            {
                reason = ImportUnknownUser.DeletedOnThisServer;
            }
            else if (deletedThere.Contains(id))
            {
                reason = ImportUnknownUser.DeletedOnExportingServer;
            }
            else if (exportName is null)
            {
                reason = ImportUnknownUser.NoKnownName;
            }
            else if (namesTaken[exportName] > 1)
            {
                reason = ImportUnknownUser.SameNameTwice;
            }
            else
            {
                if (byName.TryGetValue(exportName, out var user))
                {
                    match = user;
                }

                reason = ImportUnknownUser.NoUserWithThisName;
            }

            if (match is { } found)
            {
                map[id] = found.Key;
                mapped.Add(new ImportUserMapping(id, found.Key, found.Value, records));
            }
            else
            {
                unknown.Add(new ImportUnknownUser(id, exportName, records, reason));
            }
        }

        return new ImportUserMap(
            by,
            map,
            mapped.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => KnownUsers.FormatId(m.Id), StringComparer.Ordinal).ToList(),
            unknown.OrderBy(u => u.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(u => KnownUsers.FormatId(u.Id), StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Gets this server's id for an export's user.
    /// </summary>
    /// <param name="id">The id in the export.</param>
    /// <param name="newId">This server's id.</param>
    /// <returns><c>true</c> when the user was found; its records are imported.</returns>
    public bool TryMap(Guid id, out Guid newId) => _map.TryGetValue(id, out newId);
}
