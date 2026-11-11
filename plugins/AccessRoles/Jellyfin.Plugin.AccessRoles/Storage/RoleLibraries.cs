using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// A role's library grant ("library modes per role", <c>Roles[].Libraries</c>).
/// </summary>
internal sealed record RoleLibraries
{
    private readonly IReadOnlyList<LibraryReference> _refs = [];

    /// <summary>Gets the mode; <c>NotManaged</c> by default.</summary>
    public LenientEnum<AccessRolesRoleLibraryMode> Mode { get; init; }

    /// <summary>Gets the libraries <c>Only</c> grants or <c>AllExcept</c> leaves out; unused by the other modes.</summary>
    public IReadOnlyList<LibraryReference> Refs
    {
        get => _refs;
        init => _refs = StoreValues.Records(value);
    }

    /// <summary>Gets the fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
