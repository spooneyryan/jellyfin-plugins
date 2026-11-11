using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Storage;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>One run in a definition's history.</summary>
internal sealed record AutoCollectionHistoryEntry
{
    /// <summary>Gets when it ran.</summary>
    public DateTime Utc { get; init; }

    /// <summary>Gets what started it.</summary>
    public StoredEnum<SmartSearchAutoCollectionRunReason> Reason { get; init; }

    /// <summary>Gets the members it added.</summary>
    public int Added { get; init; }

    /// <summary>Gets the members it removed.</summary>
    public int Removed { get; init; }

    /// <summary>Gets the dead links it pruned.</summary>
    public int Dead { get; init; }

    /// <summary>Gets how it ended: <c>Written</c>, <c>Unchanged</c>, <c>Guarded</c>, or the status that stopped it.</summary>
    public string Outcome { get; init; } = string.Empty;

    /// <summary>Gets how long it took.</summary>
    public long DurationMs { get; init; }

    /// <summary>Gets how many items added outside the plugin the run kept as pins.</summary>
    public int Pinned { get; init; }

    /// <summary>Gets how many items removed outside the plugin the run kept out as excludes.</summary>
    public int Excluded { get; init; }

    /// <summary>Gets how many edits made outside the plugin the run reverted (Overwrite mode, the server's automatic adds).</summary>
    public int Reverted { get; init; }

    /// <summary>Gets the removed ids of the most recent written change (at most <see cref="AutoCollectionState.MaxRemovedIds"/>); empty on older entries.</summary>
    public IReadOnlyList<Guid> RemovedIds { get; init; } = [];

    /// <summary>Gets fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
