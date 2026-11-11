using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Storage;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// The document of <c>auto-collections-state.json</c>: each definition's run state and history. Rebuildable: every run
/// works from the definitions and the live collection, never from this file, so a lost file only loses the history.
/// </summary>
internal sealed record AutoCollectionsStateDocument : IVersionedDocument
{
    /// <summary>The current schema version.</summary>
    public const int CurrentVersion = 1;

    /// <inheritdoc />
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Gets the states by definition id.</summary>
    public IReadOnlyDictionary<Guid, AutoCollectionState> States { get; init; } = new Dictionary<Guid, AutoCollectionState>();

    /// <summary>Gets document fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>Creates the empty document.</summary>
    /// <returns>The document.</returns>
    public static AutoCollectionsStateDocument CreateEmpty() => new();
}
