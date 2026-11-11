using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;

namespace Jellyfin.Plugin.TagCloud.Clouds;

/// <summary>
/// The saved clouds' file, <c>data/tagcloud/clouds.json</c>, schema version 1, kept by the kit's
/// <see cref="JsonDocumentStore{TDoc}"/>: a newer version is refused, never overwritten.
/// </summary>
internal sealed record SavedCloudDocument : IVersionedDocument
{
    /// <summary>The schema version this build writes.</summary>
    public const int CurrentVersion = 1;

    /// <inheritdoc />
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Gets the last seen names of the clouds' owners (the kit's users service).</summary>
    public KnownUsers KnownUsers { get; init; } = KnownUsers.Empty;

    /// <summary>Gets the clouds, in the order they were saved.</summary>
    public IReadOnlyList<SavedCloudRecord> Clouds { get; init; } = [];

    /// <summary>Gets fields a newer version wrote, kept as they are.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>
    /// Creates an empty document.
    /// </summary>
    /// <returns>The document.</returns>
    public static SavedCloudDocument CreateEmpty() => new();
}
