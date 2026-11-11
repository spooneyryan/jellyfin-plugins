using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Users;

/// <summary>
/// An unknown stored user id that <i>Remap by name</i> leaves alone, and why.
/// </summary>
/// <param name="OldId">The stored id.</param>
/// <param name="Name">Its last seen name, or <c>null</c>.</param>
/// <param name="Records">How many records it owns.</param>
/// <param name="Reason">
/// <c>NoKnownName</c> (no name was ever seen), <c>NoUserWithThisName</c>, <c>SameNameTwice</c> (two stored ids share the
/// name) or <c>DeletedOnThisServer</c> (Jellyfin reported the user deleted here, so a new user of the same name is
/// someone else).
/// </param>
internal sealed record UserRemapUnmatched(Guid OldId, string? Name, int Records, string Reason)
{
    /// <summary>No name was ever seen for the id.</summary>
    public const string NoKnownName = "NoKnownName";

    /// <summary>No server user has the name.</summary>
    public const string NoUserWithThisName = "NoUserWithThisName";

    /// <summary>Two stored ids share the name.</summary>
    public const string SameNameTwice = "SameNameTwice";

    /// <summary>The user was deleted on this server.</summary>
    public const string DeletedOnThisServer = "DeletedOnThisServer";

    /// <summary>
    /// Describes the entry with plain values.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["OldId"] = KnownUsers.FormatId(OldId),
        ["Name"] = Name,
        ["Records"] = Records,
        ["Reason"] = Reason
    };
}
