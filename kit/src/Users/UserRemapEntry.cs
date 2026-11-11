using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Users;

/// <summary>
/// An unknown stored user id that <i>Remap by name</i> gives to a server user (<c>Mapped:[{OldId, NewId, Name, Records}]</c>).
/// </summary>
/// <param name="OldId">The stored id.</param>
/// <param name="NewId">The server user's id.</param>
/// <param name="Name">The server user's name.</param>
/// <param name="Records">How many records move.</param>
internal sealed record UserRemapEntry(Guid OldId, Guid NewId, string Name, int Records)
{
    /// <summary>
    /// Describes the entry with plain values.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["OldId"] = KnownUsers.FormatId(OldId),
        ["NewId"] = KnownUsers.FormatId(NewId),
        ["Name"] = Name,
        ["Records"] = Records
    };
}
