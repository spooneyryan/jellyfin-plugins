using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// A library rename the filter saw that Jellyfin hasn't finished (<c>PendingRenames[]</c>): roles
/// and overrides are re-pointed once a library folder with the new name <b>and an item id</b> appears, which takes about
/// 140 ms after the dashboard's request and, with <c>refreshLibrary=false</c>, waits for the next library scan (as
/// measured on 10.11.5).
/// </summary>
internal sealed record PendingRename
{
    private readonly string _oldName = string.Empty;
    private readonly string _newName = string.Empty;
    private readonly DateTime _requestedUtc;

    /// <summary>Gets the renamed library's id before the rename (a rename gives a library a new id).</summary>
    public Guid OldId { get; init; }

    /// <summary>Gets the library's name before the rename.</summary>
    public string OldName
    {
        get => _oldName;
        init => _oldName = value ?? string.Empty;
    }

    /// <summary>Gets the library's new name.</summary>
    public string NewName
    {
        get => _newName;
        init => _newName = value ?? string.Empty;
    }

    /// <summary>Gets when the filter saw the rename, UTC.</summary>
    public DateTime RequestedUtc
    {
        get => _requestedUtc;
        init => _requestedUtc = StoreValues.Utc(value);
    }

    /// <summary>Gets the fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
