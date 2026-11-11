using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AccessRoles.Policy;

/// <summary>
/// Reads a user as the server's database holds them, read only (<c>IDbContextFactory&lt;JellyfinDbContext&gt;</c>,
/// <c>AsNoTracking</c>): on 10.11.5 the user manager's cached object, which <c>GetUserDto(GetUserById(id))</c> gives and the
/// server enforces, can differ from the committed row after a failed save (the cache keeps the entity of a
/// save that lost, <c>UserManager.cs:719-720</c>) or a sign-in race (seen on 10.11.5). <see cref="PolicyWriter"/> compares the
/// two before each write and builds from the committed row when they differ.
/// </summary>
internal interface ICommittedUsers
{
    /// <summary>
    /// Reads a user's committed row with its permissions, preferences and access schedules, untracked.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What the read found.</returns>
    Task<CommittedUser> ReadAsync(Guid userId, CancellationToken cancellationToken);
}
