using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.TagCloud.Api.Models;

namespace Jellyfin.Plugin.TagCloud.Clouds;

/// <summary>
/// A saved cloud as stored in <c>data/tagcloud/clouds.json</c>: a configuration, never counts.
/// </summary>
internal sealed record SavedCloudRecord
{
    /// <summary>Gets the cloud's id.</summary>
    public Guid Id { get; init; }

    /// <summary>Gets the owner's user id.</summary>
    public Guid OwnerId { get; init; }

    /// <summary>Gets the cloud's name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether everyone may open it.</summary>
    public bool Shared { get; init; }

    /// <summary>Gets a value indicating whether its owner was deleted (a shared cloud kept).</summary>
    public bool OwnerDeleted { get; init; }

    /// <summary>Gets when it was saved first.</summary>
    public DateTime CreatedUtc { get; init; }

    /// <summary>Gets when it was last saved.</summary>
    public DateTime UpdatedUtc { get; init; }

    /// <summary>Gets what it counts.</summary>
    public TagCloudSource Source { get; init; }

    /// <summary>Gets which titles it counts over.</summary>
    public TagCloudScope Scope { get; init; }

    /// <summary>Gets the libraries it counts over; empty for every library.</summary>
    public IReadOnlyList<Guid> LibraryIds { get; init; } = [];

    /// <summary>Gets the word keys it is narrowed to.</summary>
    public IReadOnlyList<string> NarrowKeys { get; init; } = [];

    /// <summary>Gets how it looks.</summary>
    public CloudLook Look { get; init; } = new();

    /// <summary>Gets fields a newer version wrote, kept as they are.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
