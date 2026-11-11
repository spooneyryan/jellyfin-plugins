using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// A role's reference to a library (<c>Roles[].Libraries.Refs[]</c>): the library's id, with what it was called
/// and where it lived when the admin picked it, so a rename can be re-pointed and a deletion shown.
/// </summary>
internal sealed record LibraryReference
{
    private readonly IReadOnlyList<string> _locations = [];

    /// <summary>Gets the library's id (Jellyfin's <c>CollectionFolder</c> id).</summary>
    public Guid Id { get; init; }

    /// <summary>Gets the library's name when the admin picked it, or <c>null</c>.</summary>
    public string? NameAtSelection { get; init; }

    /// <summary>Gets the library's folders when the admin picked it.</summary>
    public IReadOnlyList<string> Locations
    {
        get => _locations;
        init => _locations = StoreValues.Strings(value);
    }

    /// <summary>
    /// Gets a value indicating whether the library was deleted: the reference stays until the admin purges it.
    /// </summary>
    public bool Orphaned { get; init; }

    /// <summary>Gets the fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
