using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.AccessRoles.Policy;

/// <summary>
/// What a read of a user's committed row found (<see cref="ICommittedUsers"/>).
/// </summary>
/// <param name="Read">Whether the database could be read; <c>false</c> when it couldn't (logged), so nothing is known.</param>
/// <param name="User">The committed row, untracked; <c>null</c> when the database has no user with the id (or wasn't read).</param>
internal sealed record CommittedUser(bool Read, User? User)
{
    /// <summary>Gets the result of a read that failed.</summary>
    public static CommittedUser Unavailable { get; } = new(false, null);

    /// <summary>Gets the result of a read that found no row.</summary>
    public static CommittedUser Missing { get; } = new(true, null);
}
