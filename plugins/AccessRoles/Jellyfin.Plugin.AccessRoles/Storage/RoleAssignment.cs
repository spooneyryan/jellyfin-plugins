using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// One user's roles and overrides (<c>Assignments[]</c>): the user is managed while the assignment exists
/// and its state is <c>Managed</c>. A user without one is unmanaged.
/// </summary>
internal sealed record RoleAssignment
{
    private readonly IReadOnlyList<Guid> _roleIds = [];
    private readonly IReadOnlyDictionary<LenientEnum<AccessRolesLock>, LenientEnum<AccessRolesLockOverride>> _lockOverrides =
        ImmutableDictionary<LenientEnum<AccessRolesLock>, LenientEnum<AccessRolesLockOverride>>.Empty;

    private readonly AssignmentLibraries _libraries = new();
    private readonly DateTime _updatedUtc;
    private readonly DateTime? _confirmedNoFullAdminUtc;

    /// <summary>Gets the user's id.</summary>
    public Guid UserId { get; init; }

    /// <summary>Gets the ids of the user's roles, in the order the admin gave them (locks accumulate, libraries add up).</summary>
    public IReadOnlyList<Guid> RoleIds
    {
        get => _roleIds;
        init => _roleIds = StoreValues.Values(value);
    }

    /// <summary>
    /// Gets the user's lock overrides by lock (<c>Inherit</c>, <c>Lock</c> or <c>Unlock</c>; per-user overrides
    /// win). Locks and overrides this build doesn't know are kept.
    /// </summary>
    public IReadOnlyDictionary<LenientEnum<AccessRolesLock>, LenientEnum<AccessRolesLockOverride>> LockOverrides
    {
        get => _lockOverrides;
        init => _lockOverrides = StoreValues.Map(value);
    }

    /// <summary>Gets the user's own library mode and lists.</summary>
    public AssignmentLibraries Libraries
    {
        get => _libraries;
        init => _libraries = value ?? new AssignmentLibraries();
    }

    /// <summary>Gets whether the user is managed; <c>Managed</c> by default.</summary>
    public LenientEnum<AccessRolesAssignmentState> State { get; init; }

    /// <summary>Gets when the assignment was last changed, UTC.</summary>
    public DateTime UpdatedUtc
    {
        get => _updatedUtc;
        init => _updatedUtc = StoreValues.Utc(value);
    }

    /// <summary>
    /// Gets when an admin confirmed (<c>confirmNoFullAdmin=true</c>) a change that left no enabled administrator with every
    /// library while this administrator's libraries are managed, UTC, or <c>null</c> (the lockout guard, <c>LockoutGuard</c>):
    /// the library sync writes such an administrator's narrower access only with this confirmation on record. It is not part
    /// of what the assignment says (<c>ChangeAnalysis.SameAssignment</c> leaves it out) and goes with the assignment.
    /// </summary>
    public DateTime? ConfirmedNoFullAdminUtc
    {
        get => _confirmedNoFullAdminUtc;
        init => _confirmedNoFullAdminUtc = value is { } time ? StoreValues.Utc(time) : null;
    }

    /// <summary>Gets the fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>
    /// Gets a value indicating whether the state or the library mode is one this build doesn't know (a newer build wrote
    /// it): the user then counts as managed with every settings section locked, and nothing is written to their native
    /// policy until the matching version is installed.
    /// </summary>
    [JsonIgnore]
    public bool HasUnknownValues => !State.IsKnown || !Libraries.Mode.IsKnown;
}
