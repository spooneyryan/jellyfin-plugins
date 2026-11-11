using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// The store document of the developer user data demo, <c>userdata-demo.json</c>: records owned by users and
/// the <see cref="Kit.Users.KnownUsers"/> map every plugin store with user data keeps.
/// </summary>
internal sealed record UserDataDemoDocument : IVersionedDocument
{
    /// <summary>The current schema version.</summary>
    public const int CurrentVersion = 1;

    /// <inheritdoc />
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Gets the last seen name of each user the records belong to.</summary>
    public KnownUsers KnownUsers { get; init; } = KnownUsers.Empty;

    /// <summary>Gets the records, ordered by creation time and id when written.</summary>
    public IReadOnlyList<UserDataDemoRecord> Records { get; init; } = [];

    /// <summary>Gets document fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>
    /// Creates the empty document.
    /// </summary>
    /// <returns>The document.</returns>
    public static UserDataDemoDocument CreateEmpty() => new();
}
