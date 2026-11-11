using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// The document of <c>auto-collections.json</c>: the definitions, the last seen names of their users, and the position
/// in the move journal up to which pins and excludes were relinked.
/// </summary>
internal sealed record AutoCollectionsDocument : IVersionedDocument
{
    /// <summary>The current schema version.</summary>
    public const int CurrentVersion = 1;

    /// <inheritdoc />
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Gets the last seen name of each user a definition names (its creator, its evaluation user).</summary>
    public KnownUsers KnownUsers { get; init; } = KnownUsers.Empty;

    /// <summary>Gets the move journal the position belongs to; another journal starts over from 0.</summary>
    public Guid? MoveJournalId { get; init; }

    /// <summary>Gets the last move-journal line applied to pins and excludes.</summary>
    public long LastMoveSeq { get; init; }

    /// <summary>Gets the definitions, ordered by creation time and id when written.</summary>
    public IReadOnlyList<AutoCollectionDefinition> Definitions { get; init; } = [];

    /// <summary>Gets document fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>Creates the empty document.</summary>
    /// <returns>The document.</returns>
    public static AutoCollectionsDocument CreateEmpty() => new();
}
