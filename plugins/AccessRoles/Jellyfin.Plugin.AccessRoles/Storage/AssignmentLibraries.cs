using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// A user's own library settings ("library mode per user", <c>Assignments[].Libraries</c>): the starting
/// set comes from the mode, then <c>effective = (starting set ∪ Add) − Remove</c>.
/// </summary>
internal sealed record AssignmentLibraries
{
    private readonly IReadOnlyList<Guid> _add = [];
    private readonly IReadOnlyList<Guid> _remove = [];

    /// <summary>Gets the mode; <c>Inherit</c> by default.</summary>
    public LenientEnum<AccessRolesUserLibraryMode> Mode { get; init; }

    /// <summary>Gets the libraries added for this user.</summary>
    public IReadOnlyList<Guid> Add
    {
        get => _add;
        init => _add = StoreValues.Values(value);
    }

    /// <summary>Gets the libraries removed for this user; a removal beats every grant.</summary>
    public IReadOnlyList<Guid> Remove
    {
        get => _remove;
        init => _remove = StoreValues.Values(value);
    }

    /// <summary>
    /// Gets a value indicating whether the admin confirmed that this user may see no libraries (mode <c>Only</c> with
    /// nothing added); the only way an empty result is written.
    /// </summary>
    public bool ConfirmNoLibraries { get; init; }

    /// <summary>Gets the fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
