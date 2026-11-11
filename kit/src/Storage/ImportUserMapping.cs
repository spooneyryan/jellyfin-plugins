using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Kit.Users;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// An export's user found on this server (<see cref="ImportUserMap"/>).
/// </summary>
/// <param name="Id">The id in the export.</param>
/// <param name="NewId">The id on this server (the same one when mapping by id).</param>
/// <param name="Name">This server's name for the user.</param>
/// <param name="Records">How many of the export's records the user owns.</param>
internal sealed record ImportUserMapping(Guid Id, Guid NewId, string Name, int Records)
{
    /// <summary>
    /// Describes the mapping with plain values.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Id"] = KnownUsers.FormatId(Id),
        ["NewId"] = KnownUsers.FormatId(NewId),
        ["Name"] = Name,
        ["Records"] = Records
    };
}
