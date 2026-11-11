using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// A user's native library fields at one moment (<c>EnableAllFolders</c> and <c>EnabledFolders</c>): the baseline a pending
/// release was computed against (<see cref="LedgerRelease.Baseline"/>).
/// </summary>
internal sealed record LedgerAccess
{
    private readonly IReadOnlyList<Guid> _enabledFolders = [];

    /// <summary>Gets a value indicating whether every library was granted, future ones included.</summary>
    public bool EnableAllFolders { get; init; }

    /// <summary>Gets the granted libraries when <see cref="EnableAllFolders"/> is off (empty when it is on).</summary>
    public IReadOnlyList<Guid> EnabledFolders
    {
        get => _enabledFolders;
        init => _enabledFolders = StoreValues.Values(value);
    }

    /// <summary>Gets the fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
