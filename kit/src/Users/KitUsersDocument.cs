using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Storage;

namespace Jellyfin.Plugin.Kit.Users;

/// <summary>
/// The kit's own small store in the plugin's data folder, <c>kit-users.json</c>: the orphan marks of
/// <see cref="UserReconciler"/>. Written only when a mark changes, so a plugin without orphans never has the file.
/// </summary>
internal sealed record KitUsersDocument : IVersionedDocument
{
    /// <summary>The file's name in the plugin's data folder.</summary>
    public const string FileName = "kit-users.json";

    /// <summary>The current schema version.</summary>
    public const int CurrentVersion = 1;

    /// <inheritdoc />
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Gets the marked user ids.</summary>
    public IReadOnlyList<OrphanMark> Orphans { get; init; } = [];

    /// <summary>Gets fields of a newer kit, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>
    /// Creates the empty document.
    /// </summary>
    /// <returns>The document.</returns>
    public static KitUsersDocument CreateEmpty() => new();
}
