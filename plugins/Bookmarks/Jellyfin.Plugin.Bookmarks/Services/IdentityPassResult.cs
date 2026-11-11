using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// What one pass of <see cref="BookmarkIdentityService"/> did (the backfill, a refresh, a move-journal catch-up), for its
/// Diagnostics entry <c>Identity</c> and the tests: counts only.
/// </summary>
internal sealed record IdentityPassResult
{
    /// <summary>Gets the pass: <c>Backfill</c>, <c>Refresh</c> or <c>CatchUp</c>.</summary>
    public required string Pass { get; init; }

    /// <summary>Gets when it started (UTC).</summary>
    public DateTime StartedUtc { get; init; }

    /// <summary>Gets why it did nothing (<c>ReadOnly</c>), or null when it ran.</summary>
    public string? Skipped { get; init; }

    /// <summary>Gets how many items it looked up, or journal lines it read.</summary>
    public int Checked { get; init; }

    /// <summary>Gets how many bookmarks it changed.</summary>
    public int Updated { get; init; }

    /// <summary>Gets how many bookmarks a catch-up moved to another item.</summary>
    public int Relinked { get; init; }

    /// <summary>Gets how many items the library didn't have.</summary>
    public int Missing { get; init; }

    /// <summary>Gets how many lookups threw.</summary>
    public int Errors { get; init; }

    /// <summary>Gets the move journal's status, for a catch-up.</summary>
    public string? JournalStatus { get; init; }

    /// <summary>Gets how long it took.</summary>
    public long Milliseconds { get; init; }

    /// <summary>
    /// A pass that did nothing.
    /// </summary>
    /// <param name="pass">The pass.</param>
    /// <param name="reason">Why.</param>
    /// <param name="utc">When.</param>
    /// <returns>The result.</returns>
    public static IdentityPassResult Skip(string pass, string reason, DateTime utc) => new() { Pass = pass, Skipped = reason, StartedUtc = utc };

    /// <summary>
    /// Describes the pass for Diagnostics.
    /// </summary>
    /// <returns>A plain dictionary.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Pass"] = Pass,
        ["StartedUtc"] = StartedUtc,
        ["Skipped"] = Skipped,
        ["Checked"] = Checked,
        ["Updated"] = Updated,
        ["Relinked"] = Relinked,
        ["Missing"] = Missing,
        ["Errors"] = Errors,
        ["JournalStatus"] = JournalStatus,
        ["Milliseconds"] = Milliseconds
    };
}
