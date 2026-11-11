using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// A record of the developer user data demo: owned by a user, like a bookmark, with <see cref="HasFile"/>
/// standing in for a clip file, which <c>KeepAsOrphan</c> keeps as a tombstone.
/// </summary>
internal sealed record UserDataDemoRecord
{
    /// <summary>Gets the record id.</summary>
    public Guid Id { get; init; }

    /// <summary>Gets the owner.</summary>
    public Guid UserId { get; init; }

    /// <summary>Gets the text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the record stands for one with a file (a bookmark with a clip).</summary>
    public bool HasFile { get; init; }

    /// <summary>Gets when the owner was deleted and the record kept as a tombstone, or <c>null</c>.</summary>
    public DateTime? DeletedUtc { get; init; }

    /// <summary>Gets when the record was created.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Gets when the record was last changed.</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>Gets fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
