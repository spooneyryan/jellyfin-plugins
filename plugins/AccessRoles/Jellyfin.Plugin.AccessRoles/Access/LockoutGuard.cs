using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// The <c>LockoutGuard</c> for the library sync: library roles apply to administrators, so a sync must never
/// leave no enabled administrator with access to every library unless an admin confirmed it. The roles API checks every
/// save (<c>ChangeAnalysis</c>, 409 <c>NoFullAdministrator</c>); this checks what a sync is about to write, which can also
/// come from a library change or from reapplying a drifted administrator's own edit.
/// </summary>
internal static class LockoutGuard
{
    /// <summary>
    /// Finds the planned writes the guard refuses: when the writes would leave no enabled administrator with every library
    /// while at least one has it now, the writes that take it from an administrator whose narrower access isn't confirmed.
    /// </summary>
    /// <param name="catalog">The libraries the server lists now.</param>
    /// <param name="users">The server's users, with their native access now.</param>
    /// <param name="planned">The library fields each user is about to get.</param>
    /// <param name="confirmed">Whether an administrator's narrower access was confirmed (<c>ConfirmedNoFullAdminUtc</c>, or
    /// <c>confirmNoFullAdmin=true</c> on this sync).</param>
    /// <returns>The users whose write is refused; empty when nothing is.</returns>
    public static IReadOnlySet<Guid> Refused(LibraryCatalog catalog, IReadOnlyList<ServerUser> users, IReadOnlyDictionary<Guid, LibraryFields> planned, Func<Guid, bool> confirmed)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(confirmed);

        var admins = users.Where(u => u.IsAdministrator && !u.IsDisabled).ToList();
        var fullNow = admins.Where(a => HasEveryLibrary(catalog, a.Native.Libraries)).ToList();
        var fullAfter = admins.Count(a => HasEveryLibrary(catalog, planned.TryGetValue(a.Id, out var fields) ? fields : a.Native.Libraries));
        if (admins.Count == 0 || fullNow.Count == 0 || fullAfter > 0)
        {
            return new HashSet<Guid>();
        }

        return fullNow
            .Where(a => planned.TryGetValue(a.Id, out var fields) && !HasEveryLibrary(catalog, fields) && !confirmed(a.Id))
            .Select(a => a.Id)
            .ToHashSet();
    }

    /// <summary>
    /// Gets whether library fields open every library there is now.
    /// </summary>
    /// <param name="catalog">The libraries.</param>
    /// <param name="fields">The fields.</param>
    /// <returns><c>true</c> for every library.</returns>
    public static bool HasEveryLibrary(LibraryCatalog catalog, LibraryFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return ChangeAnalysis.HasEveryLibrary(catalog, fields.EnableAllFolders, fields.EnabledFolders);
    }
}
