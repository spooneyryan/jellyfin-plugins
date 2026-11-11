using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.Users;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// One entry of an export's <c>Users[]</c>: a user id the data holds records of and the user's name, which
/// <c>mapUsersBy=Name</c> matches on another server, and whether the user was deleted on the exporting server.
/// </summary>
/// <param name="Id">The user id on the exporting server.</param>
/// <param name="Name">The user's name there, or <c>null</c> when it isn't known.</param>
/// <param name="Deleted">
/// Whether Jellyfin reported the user deleted on the exporting server while the user held records (the kit's
/// <c>Deleted</c> and <c>PolicyApplied</c> marks): an import never gives such a user's records to anyone by name, because
/// a user who took the old name is someone else. Written as <c>"Deleted": true</c>, and only then.
/// </param>
internal sealed record ExportUser(Guid Id, string? Name, bool Deleted = false)
{
    /// <summary>
    /// Builds <c>Users[]</c> for the stored user ids: the server's current name where the server knows the id, else the
    /// last seen name from the store's <see cref="KnownUsers"/>, and <see cref="Deleted"/> for an id the server doesn't
    /// know that was deleted here.
    /// </summary>
    /// <param name="storedUserIds">The user ids that own records.</param>
    /// <param name="known">The store's last seen names.</param>
    /// <param name="serverUsers">The server's users, id to name (<see cref="KitUsers.GetAll"/>).</param>
    /// <param name="deletedUsers">The users deleted on this server (<see cref="UserReconciler.DeletedUserIds"/>).</param>
    /// <returns>The entries, ordered by id.</returns>
    public static IReadOnlyList<ExportUser> For(
        IEnumerable<Guid> storedUserIds,
        KnownUsers known,
        IReadOnlyDictionary<Guid, string> serverUsers,
        IReadOnlySet<Guid> deletedUsers)
    {
        ArgumentNullException.ThrowIfNull(storedUserIds);
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(serverUsers);
        ArgumentNullException.ThrowIfNull(deletedUsers);

        return storedUserIds
            .Where(id => !id.Equals(Guid.Empty))
            .Distinct()
            .OrderBy(KnownUsers.FormatId, StringComparer.Ordinal)
            .Select(id => serverUsers.TryGetValue(id, out var name)
                ? new ExportUser(id, string.IsNullOrWhiteSpace(name) ? known.NameOf(id) : name)
                : new ExportUser(id, known.NameOf(id), deletedUsers.Contains(id)))
            .ToList();
    }
}
