using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Storage;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// The developer library demo's store document, <c>data/bookmarks/library-demo.json</c>.
/// </summary>
internal sealed record LibraryDemoDocument : IVersionedDocument
{
    /// <summary>The schema version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Gets the schema version.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Gets the records, ordered by creation time and id.</summary>
    public IReadOnlyList<LibraryDemoRecord> Records { get; init; } = [];

    /// <summary>Gets the fields this version doesn't know.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>
    /// Creates an empty document.
    /// </summary>
    /// <returns>The document.</returns>
    public static LibraryDemoDocument CreateEmpty() => new();
}
