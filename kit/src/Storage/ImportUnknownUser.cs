using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Kit.Users;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// An export's user not found on this server: reported, and its records skipped.
/// </summary>
/// <param name="Id">The id in the export.</param>
/// <param name="Name">The export's name for the user, or <c>null</c>.</param>
/// <param name="Records">How many of the export's records the user owns.</param>
/// <param name="Reason">Why, one of this type's constants.</param>
internal sealed record ImportUnknownUser(Guid Id, string? Name, int Records, string Reason)
{
    /// <summary>By id: no user of this server has the id.</summary>
    public const string NotOnThisServer = "NotOnThisServer";

    /// <summary>By name: the export has no name for the user.</summary>
    public const string NoKnownName = "NoKnownName";

    /// <summary>By name: no user of this server has the name, or more than one has it.</summary>
    public const string NoUserWithThisName = "NoUserWithThisName";

    /// <summary>By name: two of the export's users with records share the name, so neither can be told apart.</summary>
    public const string SameNameTwice = "SameNameTwice";

    /// <summary>The user was deleted on this server (the kit's marks); a user who took the name is someone else.</summary>
    public const string DeletedOnThisServer = "DeletedOnThisServer";

    /// <summary>By name: the export says the user was deleted on the exporting server; a user with the name is someone else.</summary>
    public const string DeletedOnExportingServer = "DeletedOnExportingServer";

    /// <summary>
    /// Describes the user with plain values.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Id"] = KnownUsers.FormatId(Id),
        ["Name"] = Name,
        ["Records"] = Records,
        ["Reason"] = Reason
    };
}
