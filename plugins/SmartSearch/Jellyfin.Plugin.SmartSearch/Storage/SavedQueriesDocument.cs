using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;

namespace Jellyfin.Plugin.SmartSearch.Storage;

/// <summary>
/// The document of <c>saved-queries.json</c>: the saved queries and the last seen names of their owners.
/// </summary>
internal sealed record SavedQueriesDocument : IVersionedDocument
{
    /// <summary>The current schema version.</summary>
    public const int CurrentVersion = 1;

    /// <inheritdoc />
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Gets the last seen name of each user who owns a query.</summary>
    public KnownUsers KnownUsers { get; init; } = KnownUsers.Empty;

    /// <summary>Gets the queries, ordered by creation time and id when written.</summary>
    public IReadOnlyList<SavedQueryRecord> Queries { get; init; } = [];

    /// <summary>Gets document fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>Creates the empty document.</summary>
    /// <returns>The document.</returns>
    public static SavedQueriesDocument CreateEmpty() => new();
}
