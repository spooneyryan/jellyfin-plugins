using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// A user's departure from library management whose native write is still to come (the ledger's
/// <c>Ledger.*.Release</c>, added to schema 1 in 0.9.2): the admin's <c>onUnmanage</c> choice, the library access it gives,
/// and the user's native access it was computed against (<see cref="Baseline"/>), for the library
/// sync to write through <c>PolicyWriter</c>.
/// </summary>
/// <remarks>
/// <para>Only <c>Restore</c> and <c>GrantAll</c> leave one, and only when the access they give differs from the user's
/// access at the time; <c>Keep</c>, or an access that already matches, needs no write. A user managed again before the write
/// loses the release and keeps the entry's original access. The entry itself stays while the user has an assignment
/// (<c>Excluded</c>, a role without libraries: a pause) and goes with the assignment.</para>
/// <para><b>The contract for the library sync.</b> Before writing a release, the sync compares the user's native
/// <c>EnableAllFolders</c>/<c>EnabledFolders</c> as the server enforces them with <see cref="Baseline"/>:</para>
/// <list type="bullet">
/// <item>they match: it writes <see cref="EnableAllFolders"/>/<see cref="EnabledFolders"/> (a <c>Restore</c> filtered again
/// against the libraries then), audits <c>LibraryAccessRestored</c>, and drops the release;</item>
/// <item>they differ (an admin changed the unmanaged user's access natively since the choice), or there is no
/// <see cref="Baseline"/> (a release a 0.9.2.0 build wrote): it writes nothing, drops the release and audits that it did,
/// with the baseline and the access it found, so an admin's own edit is never overwritten;</item>
/// <item>the native access already is the release's: it drops the release without a write.</item>
/// </list>
/// <para>Dropping the release drops the whole entry when the user has no assignment left, and only the release otherwise.
/// Effective shows a pending release (<c>Libraries.PendingRelease</c>, with <c>BaselineMatches</c>), the Users list counts it
/// as a pending write while its baseline matches, and Diagnostics counts releases (<c>Records.PendingReleases</c>).</para>
/// </remarks>
internal sealed record LedgerRelease
{
    private readonly IReadOnlyList<Guid> _enabledFolders = [];
    private readonly DateTime _requestedUtc;

    /// <summary>Gets the admin's choice.</summary>
    public LenientEnum<AccessRolesUnmanageChoice> Choice { get; init; }

    /// <summary>Gets when the user left management, UTC.</summary>
    public DateTime RequestedUtc
    {
        get => _requestedUtc;
        init => _requestedUtc = StoreValues.Utc(value);
    }

    /// <summary>Gets a value indicating whether the access to write is every library, future ones included.</summary>
    public bool EnableAllFolders { get; init; }

    /// <summary>Gets the libraries to write when <see cref="EnableAllFolders"/> is off.</summary>
    public IReadOnlyList<Guid> EnabledFolders
    {
        get => _enabledFolders;
        init => _enabledFolders = StoreValues.Values(value);
    }

    /// <summary>
    /// Gets the user's native library access when the choice was made, which the release was computed against; <c>null</c>
    /// in a release a 0.9.2.0 build wrote. The sync writes the release only while the native access still matches it.
    /// </summary>
    public LedgerAccess? Baseline { get; init; }

    /// <summary>Gets the fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
