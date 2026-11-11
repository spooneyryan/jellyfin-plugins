using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// A role (<c>Roles[]</c> in the data file): a named bundle of locks and library grants that users are assigned.
/// </summary>
internal sealed record AccessRole
{
    private readonly string _name = string.Empty;
    private readonly IReadOnlyList<LenientEnum<AccessRolesLock>> _locks = [];
    private readonly RoleLibraries _libraries = new();
    private readonly DateTime _createdUtc;
    private readonly DateTime _updatedUtc;

    /// <summary>Gets the role's id.</summary>
    public Guid Id { get; init; }

    /// <summary>Gets the role's name, unique among roles (the API compares it ignoring case).</summary>
    public string Name
    {
        get => _name;
        init => _name = value ?? string.Empty;
    }

    /// <summary>Gets the admin's description, or <c>null</c>.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the locks: a section or surface is locked for a member if any of the member's roles locks it. Names
    /// this build doesn't know are kept and ignored.
    /// </summary>
    public IReadOnlyList<LenientEnum<AccessRolesLock>> Locks
    {
        get => _locks;
        init => _locks = StoreValues.Values(value);
    }

    /// <summary>Gets the role's library grant.</summary>
    public RoleLibraries Libraries
    {
        get => _libraries;
        init => _libraries = value ?? new RoleLibraries();
    }

    /// <summary>Gets when the role was created, UTC.</summary>
    public DateTime CreatedUtc
    {
        get => _createdUtc;
        init => _createdUtc = StoreValues.Utc(value);
    }

    /// <summary>Gets when the role was last changed, UTC; the API's <c>IfUpdatedAt</c> compares it.</summary>
    public DateTime UpdatedUtc
    {
        get => _updatedUtc;
        init => _updatedUtc = StoreValues.Utc(value);
    }

    /// <summary>Gets the fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>
    /// Gets a value indicating whether the library mode is one this build doesn't know (a newer build wrote it): its
    /// members count as managed with every settings section locked, and nothing is written to their native policy.
    /// </summary>
    [JsonIgnore]
    public bool HasUnknownLibraryMode => !Libraries.Mode.IsKnown;

    /// <summary>Gets the known locks, without duplicates; unknown names are left out.</summary>
    [JsonIgnore]
    public IReadOnlyList<AccessRolesLock> KnownLocks => Locks.Where(l => l.IsKnown).Select(l => l.Value!.Value).Distinct().ToList();
}
