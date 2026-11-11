using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// A user's native access as it was before the plugin first changed it, and the plugin's last write (the ledger,
/// <c>Ledger</c> in the data file): the starting set of per-user overrides without a library role, and the target of
/// <c>onUnmanage=Restore</c>, <c>Unmanage</c> and <c>ReleaseLocks</c>.
/// </summary>
/// <remarks>
/// Recorded when a save first makes the user's libraries managed, and kept while the user has an assignment, also while
/// their libraries aren't managed (<c>Excluded</c>, a role switched to <c>NotManaged</c>, a lock-only role: a pause), so a
/// user managed again keeps their real original access after the plugin wrote theirs. It goes with the assignment
/// (<c>Unmanage</c>, the last role removed) unless a <see cref="Release"/> is still to be written, and when the user is
/// deleted.
/// <para>A user given the default roles at their creation (<see cref="DefaultRolesUtc"/>) has <b>no libraries</b> as their
/// original access, whatever was captured: the access the server gave them was its default
/// for a new user (<c>EnableAllFolders=true</c>), nothing an admin chose, so <c>Restore</c> (and the starting set of
/// per-user overrides without a library role) never widens it. This holds when read, so an entry a 0.9.4.0 build wrote
/// with every library reads as none too, and is saved that way at the next change.</para>
/// </remarks>
internal sealed record LedgerEntry
{
    private readonly IReadOnlyList<Guid> _originalEnabledFolders = [];
    private readonly bool _originalEnableAllFolders;
    private readonly DateTime _capturedUtc;
    private readonly DateTime? _defaultRolesUtc;

    /// <summary>
    /// Gets a value indicating whether the user's native <c>EnableAllFolders</c> was on when it was captured; always
    /// <c>false</c> for a user given the default roles at their creation (<see cref="DefaultRolesUtc"/>).
    /// </summary>
    public bool OriginalEnableAllFolders
    {
        get => _originalEnableAllFolders && _defaultRolesUtc is null;
        init => _originalEnableAllFolders = value;
    }

    /// <summary>
    /// Gets the user's native <c>EnabledFolders</c> when it was captured; always empty for a user given the default roles at
    /// their creation (<see cref="DefaultRolesUtc"/>).
    /// </summary>
    public IReadOnlyList<Guid> OriginalEnabledFolders
    {
        get => _defaultRolesUtc is null ? _originalEnabledFolders : [];
        init => _originalEnabledFolders = StoreValues.Values(value);
    }

    /// <summary>Gets a value indicating whether the user's native <c>EnableUserPreferenceAccess</c> was on when it was captured.</summary>
    public bool OriginalEnableUserPreferenceAccess { get; init; }

    /// <summary>Gets when the original values were captured, UTC.</summary>
    public DateTime CapturedUtc
    {
        get => _capturedUtc;
        init => _capturedUtc = StoreValues.Utc(value);
    }

    /// <summary>Gets the plugin's last write of the user's native policy, or <c>null</c> before the first.</summary>
    public LedgerWrite? LastWrite { get; init; }

    /// <summary>
    /// Gets the user's departure from library management whose write is still to come, or
    /// <c>null</c> while the user is managed or there is nothing to write.
    /// </summary>
    public LedgerRelease? Release { get; init; }

    /// <summary>
    /// Gets when the user got the default roles at their creation (added to schema 1 in 0.9.4), UTC, or
    /// <c>null</c>. Until the plugin's first write (<see cref="LastWrite"/>) the user's native access is the server's
    /// default for a new user, nothing an admin gave them: when their roles give no library, the empty result is written
    /// instead of refused (fail closed), as for a user whom library deletions left nothing. Their original access is none
    /// (<see cref="OriginalEnableAllFolders"/>, <see cref="OriginalEnabledFolders"/>).
    /// </summary>
    public DateTime? DefaultRolesUtc
    {
        get => _defaultRolesUtc;
        init => _defaultRolesUtc = value is { } time ? StoreValues.Utc(time) : null;
    }

    /// <summary>Gets the fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
