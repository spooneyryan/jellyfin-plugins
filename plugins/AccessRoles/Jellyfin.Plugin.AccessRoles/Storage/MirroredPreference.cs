using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// A user's native <c>EnableUserPreferenceAccess</c> that the optional mirror turned off
/// (<c>MirrorPreferenceLockToPolicy</c>; <c>PreferenceMirror</c>, added to schema 1 in 0.9.13): what it was,
/// and when the mirror wrote it. The entry is the mirror's ownership of the flag: only a flag with an entry is ever turned
/// back on (when the five server-stored sections are no longer all locked, the setting is off, <c>ReleaseLocks</c>, the
/// plugin's uninstall), and only while it is still off; a flag someone else turned off is never touched.
/// </summary>
/// <remarks>
/// Kept apart from the <see cref="LedgerEntry"/>: a ledger entry follows library management and the user's assignment (it
/// goes with <c>Unmanage</c>), while the mirror applies to users whose libraries Access Roles may not manage at all, and its
/// flag must be put back after they leave management. The entry goes when the flag is back on, when the user is deleted,
/// and with a remap (the new id's flag is the mirror's to look at again, never to turn on).
/// </remarks>
internal sealed record MirroredPreference
{
    private readonly DateTime _writtenUtc;

    /// <summary>Gets a value indicating whether the flag was on before the mirror turned it off (always, as written).</summary>
    public bool OriginalEnableUserPreferenceAccess { get; init; } = true;

    /// <summary>Gets when the mirror turned the flag off, UTC.</summary>
    public DateTime WrittenUtc
    {
        get => _writtenUtc;
        init => _writtenUtc = StoreValues.Utc(value);
    }

    /// <summary>Gets the fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
