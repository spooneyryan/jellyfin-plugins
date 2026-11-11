using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// The mixed-audience answers of one pass: per user, whether each item asked about is theirs to open. The definitions of a
/// pass share many members, and a user with parental or tag rules costs one item load per member.
/// </summary>
internal sealed class AudienceMemo
{
    private readonly Dictionary<Guid, Dictionary<Guid, bool>> _users = [];

    /// <summary>Gets the answers kept for a user.</summary>
    /// <param name="userId">The user's id.</param>
    /// <returns>The answers, by item id.</returns>
    public Dictionary<Guid, bool> For(Guid userId)
    {
        if (!_users.TryGetValue(userId, out var answers))
        {
            answers = [];
            _users[userId] = answers;
        }

        return answers;
    }
}
