using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// A library as the plugin's catalog last saw it (<c>KnownLibraries[]</c>).
/// </summary>
internal sealed record KnownLibrary
{
    private readonly string _name = string.Empty;
    private readonly IReadOnlyList<string> _locations = [];
    private readonly DateTime _firstSeenUtc;
    private readonly DateTime? _createdUtc;
    private readonly DateTime? _recreatedUtc;

    /// <summary>Gets the library's id (Jellyfin's <c>CollectionFolder</c> id).</summary>
    public Guid Id { get; init; }

    /// <summary>Gets the library's name when last seen.</summary>
    public string Name
    {
        get => _name;
        init => _name = value ?? string.Empty;
    }

    /// <summary>Gets the library's folders when last seen.</summary>
    public IReadOnlyList<string> Locations
    {
        get => _locations;
        init => _locations = StoreValues.Strings(value);
    }

    /// <summary>Gets a value indicating whether the library's native "Enable the library" switch was on when last seen.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Gets when the library was created, UTC, or <c>null</c> when unknown: the creation time of its
    /// <c>root\default\&lt;Name&gt;</c> folder, not <c>CollectionFolder.DateCreated</c>, which a library deleted and created
    /// again with the same name keeps (measured on 10.11.5). "Same id, newer created time" is
    /// a new library.
    /// </summary>
    public DateTime? CreatedUtc
    {
        get => _createdUtc;
        init => _createdUtc = value is { } time ? StoreValues.Utc(time) : null;
    }

    /// <summary>Gets when the catalog first saw the library, UTC.</summary>
    public DateTime FirstSeenUtc
    {
        get => _firstSeenUtc;
        init => _firstSeenUtc = StoreValues.Utc(value);
    }

    /// <summary>Gets the library's state; <c>Active</c> by default.</summary>
    public LenientEnum<AccessRolesLibraryState> State { get; init; }

    /// <summary>
    /// Gets when the catalog found that the library was deleted and created again under the same id (a folder newer
    /// than <see cref="CreatedUtc"/>, or an <c>Orphaned</c> library listed again), UTC, or <c>null</c>. Such a library is new
    /// as far as access goes: no <c>Only</c> reference, <c>Add</c> entry or original access grants it until the admin grants
    /// it again, which clears this and records the folder's created time.
    /// </summary>
    public DateTime? RecreatedUtc
    {
        get => _recreatedUtc;
        init => _recreatedUtc = value is { } time ? StoreValues.Utc(time) : null;
    }

    /// <summary>Gets the fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
