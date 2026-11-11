using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// The fields the plugin last wrote to a user's native policy (<c>Ledger.*.LastWrite</c>), which
/// the reconcile compares with the user manager's object (on 10.11.5 a concurrent sign-in can put a
/// stale user back into the cache after a write).
/// </summary>
internal sealed record LedgerWrite
{
    private readonly IReadOnlyList<Guid> _enabledFolders = [];
    private readonly DateTime _utc;

    /// <summary>Gets when the write happened, UTC.</summary>
    public DateTime Utc
    {
        get => _utc;
        init => _utc = StoreValues.Utc(value);
    }

    /// <summary>Gets a value indicating whether the write set <c>EnableAllFolders</c>.</summary>
    public bool EnableAllFolders { get; init; }

    /// <summary>Gets the <c>EnabledFolders</c> written.</summary>
    public IReadOnlyList<Guid> EnabledFolders
    {
        get => _enabledFolders;
        init => _enabledFolders = StoreValues.Values(value);
    }

    /// <summary>
    /// Gets the <c>EnableUserPreferenceAccess</c> written while <c>MirrorPreferenceLockToPolicy</c> is on,
    /// or <c>null</c> when the write left it alone.
    /// </summary>
    public bool? EnableUserPreferenceAccess { get; init; }

    /// <summary>Gets the fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
