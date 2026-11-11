using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// What a query searches: its item kinds, its libraries (none means every library the user can
/// open) and whether missing episodes count.
/// </summary>
public sealed class SmartSearchQueryScope
{
    /// <summary>Gets or sets the item kinds, at least one of Movie, Series, Episode, MusicVideo, Video and BoxSet.</summary>
    [SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "A query the JSON reader fills and the engine reads.")]
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "A query the JSON reader fills.")]
    public List<BaseItemKind> ItemKinds { get; set; } = [];

    /// <summary>Gets or sets the libraries; empty means every library the user can open.</summary>
    [SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "A query the JSON reader fills and the engine reads.")]
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "A query the JSON reader fills.")]
    public List<Guid> LibraryIds { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether missing (virtual) episodes are searched too.</summary>
    public bool IncludeMissing { get; set; }

    /// <summary>Gets or sets the properties this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "A query the JSON reader fills.")]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
