using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// The server's users with their native access, read through the kit's <c>KitUsers</c>. Only
/// reads: the only writer of a user's policy is <c>Policy/PolicyWriter</c>.
/// </summary>
internal interface IServerUsers
{
    /// <summary>
    /// Lists every user.
    /// </summary>
    /// <returns>The users, by name.</returns>
    IReadOnlyList<ServerUser> GetAll();

    /// <summary>
    /// Finds a user.
    /// </summary>
    /// <param name="id">The user id.</param>
    /// <returns>The user, or <c>null</c> when the server has none with the id.</returns>
    ServerUser? Find(Guid id);
}
