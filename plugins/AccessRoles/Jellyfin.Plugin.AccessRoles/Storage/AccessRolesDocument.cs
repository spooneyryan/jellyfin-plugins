using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// The on-disk shape of <c>data/accessroles/accessroles.json</c>, schema version 1, kept by the kit's
/// <see cref="JsonDocumentStore{TDoc}"/>, so instances are immutable snapshots changed through
/// <see cref="AccessRolesStore"/>.
/// </summary>
/// <remarks>
/// <para>Properties are written in the order declared here, nulls omitted. Every collection reads as empty
/// when the file has none or <c>null</c>, and <c>null</c> entries are dropped, so code never meets a null. Fields this
/// version doesn't know are kept, on the document and on every record; enum values are kept by name, known or
/// not (<see cref="LenientEnum{TEnum}"/>).</para>
/// <para>Roles and assignments live here, not in the plugin's XML settings, so the generic
/// <c>POST /Plugins/{id}/Configuration</c> can't bypass the lockout checks.</para>
/// </remarks>
internal sealed record AccessRolesDocument : IVersionedDocument
{
    /// <summary>The current schema version.</summary>
    public const int CurrentVersion = 1;

    private readonly IReadOnlyList<AccessRole> _roles = [];
    private readonly IReadOnlyList<RoleAssignment> _assignments = [];
    private readonly IReadOnlyList<Guid> _defaultRoleIds = [];
    private readonly IReadOnlyList<KnownLibrary> _knownLibraries = [];
    private readonly IReadOnlyList<PendingRename> _pendingRenames = [];
    private readonly IReadOnlyDictionary<Guid, LedgerEntry> _ledger = ImmutableDictionary<Guid, LedgerEntry>.Empty;
    private readonly IReadOnlyDictionary<Guid, MirroredPreference> _preferenceMirror = ImmutableDictionary<Guid, MirroredPreference>.Empty;
    private readonly KnownUsers _knownUsers = KnownUsers.Empty;

    /// <summary>Gets the schema version the file was written with.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Gets the roles.</summary>
    public IReadOnlyList<AccessRole> Roles
    {
        get => _roles;
        init => _roles = StoreValues.Records(value);
    }

    /// <summary>Gets the assignments, one per managed (or excluded) user.</summary>
    public IReadOnlyList<RoleAssignment> Assignments
    {
        get => _assignments;
        init => _assignments = StoreValues.Records(value);
    }

    /// <summary>Gets the roles a new user gets.</summary>
    public IReadOnlyList<Guid> DefaultRoleIds
    {
        get => _defaultRoleIds;
        init => _defaultRoleIds = StoreValues.Values(value);
    }

    /// <summary>Gets the libraries as the catalog last saw them.</summary>
    public IReadOnlyList<KnownLibrary> KnownLibraries
    {
        get => _knownLibraries;
        init => _knownLibraries = StoreValues.Records(value);
    }

    /// <summary>Gets the library renames waiting for Jellyfin to finish them.</summary>
    public IReadOnlyList<PendingRename> PendingRenames
    {
        get => _pendingRenames;
        init => _pendingRenames = StoreValues.Records(value);
    }

    /// <summary>Gets each managed user's original native access and the plugin's last write, by user id.</summary>
    [JsonConverter(typeof(UserIdMapJsonConverter<LedgerEntry>))]
    public IReadOnlyDictionary<Guid, LedgerEntry> Ledger
    {
        get => _ledger;
        init => _ledger = StoreValues.Map(value);
    }

    /// <summary>
    /// Gets the native <c>EnableUserPreferenceAccess</c> flags the optional mirror turned off, by user id
    /// (<c>MirrorPreferenceLockToPolicy</c>; added to schema 1 in 0.9.13): the flags it may turn back on.
    /// </summary>
    [JsonConverter(typeof(UserIdMapJsonConverter<MirroredPreference>))]
    public IReadOnlyDictionary<Guid, MirroredPreference> PreferenceMirror
    {
        get => _preferenceMirror;
        init => _preferenceMirror = StoreValues.Map(value);
    }

    /// <summary>
    /// Gets the last seen name of each user the store holds records of: what <i>Remap by name</i> and an
    /// export's <c>Users[]</c> use.
    /// </summary>
    public KnownUsers KnownUsers
    {
        get => _knownUsers;
        init => _knownUsers = value ?? KnownUsers.Empty;
    }

    /// <summary>Gets the fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>
    /// Creates an empty document.
    /// </summary>
    /// <returns>The document.</returns>
    public static AccessRolesDocument CreateEmpty() => new();
}
