using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartSearch.Storage;

/// <summary>
/// One saved query of <c>saved-queries.json</c>: its owner, name, sharing and the query itself.
/// </summary>
/// <remarks>
/// <see cref="Query"/> is kept as JSON, not as the model: a query a newer Smart Search wrote (a newer version, an
/// unknown field or operator) round-trips as it is, and is only marked unsupported when read
/// (<see cref="SavedQueryFacts"/>). A query this build saved is the compact form <c>QueryJson.Write</c> gives. Times are
/// read and written as UTC (<see cref="UtcDateTimeConverter"/>).
/// </remarks>
internal sealed record SavedQueryRecord
{
    /// <summary>Gets the query's id.</summary>
    public Guid Id { get; init; }

    /// <summary>Gets the id of the user who owns it; a deleted user's id while it is kept as an orphan.</summary>
    public Guid OwnerUserId { get; init; }

    /// <summary>Gets the name: 1 to 100 characters, trimmed, unique among the owner's queries ignoring case.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the description: plain text of at most 500 characters, empty for none.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether every user may open and run it (each as themselves).</summary>
    public bool Shared { get; init; }

    /// <summary>Gets the query's JSON (a <c>SmartSearchQueryDefinition</c> object).</summary>
    public JsonElement Query { get; init; }

    /// <summary>Gets the canonical hash of the query when it was saved (<c>sha256:…</c>); <c>null</c> for one this build can't read.</summary>
    public string? QueryHash { get; init; }

    /// <summary>Gets when it was created.</summary>
    [JsonConverter(typeof(UtcDateTimeConverter))]
    public DateTime CreatedUtc { get; init; }

    /// <summary>Gets when it was last changed (the value a <c>PUT</c> sends back as <c>IfUpdatedAt</c>).</summary>
    [JsonConverter(typeof(UtcDateTimeConverter))]
    public DateTime UpdatedUtc { get; init; }

    /// <summary>Gets who changed it last: the owner, or an administrator.</summary>
    public Guid UpdatedByUserId { get; init; }

    /// <summary>Gets when its owner last ran it, written at most once an hour; <c>null</c> when never.</summary>
    [JsonConverter(typeof(UtcDateTimeConverter))]
    public DateTime? LastRunUtc { get; init; }

    /// <summary>Gets fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
